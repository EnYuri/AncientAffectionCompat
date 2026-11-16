using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AncientAffectionCompat.Patching;

public sealed record AsyncCopies(byte[] AssemblyBytes, Dictionary<int, string> OuterMethods, HashSet<int> MoveNextMethods, HashSet<int> SynchronousMethods);

// Build replacement methods and the affected async state machines. Their owner fields
// still refer to the ORIGINAL relic/player types; no relic models or assets are copied.
public static class AsyncCopyBuilder
{
    public static AsyncCopies Build(byte[] rewrittenBytes, RuntimePatchPlan plan)
    {
        using var input = new MemoryStream(rewrittenBytes);
        using var source = ModuleDefinition.ReadModule(input);
        var output = source;
        var originalScope = new AssemblyNameReference(source.Assembly.Name.Name, source.Assembly.Name.Version);
        output.AssemblyReferences.Add(originalScope);
        var methodsByName = Flatten(source.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
        var selected = plan.ApiCalls.Select(c => methodsByName[c.MethodName])
            .Where(m => m.Name == "MoveNext" && m.DeclaringType.Interfaces.Any(i => i.InterfaceType.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine"))
            .Distinct().ToArray();
        var synchronous = plan.ApiCalls.Select(c => methodsByName[c.MethodName]).Except(selected).Distinct().ToArray();
        var syncByName = synchronous.ToDictionary(m => m.FullName);
        var states = selected.Select(m => m.DeclaringType).ToHashSet();
        var allTypes = Flatten(source.Types).ToArray();
        var owners = selected.ToDictionary(m => m.DeclaringType, m => allTypes.SelectMany(t => t.Methods)
            .Single(candidate => candidate.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute" &&
                ((TypeReference)a.ConstructorArguments[0].Value).FullName == m.DeclaringType.FullName)));
        var originalNames = allTypes.ToDictionary(t => t, t => (t.Namespace, t.Name, t.DeclaringType));
        var outerNames = new Dictionary<int, string>();
        var moveNextTokens = selected.Select(m => plan.OriginalMethodTokens[m.FullName]).ToHashSet();
        var host = new TypeDefinition("AncientAffectionCompat.Generated", "AsyncMethods",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, output.TypeSystem.Object);
        output.Types.Add(host);

        TypeReference MapType(TypeReference type)
        {
            if (type is GenericInstanceType generic)
            {
                var copy = new GenericInstanceType(MapType(generic.ElementType));
                foreach (var arg in generic.GenericArguments) copy.GenericArguments.Add(MapType(arg));
                return copy;
            }
            if (type is ByReferenceType byref) return new ByReferenceType(MapType(byref.ElementType));
            if (type is ArrayType array) return new ArrayType(MapType(array.ElementType), array.Rank);
            if (type is PointerType pointer) return new PointerType(MapType(pointer.ElementType));
            if (type is GenericParameter) return type;
            var definition = type as TypeDefinition;
            if (definition == null && type.Scope == source) definition = allTypes.FirstOrDefault(t => t.FullName == type.FullName);
            if (definition != null && states.Contains(definition)) return definition;
            if (definition != null)
            {
                var (ns, name, declaring) = originalNames[definition];
                return new TypeReference(ns, name, output, originalScope, definition.IsValueType)
                { DeclaringType = declaring == null ? null : MapType(declaring) };
            }
            return output.ImportReference(type);
        }
        MethodReference MapMethod(MethodReference method)
        {
            if (syncByName.TryGetValue(method.FullName, out var safeMethod)) return safeMethod;
            if (method is GenericInstanceMethod generic)
            {
                var copy = new GenericInstanceMethod(MapMethod(generic.ElementMethod));
                foreach (var arg in generic.GenericArguments) copy.GenericArguments.Add(MapType(arg));
                return copy;
            }
            if (method is MethodDefinition def && states.Contains(def.DeclaringType)) return def;
            var reference = new MethodReference(method.Name, MapType(method.ReturnType), MapType(method.DeclaringType))
            { HasThis = method.HasThis, ExplicitThis = method.ExplicitThis, CallingConvention = method.CallingConvention };
            foreach (var parameter in method.GenericParameters) reference.GenericParameters.Add(new GenericParameter(parameter.Name, reference));
            foreach (var parameter in method.Parameters) reference.Parameters.Add(new ParameterDefinition(MapType(parameter.ParameterType)));
            return reference;
        }
        FieldReference MapField(FieldReference field)
        {
            if (field is FieldDefinition def && states.Contains(def.DeclaringType)) return def;
            return new FieldReference(field.Name, MapType(field.FieldType), MapType(field.DeclaringType));
        }
        void Remap(MethodDefinition method)
        {
            method.ReturnType = MapType(method.ReturnType);
            foreach (var p in method.Parameters) p.ParameterType = MapType(p.ParameterType);
            method.CustomAttributes.Clear();
            if (!method.HasBody) return;
            foreach (var local in method.Body.Variables) local.VariableType = MapType(local.VariableType);
            foreach (var handler in method.Body.ExceptionHandlers)
                if (handler.CatchType != null) handler.CatchType = MapType(handler.CatchType);
            foreach (var i in method.Body.Instructions)
            {
                if (i.Operand is MethodReference target && syncByName.ContainsKey(target.FullName) && i.OpCode == OpCodes.Callvirt)
                    i.OpCode = OpCodes.Call;
                i.Operand = i.Operand switch {
                    MethodReference m => MapMethod(m), FieldReference f => MapField(f), TypeReference t => MapType(t), _ => i.Operand
                };
            }
        }
        foreach (var state in states)
        {
            state.BaseType = MapType(state.BaseType);
            foreach (var field in state.Fields) field.FieldType = MapType(field.FieldType);
            foreach (var method in state.Methods) Remap(method);
            foreach (var face in state.Interfaces) face.InterfaceType = MapType(face.InterfaceType);
            state.CustomAttributes.Clear();
        }
        var roots = owners.Values.Concat(synchronous).ToArray();
        var rootInfo = roots.ToDictionary(m => m, m => (Token: plan.OriginalMethodTokens[m.FullName], Owner: MapType(m.DeclaringType), WasInstance: m.HasThis));
        foreach (var outer in roots) Remap(outer);
        foreach (var outer in roots)
        {
            var (token, ownerType, wasInstance) = rootInfo[outer];
            var oldThis = wasInstance ? outer.Body.ThisParameter : null;
            outer.DeclaringType.Methods.Remove(outer);
            outer.Name = "Run_" + token.ToString("X8");
            outer.Attributes = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
            outer.HasThis = false;
            if (wasInstance)
            {
                var self = new ParameterDefinition("self", ParameterAttributes.None, ownerType);
                outer.Parameters.Insert(0, self);
                foreach (var i in outer.Body.Instructions) if (ReferenceEquals(i.Operand, oldThis)) i.Operand = self;
            }
            host.Methods.Add(outer);
            outerNames.Add(token, outer.Name);
        }
        output.Types.Clear();
        output.Types.Add(new TypeDefinition("", "<Module>", TypeAttributes.NotPublic));
        output.Types.Add(host);
        output.Assembly.CustomAttributes.Clear();
        output.CustomAttributes.Clear();
        output.Resources.Clear();
        output.Name = "AncientAffectionCompat.Async.dll";
        output.Assembly.Name.Name = "AncientAffectionCompat.Async";
        // Harmony identifies registered methods by module MVID + metadata token.
        // A generated helper must never share the original module's identity.
        output.Mvid = new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("AncientAffectionCompat.Async:v0.3.0")).AsSpan(0, 16));
        var index = 0;
        foreach (var state in states)
        {
            state.DeclaringType!.NestedTypes.Remove(state);
            state.DeclaringType = null;
            state.Namespace = "AncientAffectionCompat.Generated";
            state.Name = "State_" + index++;
            state.Attributes = (state.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public;
            output.Types.Add(state);
        }
        // CLR-supported dynamic assembly access bypass, limited to the original mod.
        var attribute = new TypeDefinition("System.Runtime.CompilerServices", "IgnoresAccessChecksToAttribute",
            TypeAttributes.Public | TypeAttributes.Sealed, output.ImportReference(typeof(Attribute)));
        var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            output.TypeSystem.Void);
        ctor.Parameters.Add(new ParameterDefinition("assemblyName", ParameterAttributes.None, output.TypeSystem.String));
        var il = ctor.Body.GetILProcessor();
        il.Append(Instruction.Create(OpCodes.Ldarg_0));
        il.Append(Instruction.Create(OpCodes.Call, output.ImportReference(typeof(Attribute).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null)!)));
        il.Append(Instruction.Create(OpCodes.Ret));
        attribute.Methods.Add(ctor); output.Types.Add(attribute);
        var access = new CustomAttribute(ctor);
        access.ConstructorArguments.Add(new CustomAttributeArgument(output.TypeSystem.String, originalScope.Name));
        output.Assembly.CustomAttributes.Add(access);
        using var bytes = new MemoryStream(); output.Write(bytes);
        return new AsyncCopies(bytes.ToArray(), outerNames, moveNextTokens, synchronous.Select(m => rootInfo[m].Token).ToHashSet());
    }

    private static IEnumerable<TypeDefinition> Flatten(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types) { yield return type; foreach (var child in Flatten(type.NestedTypes)) yield return child; }
    }
}
