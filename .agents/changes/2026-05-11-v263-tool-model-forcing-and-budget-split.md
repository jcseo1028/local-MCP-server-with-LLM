# 2026-05-11 변경 기록: v2.6.3 도구 모델 강제 + 예산 분리 + 대기시간 제외

## 목적

- 1) 코드 수정 도구 실행 모델을 코드 모델로 강제한다.
- 2) Run 전체 예산과 step 실행 예산을 분리한다.
- 3) 승인/클라이언트 대기 시간을 예산 계산에서 제외한다.

## 코드 변경

### 1. 도구 모델 강제
- 파일: `src/LocalMcpServer/ToolRegistry/CodeToolBase.cs`
- 변경:
  - `ServerConfig` 주입 추가
  - `ResolveToolModel()` 기본 구현 추가 (`Llm.DefaultModel`)
  - `GenerateAsync` 호출 시 `LlmRequest.Model` 명시
- 효과: 코드 도구가 `summaryModel`로 실행되는 경로 차단

### 2. 생성자 시그니처 정리
- 파일:
  - `src/LocalMcpServer/ToolRegistry/AddCommentsTool.cs`
  - `src/LocalMcpServer/ToolRegistry/RefactorCurrentCodeTool.cs`
  - `src/LocalMcpServer/ToolRegistry/FixCodeIssuesTool.cs`
  - `src/LocalMcpServer/ToolRegistry/OrganizeImportsTool.cs`
- 변경: 모두 `IOptions<ServerConfig>`를 받아 base로 전달

### 3. Run 예산 분리 + 대기시간 제외
- 파일: `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- 변경:
  - `StepExecutionBudget`(기본 4분) 추가
  - `StartPause/EndPause/GetRemainingRunBudget` 헬퍼 추가
  - 승인 대기 진입 시 `StartPause`, 승인 처리 시 `EndPause`
  - 클라이언트 빌드/테스트 대기 진입 시 `StartPause`, 결과 수신 시 `EndPause`
  - step 실행마다 `min(남은 Run 예산, Step 예산)`으로 linked cancellation token 생성
  - 멀티파일 재시도 판단도 `GetRemainingRunBudget()` 기준으로 변경

### 4. Run 모델 확장
- 파일: `src/LocalMcpServer/McpServer/RunModels.cs`
- 변경:
  - `PausedDuration`
  - `PauseStartedAt`

## 문서 반영

- `.agents/contracts.md`: `chat.chatModel` 및 `chat.intentModel` fallback 규칙 갱신
- `.agents/modules.md`: v2.6.3 항목(도구 모델 강제, 예산 분리) 추가
- `README.md`: v2.6.3 기능 3개 항목 추가

## 검증

- `src/LocalMcpServer`에서 `dotnet build` 성공
- 참고: 실행 중 프로세스가 `LocalMcpServer.exe`를 점유하여 apphost 복사 경고(MSB3026) 발생 가능
