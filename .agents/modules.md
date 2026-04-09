# Modules

## Overview

시스템은 아래 5개 모듈로 구성된다. 각 모듈은 명확한 책임을 가지며 독립적으로 구현·교체할 수 있다.

---

## 1. MCP Server

- **책임**: MCP 프로토콜 준수, VS 2022 Agent mode 요청 수신, 메서드 라우팅, 응답 반환
- **입력**: MCP 클라이언트 요청 (JSON-RPC) — 클라이언트는 VS 2022 Agent mode 또는 REST 클라이언트
- **출력**: MCP 프로토콜 응답 (JSON-RPC) 또는 REST JSON 응답
- **의존**: Tool Registry, Configuration
- **비의존**: LLM Connector (직접 호출하지 않음), Resource Cache (직접 접근하지 않음)
- **동기 REST**: `GET /api/tools/list`, `POST /api/tools/call` — SSE 세션 없이 직접 도구 실행. 오프라인 CLI 호출용.
- **구현**: `McpServer/McpEndpoints.cs` — SSE 전송 방식 (`GET /sse`, `POST /message`) + 동기 REST 엔드포인트, JSON 직렬화는 `JsonSerializerOptions`(camelCase) 사용

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
- **구현**: 미구현 — 다음 단계에서 구현 예정

## 5. Configuration

- **책임**: 서버, 모델, 도구, 캐시, 코드 인덱스 설정의 중앙 관리
- **입력**: 설정 파일 또는 환경 변수
- **출력**: Config 객체 (`contracts.md` 참조)
- **의존**: 없음
- **구현**: `Configuration/ServerConfig.cs` + `appsettings.json`

## 6. VS Extension (VSIX)

- **책임**: VS 2022 내 Tool Window UI 제공, 현재 편집 파일/선택 영역 코드 획득, MCP Server REST API 호출, 결과 표시
- **입력**: 사용자 버튼 클릭 (현재 파일, 선택 영역) + 도구 선택 (서버에서 동적 로드)
- **출력**: Tool Window에 Markdown 렌더링된 결과 표시 (FlowDocument)
- **의존**: MCP Server (REST API — contracts.md §8: GET /api/tools/list + POST /api/tools/call)
- **비의존**: Tool Registry, LLM Connector, Resource Cache, Configuration (모두 서버 측)
- **제약**: VS 2022 17.14+ 전용, .NET Framework 4.8, 오프라인 환경에서도 동작 (서버가 로컬이므로)
- **VS 테마**: VsBrushes + VSColorTheme 기반 Dark/Light/Blue 자동 대응
- **Markdown 렌더링**: Markdig AST → WPF FlowDocument 변환 (헤딩, 리스트, 코드블록, 볼드/이탤릭, 인용)
- **동적 도구 로딩**: 시작 시 GET /api/tools/list로 서버 도구 목록 조회 → ComboBox에 표시. 서버에 도구 추가 시 VSIX 재설치 불필요.
- **코드 적용 기능**: 코드 수정 도구(add_comments, refactor_current_code, fix_code_issues) 실행 결과를 "📋 적용" 버튼으로 에디터에 반영. 선택 영역이 있으면 선택 영역만, 없으면 전체 파일을 교체한다. LLM 응답에서 마지막 코드 펜스 블록을 추출하여 순수 코드만 적용한다.
- **구현**: `src/LocalMcpVsExtension/` — VSIX 프로젝트 (Community.VisualStudio.Toolkit.17, Markdig)
- **빌드 주의**: SDK-style csproj에서 `<Import Project="Sdk.props" />` / `<Import Project="Sdk.targets" />` 를 명시적으로 분리하고 VsSDK.targets를 Sdk.targets 뒤에 import해야 pkgdef 생성과 VSIX 패키징이 동작한다. VS 2022 MSBuild로 빌드한다.

---

## Module Interaction Rules

- 모듈 간 통신은 `contracts.md`에 정의된 계약을 통해서만 수행한다.
- 다른 모듈의 내부 구현을 직접 참조하지 않는다.
- 의존 관계는 위에 명시된 것만 허용한다. 새 의존 추가 시 이 문서를 먼저 갱신한다.
- 새 모듈 추가 시 이 문서와 `contracts.md`를 먼저 갱신한다.
