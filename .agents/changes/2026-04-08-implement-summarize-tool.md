# 2026-04-08: summarize_current_code 최소 구현

## 결정 사항

- **단일 프로세스 아키텍처**: MCP Server + LLM Connector를 하나의 ASP.NET Core 프로세스로 구현
- **전송 방식**: SSE (Server-Sent Events) — `GET /sse` + `POST /message`
- **프레임워크**: C# / ASP.NET Core / .NET 9.0
- **LLM 연동**: Ollama REST API (`/api/chat` — messages 형식)
- **JSON 직렬화**: 일반 `JsonSerializerOptions` 사용 (source-generated context는 익명 타입 비호환으로 제거)
- **모델 선택**: OllamaConnector 내부에서 `string.IsNullOrEmpty` fallback (summaryModel → defaultModel → 하드코딩)
- **프롬프트 템플릿**: 외부 `.prompt.md` 파일 로드 + `{{variable}}` 치환
- **첫 구현 도구**: `summarize_current_code` (contracts.md §7a)
- **VS 2022 연동 검증 완료**: SSE 연결 → initialize → tools/list → tools/call 전체 파이프라인 동작 확인

## 변경된 파일

### 신규 생성

| 파일 | 모듈 | 설명 |
|------|------|------|
| `src/LocalMcpServer/LocalMcpServer.csproj` | — | 프로젝트 파일 (.NET 9.0) |
| `src/LocalMcpServer/Program.cs` | 전체 | DI 구성, Ollama 헬스체크, 서버 시작 |
| `src/LocalMcpServer/Configuration/ServerConfig.cs` | Configuration | 설정 모델 (contracts.md §5) |
| `src/LocalMcpServer/LlmConnector/LlmModels.cs` | LLM Connector | LlmRequest/LlmResponse 모델 |
| `src/LocalMcpServer/LlmConnector/OllamaConnector.cs` | LLM Connector | Ollama /api/chat 클라이언트 |
| `src/LocalMcpServer/ToolRegistry/IMcpTool.cs` | Tool Registry | 도구 인터페이스 |
| `src/LocalMcpServer/ToolRegistry/ToolRegistryService.cs` | Tool Registry | 도구 등록/조회 |
| `src/LocalMcpServer/ToolRegistry/SummarizeCurrentCodeTool.cs` | Tool Registry | summarize_current_code 구현 |
| `src/LocalMcpServer/ToolRegistry/PromptTemplateLoader.cs` | Tool Registry | 프롬프트 템플릿 로더 |
| `src/LocalMcpServer/McpServer/McpEndpoints.cs` | MCP Server | SSE 전송, JSON-RPC 핸들링 |
| `src/LocalMcpServer/prompts/summarize_current_code.prompt.md` | — | 코드 요약 프롬프트 (한국어) |
| `src/LocalMcpServer/appsettings.json` | Configuration | 런타임 설정 |
| `.vs/mcp.json` | — | VS 2022 MCP 연결 설정 |

### 수정

| 파일 | 변경 내용 |
|------|----------|
| `.agents/modules.md` | 각 모듈에 구현 상태(파일 경로) 추가 |
| `README.md` | 빠른 시작 가이드, 프로젝트 구조, 지원 도구 표 추가 |

## 빌드 상태

- `dotnet build` 성공 (오류 0건)

## 디버깅 이력

| 문제 | 원인 | 해결 |
|------|------|------|
| `NotSupportedException` JSON 직렬화 | source-generated `McpJsonContext`가 익명 타입 미지원 | `JsonSerializerOptions`(일반 리플렉션) 전환 |
| Ollama 404 (`/api/generate`) | Ollama 버전에 따라 `/api/generate` 미지원 | `/api/chat` (messages 형식) 전환 |
| Ollama 400 (빈 모델명) | `SummaryModel`이 null 대신 빈 문자열로 바인딩됨 | Tool에서 모델 지정 제거, OllamaConnector에 `IsNullOrEmpty` fallback |
| 요약 품질 부족 | 프롬프트가 "3~5문장" 단순 요약 유도 | 구조화된 프롬프트로 개선 (목적/구성요소/알고리즘/주의사항) |

## 완료 항목

- [x] `summarize_current_code` 도구 구현 및 VS 2022 연동 검증
- [x] Ollama `/api/chat` 연동 런타임 테스트

## 미구현 항목

- [ ] `search_project_code` 도구 (contracts.md §7b)
- [ ] `suggest_fix_from_error_log` 도구 (contracts.md §7c)
- [ ] `ask_local_docs` 도구 (contracts.md §7d)
- [ ] Resource Cache 모듈 전체
- [ ] 코드 인덱스 (정규식 심볼 추출 + 전문 텍스트 검색)
- [ ] VS 2022 Agent mode 오프라인 환경 테스트 (Copilot 없이 MCP만 사용)
- [ ] VS 2022 Agent mode 엔드투엔드 테스트
