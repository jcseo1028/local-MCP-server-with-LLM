# 2026-05-11 변경 기록: organize_imports 안전장치 및 문서 동기화

## 코드 변경

- `ToolRegistry/OrganizeImportsTool.cs` 추가
  - using/import 전용 정리 도구 등록
- `McpServer/IntentResolver.cs`
  - `organize_imports`를 코드 수정 도구(EditTools)에 포함
  - using/import 키워드 fallback 매핑 추가
- `ToolRegistry/CodeToolBase.cs`
  - 멀티파일 모드에서 단건 코드 섹션 비활성화를 위한 `single_code_section` 변수 추가
- `prompts/organize_imports.prompt.md` 추가
  - using/import 전용 규칙 + 멀티파일 [FILE:] 출력 규약 반영
- `prompts/refactor_current_code.prompt.md`
  - 요청 범위 준수 규칙 강화
  - 멀티파일 모드 시 [FILE:] 출력 규약 강화
- `McpServer/RunOrchestrator.cs`
  - 멀티파일 [FILE:] 파싱 실패 시 1회 재시도 + 명시적 실패
  - `organize_imports` 결과 검증(본문 변경 감지) 추가
  - 본문 변경 시 import 블록만 원본 본문에 자동 보정 후 재검증

## 문서 변경 (.agents)

- `contracts.md`
  - `refactor_current_code` 멀티파일 입력(`files_context`) 및 엄격 모드 반영
  - `organize_imports` 계약(§7d-2) 추가 및 내부 검증/자동 보정 동작 설명 반영
- `modules.md`
  - Tool Registry 구현 목록에 `OrganizeImportsTool` 추가
  - CodeToolBase 멀티파일 프롬프트 조립 방식(`single_code_section`) 반영
  - RunOrchestrator의 멀티파일 엄격 모드 및 using/import 보정 로직 반영
- `pipeline.md`
  - Tool-specific flow에 `organize_imports` 추가
  - `summarize_current_code` 변수 치환을 `code_section` 기준으로 정정

## README 변경

- 도구 수를 6개 → 7개로 갱신 (`organize_imports` 포함)
- 지원 도구 표에 `organize_imports` 항목 추가
- 프로젝트 구조에 `ToolRegistry/OrganizeImportsTool.cs` 추가
- v2.5 섹션 추가
  - organize_imports 도구
  - 멀티파일 엄격 모드
  - using/import 검증 + 자동 보정

## 검증

- `src/LocalMcpServer` 빌드 성공 (`dotnet build`)
- 실제 시나리오에서 organize_imports가 멀티파일 [FILE:] 출력으로 파싱되는 것을 확인함
- using/import 외 본문이 변경된 응답은 검증 단계에서 차단/보정됨
