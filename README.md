# Ancient Affection Compatibility Patch

Slay the Spire 2 **v0.113.1 (d09d48e5)**용 Ancient Affection v0.1.0 호환 패치입니다.
원작자는 **nagis**이며, 이 프로젝트는 비공식 후속 호환 패치입니다.

v0.3.0은 원본을 함께 구독하는 별도 패치입니다. **원본 Ancient Affection, BaseLib, RitsuLib과 이 패치를 함께 활성화하면 됩니다.** 원본을 의존성으로 지정하므로 게임이 로딩 순서를 자동으로 정렬합니다.

원본 파일을 수정하거나 재배포하지 않고, 원본 로드 후 필요한 호출부를 교정합니다. 원본 유물 객체·세이브 식별자를 유지하며 모든 언어의 설명문과 이미지를 그대로 사용합니다.

`./BuildAndVerify.ps1`은 `dist/workshop-content`에 창작마당용 패키지를 만들고 자동 정렬 120가지·로더·유물·비동기 재개·동기화 검증을 실행합니다. 설치나 백업은 수행하지 않습니다. 실제 두 PC의 멀티플레이와 중도 저장/재접속은 아직 검증하지 않았습니다.
