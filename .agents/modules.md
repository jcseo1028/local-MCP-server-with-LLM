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
- **Run REST (v2.1)**: `POST /api/chat/runs`, `GET /api/chat/runs/{runId}`, `POST /api/chat/runs/{runId}/approval`, `POST /api/chat/runs/{runId}/client-result` — 다단계 오케스트레이션 (contracts.md §11)
- **IntentResolver**: 사용자 메시지를 분석하여 적절한 도구를 선택한다. LLM Connector를 직접 호출하되, 이는 "도구 실행"이 아닌 "라우팅 결정"이다. v2.1에서 **계획 수립**(최대 5개 항목) 역할도 포함한다.
- **DocumentSearcher (v2.1)**: `Config.documentSearch.directories` 내 로컬 문서를 검색한다. Resource Cache를 통해 조회하며, 문서가 없으면 Skipped 처리한다. 외부 네트워크를 호출하지 않는다.
- **RunOrchestrator (v2.1)**: Run 단위로 9단계 상태 머신을 관리한다. IntentResolver, DocumentSearcher, Tool Registry를 순차 호출하고 단계별 상태를 ConversationStore에 기록한다. context_collection 단계에서 코드 크기 검증(32KB 절단)을 수행한다.
- **ConversationStore**: 대화 상태를 메모리 내 관리. 대화 이력을 LLM 컨텍스트에 포함. v2.1에서 **run 단위 실행 상태**도 함께 관리. 향후 SQLite 등 경량 DB 마이그레이션 가능하도록 인터페이스 분리.
- **제약**: 모든 LLM 호출은 로컬 엔드포인트(Ollama 등)만 사용. 원격 LLM API 금지. 문서검색은 로컬 파일만 대상.
- **구현**: `McpServer/McpEndpoints.cs` — SSE + REST + Chat + Run 엔드포인트, `McpServer/IntentResolver.cs`, `McpServer/ConversationStore.cs`, `McpServer/RunOrchestrator.cs` (v2.1), `McpServer/DocumentSearcher.cs` (v2.1), `McpServer/RunModels.cs` (v2.1 상태 모델)

## 2. Tool Registry

- **책임**: 도구 정의 등록, 도구 목록 조회, 도구 실행, 프롬프트 템플릿 로드
- **입력**: ToolListRequest, ToolCallRequest (`contracts.md` 참조)
- **출력**: ToolListResponse, ToolCallResponse (`contracts.md` 참조)
- **의존**: LLM Connector (도구 실행 시 필요한 경우에만), Resource Cache (도구 실행 시 자료 조회가 필요한 경우에만), Configuration
- **비의존**: MCP Server
- **프롬프트 관리**: 각 도구는 `Config.tools.promptsDirectory`에서 `{toolName}.prompt.md` 파일을 로드하여 변수 치환 후 LLMRequest.prompt로 전달한다. 코드 수정 없이 프롬프트 튜닝이 가능하다.
- **코드 수정 도구 패턴**: `CodeToolBase` 추상 클래스가 code+language 입력, LLM 호출, 옵션 오버라이드 패턴을 공통화한다. 개별 도구(AddCommentsTool, RefactorCurrentCodeTool, FixCodeIssuesTool)는 이를 상속하여 프롬프트와 LlmOptions만 정의한다.
- **구현**: `ToolRegistry/` — ToolRegistryService, SummarizeCurrentCodeTool, CodeToolBase, AddCommentsTool, RefactorCurrentCodeTool, FixCodeIssuesTool, PromptTemplateLoader

## 3. LLM Connector

- **책임**: 로컬 LLM 엔드포인트와의 통신 추상화, 요청/응답 변환, 모델 선택
- **입력**: LLMRequest (`contracts.md` 참조)
- **출력**: LLMResponse (`contracts.md` 참조)
- **의존**: Configuration
- **외부 의존**: 로컬 LLM 엔드포인트 (권장: Ollama)
- **제약**: 오프라인 환경 전용. 원격 LLM 엔드포인트를 사용하지 않는다.
- **다중 모델**: 기본 모델(추론용)과 보조 모델(요약용)을 Config 기반으로 선택한다. 모델 선택 시 `string.IsNullOrEmpty` 검증으로 빈 문자열도 fallback 처리한다.
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
- **코드 변경 승인**: 코드 수정 도구 결과를 side-by-side diff로 표시. 사용자 승인(✅) 시 에디터에 적용, 거부(❌) 시 미적용. VS Undo 스택으로 자동 백업.
- **코드 적용 모드**: 선택 영역 모드(SelectionOnly)일 때 원본 텍스트를 문서에서 찾아 해당 부분만 교체. 원본 매칭 실패 시 적용 실패로 처리하고 build_test는 Skipped (§8g). 전체 파일 모드일 때 전체 교체.
- **대화 세션 백업**: 새 대화 시작 또는 이전 대화 복원 시 현재 대화를 자동 백업. 최대 20개 세션 유지. 첫 번째 사용자 메시지를 제목으로 표시.
- **코드 컨텍스트 확인**: 선택 영역 없이 코드 수정 요청 시 "현재 파일 전체를 포함할까요?" 확인 후 진행.
- **구현**: `src/LocalMcpVsExtension/` — VSIX 프로젝트 (Community.VisualStudio.Toolkit.17, Markdig). `Services/McpRestClient.cs` (Run API 클라이언트 포함), `Services/ChatMessageViewModel.cs` (ChatRunViewModel·ChatRunStageViewModel 분리, 단계별 상태 열거형), `Services/BuildTestRunner.cs` (v2.1 오프라인 빌드/테스트), `ToolWindows/SummaryToolWindowControl.cs` (Run 폴링 + 타임라인 UI + 단계별 색상 구분 + 계획/참조 섹션 카드)
- **빌드 주의**: SDK-style csproj에서 `<Import Project="Sdk.props" />` / `<Import Project="Sdk.targets" />` 를 명시적으로 분리하고 VsSDK.targets를 Sdk.targets 뒤에 import해야 pkgdef 생성과 VSIX 패키징이 동작한다. VS 2022 MSBuild로 빌드한다.

---

## Module Interaction Rules

- 모듈 간 통신은 `contracts.md`에 정의된 계약을 통해서만 수행한다.
- 다른 모듈의 내부 구현을 직접 참조하지 않는다.
- 의존 관계는 위에 명시된 것만 허용한다. 새 의존 추가 시 이 문서를 먼저 갱신한다.
- 새 모듈 추가 시 이 문서와 `contracts.md`를 먼저 갱신한다.
