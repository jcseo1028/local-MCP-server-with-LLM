# Modules

## Overview

시스템은 아래 5개 모듈로 구성된다. 각 모듈은 명확한 책임을 가지며 독립적으로 구현·교체할 수 있다.

---

## 1. MCP Server

- **책임**: MCP 프로토콜 준수, VS 2022 Agent mode 요청 수신, 메서드 라우팅, 응답 반환, **채팅 의도 분석 및 대화 관리**, **Run 단위 다단계 오케스트레이션**
- **입력**: MCP 클라이언트 요청 (JSON-RPC) — 클라이언트는 VS 2022 Agent mode 또는 REST 클라이언트
- **출력**: MCP 프로토콜 응답 (JSON-RPC) 또는 REST JSON 응답
- **의존**: Tool Registry, Configuration, **LLM Connector (의도 분석·계획 수립·결과 요약 전용 — 라우팅 결정 목적)**, **Resource Cache (문서검색 단계 전용 — 로컬 문서 조회 목적)**
- **비의존**: 없음
- **동기 REST**: `GET /api/tools/list`, `POST /api/tools/call` — SSE 세션 없이 직접 도구 실행. 오프라인 CLI 호출용.
- **Chat REST**: `POST /api/chat`, `POST /api/chat/approve` — 채팅 기반 의도 분석 + 도구 자동 실행 (contracts.md §9, §10)
- **Run REST (v2.1/v2.6)**: `POST /api/chat/runs`, `GET /api/chat/runs/{runId}`, `POST /api/chat/runs/{runId}/approval`, `POST /api/chat/runs/{runId}/confirm`, `POST /api/chat/runs/{runId}/revert`, `POST /api/chat/runs/{runId}/client-result` — 다단계 오케스트레이션 + 임시 적용 확정/되돌리기 (contracts.md §11)
- **IntentResolver**: 사용자 메시지를 분석하여 적절한 도구를 선택한다. LLM Connector를 직접 호출하되, 이는 "도구 실행"이 아닌 "라우팅 결정"이다. v2.1에서 **계획 수립**(최대 5개 항목) 역할도 포함한다.
- **IntentResolver 모델 분기 (v2.6.2)**: 의도 분석/계획 수립은 `Chat.IntentModel` 우선(없으면 `Llm.GeneralModel`), 일반 대화 응답은 `Chat.ChatModel` 우선, 결과 요약은 `Llm.SummaryModel` 우선으로 분리한다.
- **DocumentSearcher (v2.1)**: `Config.documentSearch.directories` 내 로컬 문서를 검색한다. Resource Cache를 통해 조회하며, 문서가 없으면 Skipped 처리한다. 외부 네트워크를 호출하지 않는다.
- **RunOrchestrator (v2.1/v2.6)**: Run 단위로 9단계 상태 머신을 관리한다. IntentResolver, DocumentSearcher, Tool Registry를 순차 호출하고 단계별 상태를 ConversationStore에 기록한다. context_collection 단계에서 코드 크기 검증(32KB 절단)을 수행한다. **A-2**: `BuildFileChange()`에서 `DiffEngine.Compute()`를 호출하여 `FileChange.Hunks`를 사전 계산하고 proposal에 포함한다. **B-4**: `ParseFileBlocks()`가 1차 `[FILE:]…[/FILE]` 파싱 후 실패 시 2차 마크다운 헤딩(`### path.ext`, `**path.ext**`, `// File:`) 패턴 폴백 파싱. **B-5**: `BuildFilesContext()`에서 파일당 8,000자 / 전체 32,000자 제한 적용, 임시표 `파일별 비율 분배` 절단, 프롬프트 내 생략 표시. RAG 활성화 시 `VectorSearchEngine` 검색 결과를 `files_context`에 주입해 대용량 파일의 관련 조각을 함께 전달한다. **SC-4**: 멀티파일 입력에서 `[FILE:]` 파싱 실패 시 단건 조용한 폴백 없이 1회 재시도 후 명시적 실패 처리. **SC-5**: `organize_imports` 결과는 using/import 외 본문 변경 여부를 검증하고, 본문이 변경된 경우 import 블록만 원본 본문에 자동 투영(자동 보정) 후 재검증한다. **v2.6**: `allowMultiToolPlan/maxPlanSteps` 기반 다중 도구 실행 계획(`PlanSteps`)을 구성하며, 코드 변경 단계는 `PendingPatch` 상태로 추적하고 `confirm/revert` API로 확정/되돌리기 상태 전이를 지원한다.
- **RunOrchestrator 안정화 (v2.6.1)**: 다중 도구 계획 키워드에 `리팩토링`을 추가해 `refactor_current_code` 단계 생성 누락을 줄였다. 계획 파서는 빈/공백 응답을 무의미한 1개 항목으로 승격하지 않도록 정리했다. 멀티파일 파싱 재시도는 Run 전체 남은 시간 예산(최소 90초) 확인 후 수행하며, 예산 부족 시 재시도를 생략하고 명시적 안내를 반환한다. `OperationCanceledException` 로그는 타임아웃 취소와 외부 취소를 분리해 기록한다.
- **RunOrchestrator 예산 분리 (v2.6.3)**: 전체 Run 예산(10분)과 step 실행 예산(기본 4분)을 분리하고, 승인 대기/클라이언트 빌드·테스트 대기 시간은 예산 계산에서 제외한다. 멀티파일 재시도 여부도 `실제 남은 실행 예산` 기준으로 판단한다.
- **RunOrchestrator 파일별 편집 조합 (v2.6.4)**: `organize_imports`, `refactor_current_code` 같은 편집 도구는 멀티파일 응답 포맷을 모델에게 강제하지 않고, 선택된 파일마다 단건 모드로 실행한 뒤 서버가 `FileChange` 목록으로 조합한다. 다른 선택 파일은 요약된 보조 컨텍스트로만 전달한다.
- **RunOrchestrator fail-fast (v2.6.5)**: 클라이언트 `client-result`에서 적용 실패/빌드 실패/테스트 실패가 보고되면 후속 step 실행을 중단하고 Run을 즉시 `Failed`로 종료한다. 현재 step 상태도 `Failed`로 반영한다.
- **RunOrchestrator 대용량 파일 강제 모델 (v2.6.6)**: 코드 수정 도구 실행 시 파일 줄 수를 기준으로 모델/입력 크기를 동적으로 결정한다. 800줄 이상은 `Llm.LargeFileModel`(예: `qwen2.5-coder:32b`)을 강제 사용하며, 설정이 없으면 폴백 없이 명시적 실패를 반환한다. 입력 길이는 300줄 초과(16KB), 800줄 이상(24KB), 2000줄 초과(12KB 청크 기준)로 조정한다.
- **RunOrchestrator 타임아웃 조정 (v2.6.6)**: 32b 추론 지연을 고려해 Run 예산을 30분, step 실행 예산을 12분으로 상향했다. 후처리 단계 제한은 15분, 최종 요약 제한은 10분으로 조정했다.
- **RunOrchestrator 대용량 분할 처리 (v2.6.7)**: 2000줄 초과 파일은 메서드 단위 분할 모드(실패 시 라인 청크 분할)로 처리한다. 청크별 도구 실행이 모두 성공할 때만 최종 코드를 반영하여 원자성을 보장한다.
- **RunOrchestrator 자동 구문 복구 (v2.6.7)**: C# 출력에서 괄호/중괄호 불균형을 감지하면 닫힘 토큰 보정으로 1차 자동 복구를 시도한다. 복구 실패 시 해당 수정안을 거부한다.
- **RAG Phase 1 코드 분할기 (v2.6.7)**: `CodeChunker`를 추가해 C# 파일을 class/region/method 기준으로 분할하고, 초과 길이는 라인 블록(overlap 포함)으로 재분할한다. 본 단계는 파싱/분할 유틸 구현 범위이며 벡터 저장/검색 통합은 Phase 2 이후에 진행한다.
- **검증 모드**: `ChatRunStartRequest.intentAndPlanOnly=true` 이면 RunOrchestrator가 의도 분석과 계획 수립까지만 수행하고, 후속 단계는 Skipped 처리한 뒤 즉시 완료한다. 느린 LLM 환경에서 의도/계획의 타당성을 우선 검증하기 위한 모드다.
- **ConversationStore**: 대화 상태를 메모리 내 관리. 대화 이력을 LLM 컨텍스트에 포함. v2.1에서 **run 단위 실행 상태**도 함께 관리. 향후 SQLite 등 경량 DB 마이그레이션 가능하도록 인터페이스 분리.
- **제약**: 모든 LLM 호출은 로컬 엔드포인트(Ollama 등)만 사용. 원격 LLM API 금지. 문서검색은 로컬 파일만 대상.
- **구현**: `McpServer/McpEndpoints.cs` — SSE + REST + Chat + Run 엔드포인트, `McpServer/IntentResolver.cs`, `McpServer/ConversationStore.cs`, `McpServer/RunOrchestrator.cs` (v2.1), `McpServer/DiffEngine.cs` (A-2 서버 측 diff 사전 계산), `McpServer/DocumentSearcher.cs` (v2.1), `McpServer/RunModels.cs` (v2.1 상태 모델 + `DiffHunkInfo`), `McpServer/RunLogger.cs` (v2.1 파이프라인 로깅)
- **CodeToolBase 모델 선택 (v2.6.3)**: 코드 수정 도구는 `LlmRequest.Model`을 명시하여 `Llm.DefaultModel`(코드 모델)로 호출한다. 모델 미지정 시 `summaryModel`로 떨어지는 부작용을 방지한다.
- **CodeToolBase 모델 오버라이드 (v2.6.6)**: 도구 인자에 `model`이 전달되면 해당 값을 우선 사용하고, 없으면 `Llm.DefaultModel`을 사용한다.
- **CodeToolBase 보조 컨텍스트 (v2.6.4)**: 단건 편집 모드에서 `related_files_context` 변수를 프롬프트에 주입해, 현재 파일 외 참조 정보만 제한적으로 제공한다.
- **RunOrchestrator 대용량 파일 적응형 처리 (v2.6.6)**:
  - MaxPerFileChars 동적 결정: 300줄 미만(8KB), 300-800줄(10-16KB), 800줄 이상(16-20KB)
  - 모델 선택: 800줄 이상 파일은 `qwen2.5-coder:32b` 우선 사용
  - SelectionOnly 모드: 4KB 유지
  - 메서드 추가: `GetOptimalMaxPerFileChars(lineCount)`, `GetOptimalModelForFile(lineCount)`
  
- **완성도 검증 (v2.6.6)**:
  - 메서드 보존율: 변경 후 메서드 수 ≥ 원본 × 80% (공개 메서드 기준)
  - 구문 검증: C# 컴파일 오류 0개
  - 메서드 추가: `ValidateMethodPreservationRate(before, after, minRate)`, `CountPublicMethods(code)`
  - 실패 시 자동 거부 및 사용자 알림

## 2. Tool Registry

- **책임**: 도구 정의 등록, 도구 목록 조회, 도구 실행, 프롬프트 템플릿 로드
- **입력**: ToolListRequest, ToolCallRequest (`contracts.md` 참조)
- **출력**: ToolListResponse, ToolCallResponse (`contracts.md` 참조)
- **의존**: LLM Connector (도구 실행 시 필요한 경우에만), Resource Cache (도구 실행 시 자료 조회가 필요한 경우에만), Configuration
- **비의존**: MCP Server
- **프롬프트 관리**: 각 도구는 `Config.tools.promptsDirectory`에서 `{toolName}.prompt.md` 파일을 로드하여 변수 치환 후 LLMRequest.prompt로 전달한다. 코드 수정 없이 프롬프트 튜닝이 가능하다.
- **코드 수정 도구 패턴**: `CodeToolBase` 추상 클래스가 code+language(+files_context) 입력, LLM 호출, 옵션 오버라이드 패턴을 공통화한다. 멀티파일 모드에서는 `single_code_section`을 빈 문자열로 만들어 단건 코드 블록 출력을 비활성화한다. 개별 도구(AddCommentsTool, RefactorCurrentCodeTool, OrganizeImportsTool, FixCodeIssuesTool)는 이를 상속하여 프롬프트와 LlmOptions만 정의한다.
- **프로젝트 구조 분석 도구**: `AnalyzeProjectStructureTool`은 Resource Cache의 코드 인덱스(파일/심볼 구조)를 LLM 컨텍스트로 전달하여 프로젝트 전체 아키텍처를 분석한다. 단일 파일이 아닌 프로젝트 범위 분석을 담당한다.
- **구현**: `ToolRegistry/` — ToolRegistryService, SummarizeCurrentCodeTool, CodeToolBase, AddCommentsTool, RefactorCurrentCodeTool, OrganizeImportsTool, FixCodeIssuesTool, AnalyzeProjectStructureTool, PromptTemplateLoader

## 3. LLM Connector

- **책임**: 로컬 LLM 엔드포인트와의 통신 추상화, 요청/응답 변환, 모델 선택
- **입력**: LLMRequest (`contracts.md` 참조)
- **출력**: LLMResponse (`contracts.md` 참조)
- **의존**: Configuration
- **외부 의존**: 로컬 LLM 엔드포인트 (권장: Ollama)
- **제약**: 오프라인 환경 전용. 원격 LLM 엔드포인트를 사용하지 않는다.
- **다중 모델**: 코드 모델(`DefaultModel`, 코드 변환용)과 일반 모델(`GeneralModel`, 의도 분석·계획·대화·요약용)을 Config 기반으로 선택한다. 호출자가 `LlmRequest.Model`에 적절한 모델을 명시하고, null이면 `SummaryModel → DefaultModel` 순으로 폴백한다. 모델 선택 시 `string.IsNullOrEmpty` 검증으로 빈 문자열도 fallback 처리한다.
- **요청 타임아웃 (v2.6.6)**: `Program.cs`의 `AddHttpClient<OllamaConnector>()`에서 `Llm.RequestTimeoutMinutes`를 적용한다. 기본값은 20분이며 32b 모델 장시간 추론을 허용한다.
- **Embedding Connector (v2.6.7 Phase 2)**: `EmbeddingConnector`가 Ollama `/api/embed`를 호출해 텍스트를 벡터로 변환한다. 기본 모델은 `Rag.EmbeddingModel`이며 미설정 시 `nomic-embed-text`를 사용한다.
- **Vector Search Engine (v2.6.7 Phase 3)**: `VectorSearchEngine`가 `CodeChunker`와 `EmbeddingConnector`를 조합해 코드 파일을 chunk로 분할하고 코사인 유사도로 관련 chunk를 검색한다. `IResourceCache`의 현재 인덱스 루트와 색인 파일 목록을 활용하며, chunk embedding은 `ResourceCache`의 영속 저장소에 보관한다.
- **구현**: `LlmConnector/` — OllamaConnector (`/api/chat` 엔드포인트), LlmModels

## 4. Resource Cache

- **책임**: 현장 필수 자료(문서, 표준, API 참조, 코드 스니펫 등)의 로컬 저장 및 조회, **프로젝트 코드 인덱스** 검색
- **입력**: CacheLookupRequest, CodeSearchRequest (`contracts.md` 참조)
- **출력**: CacheLookupResponse, CodeSearchResponse (`contracts.md` 참조)
- **의존**: Configuration
- **제약**: 모든 자료는 사전 준비(pre-populated)되어야 한다. 런타임에 외부에서 자료를 다운로드하지 않는다.
- **코드 인덱스**: 하이브리드 방식으로 동작한다.
  1. 정규식 기반 심볼 추출: 소스 파일에서 클래스/함수/메서드 선언 패턴을 감지하여 심볼 목록을 구축한다.
  2. 전문 텍스트 검색: 키워드 기반 역인덱스로 전체 텍스트 검색을 지원한다.
  3. 색인 범위는 Config.codeIndex.filePatterns로 제어한다.
  4. 향후 필요 시 AST 파싱(Roslyn 등)으로 업그레이드 가능한다.
- **구현**: `ResourceCache/` — IResourceCache, ResourceCacheService, CacheModels. 시작 시 캐시 디렉터리 로드 + 코드 인덱스 구축. 정규식 기반 심볼 추출 + 텍스트 역인덱스.
- **혼합형 코드 인덱스**: VSIX SolutionPath 우선, Config.CodeIndex.RootPath 폴백.
  - VSIX Run 시작 시 SolutionPath를 서버에 전송 → `ReindexAsync`로 동적 재인덱싱
  - `ReaderWriterLockSlim`으로 검색/재인덱싱 간 스레드 안전성 확보
  - 같은 루트 경로면 재인덱싱 스킵
  - `GetIndexedCodeFiles()`로 현재 인덱스 대상 파일 목록을 제공해 RAG Vector Search가 chunk 후보를 구성한다.
  - `GetChunkEmbeddingAsync()` / `StoreChunkEmbeddingAsync()`로 chunk embedding을 SQLite(`rag-index.sqlite`)에 영속 저장해 재계산 비용을 줄인다.
  - `GetConversationRagStateAsync()` / `UpsertConversationRagStateAsync()` / `TouchConversationChunkAsync()`로 conversationId 기반 warm chunk 메타를 관리하고, 서버 재시작 후에도 세션별 RAG warm-start를 지원한다.

## 5. Configuration

- **책임**: 서버, 모델, 도구, 캐시, 코드 인덱스 설정의 중앙 관리
- **입력**: 설정 파일 또는 환경 변수
- **출력**: Config 객체 (`contracts.md` 참조)
- **의존**: 없음
- **구현**: `Configuration/ServerConfig.cs` + `appsettings.json`

## 6. VS Extension (VSIX)

- **책임**: VS 2022 내 **채팅 기반 Tool Window UI** 제공, 현재 편집 파일/선택 영역 코드 획득, MCP Server Chat API 호출, 대화 이력 표시, 코드 변경 승인/거부, **빌드/테스트 실행 및 결과 보고**
- **입력**: 사용자 채팅 메시지 입력 (자연어) + 현재 에디터 코드 컨텍스트 (자동 첨부)
- **출력**: 채팅 메시지 목록 (Markdown 렌더링, 대화 이력) + 코드 변경 side-by-side diff 뷰 + **단계별 타임라인 UI (v2.1)**
- **의존**: MCP Server (REST API — contracts.md §8, §9, §10, **§11**: tools/list, tools/call, chat, chat/approve, **chat/runs 계열**)
- **비의존**: Tool Registry, LLM Connector, Resource Cache, Configuration (모두 서버 측)
- **제약**: VS 2022 17.14+ 전용, .NET Framework 4.8, 오프라인 환경에서도 동작 (서버가 로컬이므로)
- **VS 테마**: VsBrushes + VSColorTheme 기반 Dark/Light/Blue 자동 대응
- **Markdown 렌더링**: Markdig AST → WPF FlowDocument 변환 (헤딩, 리스트, 코드블록, 볼드/이탤릭, 인용)
- **채팅 UI**: 사용자 메시지(우측) / 봇 응답(좌측) 대화 형태. Enter 전송, Shift+Enter 줄바꿈.
- **코드 변경 승인**: 코드 수정 도구 결과를 side-by-side diff로 표시. 사용자 승인(✅) 시 에디터에 적용, 거부(❌) 시 미적용. VS Undo 스택으로 자동 백업. **A-1**: per-hunk 수낙/거부 체크박스 UI—각 hunk 헤더에 체크박스가 표시되며 체크 해제 시 해당 hunk를 건너맜. **A-2**: 서버에서 `hunks`를 사전 계산하여 전달하면 VSIX `LineDiffEngine.Compute()` 재계산 생략. **A-3**: unified diff 콤러 하이라이트—삭제 라인 빨간 배경, 추가 라인 초록 배경, 컨텍스트 기본 배경, ±3 컨텍스트 라인 표시. **v2.6**: PendingPatch가 있는 Run에서는 승인/거부 버튼을 확정/되돌리기(`confirm/revert`)로 전환하고, 실패 시 `approval` API로 하위 호환 폴백한다.
- **코드 적용 모드**: 선택 영역 모드(SelectionOnly)일 때 원본 텍스트를 문서에서 찾아 해당 부분만 교체. 원본 매칭 실패 시 적용 실패로 처리하고 build_test는 Skipped (§8g). 전체 파일 모드일 때 전체 교체. **B-1**: 멀티파일 Expander 헤더에 파일별 승인/거부 체크박스 표시—체크 해제된 파일은 적용 제외. **B-2**: 파일 선택 UI—파일 선택 버튼 + 체크리스트 패널 토글로 열린 파일 직접 선택 가능. **B-3**: 멀티파일 atomic rollback—적용 전 원본 메모리 백업, 어느 파일이든 실패 시 이미 적용된 전체 반자.
- **대화 세션 백업**: 새 대화 시작 또는 이전 대화 복원 시 현재 대화를 자동 백업. 최대 20개 세션 유지. 첫 번째 사용자 메시지를 제목으로 표시.
- **코드 컨텍스트 확인**: 선택 영역 없이 코드 수정 요청 시 "현재 파일 전체를 포함할까요?" 확인 후 진행.
- **구현**: `src/LocalMcpVsExtension/` — VSIX 프로젝트 (Community.VisualStudio.Toolkit.17, Markdig). `Services/McpRestClient.cs` (Run API 클라이언트 + `DiffHunkDto` + `confirm/revert` 호출), `Services/ChatMessageViewModel.cs` (ChatRunViewModel·ChatRunStageViewModel 분리, `CodeChangeInfo.HunkSelections`, `FileChangeInfo.HunkSelections`, `HunkSelection` 포함), `Services/BuildTestRunner.cs` (v2.1 오프라인 빌드/테스트, dotnet 빌드의 MSB4803 감지 시 Visual Studio MSBuild 폴백), `Services/LineDiffEngine.cs` (Myers LCS diff, `DiffHunk`), `ToolWindows/SummaryToolWindowControl.cs` (Run 폴링 + 타임라인 UI + v2.6 실행 단계 섹션 + A-1/A-2/A-3/B-1/B-2/B-3 + 파일 선택·per-hunk·unified diff·rollback)
- **빌드 주의**: SDK-style csproj에서 `<Import Project="Sdk.props" />` / `<Import Project="Sdk.targets" />` 를 명시적으로 분리하고 VsSDK.targets를 Sdk.targets 뒤에 import해야 pkgdef 생성과 VSIX 패키징이 동작한다. VS 2022 MSBuild로 빌드한다.

---

## Module Interaction Rules

- 모듈 간 통신은 `contracts.md`에 정의된 계약을 통해서만 수행한다.
- 다른 모듈의 내부 구현을 직접 참조하지 않는다.
- 의존 관계는 위에 명시된 것만 허용한다. 새 의존 추가 시 이 문서를 먼저 갱신한다.
- 새 모듈 추가 시 이 문서와 `contracts.md`를 먼저 갱신한다.
