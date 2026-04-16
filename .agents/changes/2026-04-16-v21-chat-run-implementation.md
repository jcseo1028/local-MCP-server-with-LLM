# 2025-04-16 v2.1 Chat Run Orchestration 구현

## 변경 목적
Chat Run Pipeline (v2.1) 스펙을 구현하여 9단계 오케스트레이션을 서버 + VSIX 양쪽에서 동작하도록 한다.

## 변경 파일

### MCP Server (신규)
- `McpServer/RunModels.cs` — Run 상태 모델 (RunState, StageStatus, RunData, RunStage, API DTO)
- `McpServer/RunOrchestrator.cs` — 9단계 상태 머신 엔진 (백그라운드 Task 실행)
- `McpServer/DocumentSearcher.cs` — 로컬 파일 시스템 문서 검색 (오프라인 전용)
- `prompts/planning.prompt.md` — 계획 수립 LLM 프롬프트
- `prompts/run_summary.prompt.md` — 결과 요약 LLM 프롬프트

### MCP Server (수정)
- `Configuration/ServerConfig.cs` — DocumentSearchSection, BuildSection 추가
- `appsettings.json` — DocumentSearch, Build 설정 섹션 추가
- `McpServer/ConversationStore.cs` — IConversationStore에 AddRun/GetRun/ListRuns 추가
- `McpServer/IntentResolver.cs` — GeneratePlanAsync, GenerateSummaryAsync 추가
- `McpServer/McpEndpoints.cs` — Run API 4개 엔드포인트 추가 + BuildRunSnapshot 헬퍼
- `Program.cs` — DocumentSearcher, RunOrchestrator DI 등록

### VSIX (신규)
- `Services/BuildTestRunner.cs` — 오프라인 빌드/테스트 러너 (dotnet build --no-restore)

### VSIX (수정)
- `Services/McpRestClient.cs` — Run API 클라이언트 메서드 4개 + DTO 추가
- `Services/ChatMessageViewModel.cs` — RunId, RunState, RunStages 속성 추가
- `ToolWindows/SummaryToolWindowControl.cs` — Run 기반 SendMessageAsync, 폴링, 타임라인 UI, Run 승인/거부, 빌드/테스트 실행

### .agents 문서
- `pipeline.md` — "v2.1 예정" → "v2.1" (구현 상태 반영)
- `modules.md` — 구현 파일 목록 갱신 (RunModels, BuildTestRunner 추가)

## 설계 결정
1. Run 오케스트레이션은 서버 백그라운드 Task로 실행. 클라이언트는 폴링으로 상태 추적.
2. 승인(WaitingForApproval)에서 파이프라인 중단 후 클라이언트 POST로 재개.
3. 빌드/테스트는 VSIX 클라이언트에서 실행하고 결과를 서버로 보고.
4. Document 검색은 Config.DocumentSearch.Directories가 비어있으면 Skip.
5. 모든 LLM 호출은 로컬 Ollama만 사용 (오프라인 제약 준수).

## 빌드 확인
- MCP Server: `dotnet build --no-restore` 성공
- VSIX: MSBuild 빌드 성공 (VSIX 패키징 포함)
