# local-MCP-server-with-LLM

인터넷이 없는 현장에서 Visual Studio 2022 안에서 최소한의 Agent형 코딩 보조 기능을 제공하는 로컬 MCP 서버.

## 개요

- **목적**: 오프라인 환경에서 로컬 LLM + 로컬 MCP 서버 + 현장 자료 캐시로 코딩 보조 대응력 확보
- **클라이언트**: Visual Studio 2022 (17.14 이상) Agent mode
- **LLM 런타임**: Ollama (`/api/chat` 엔드포인트)
- **기본 모델**: qwen2.5-coder:7b
- **전송 방식**: SSE (Server-Sent Events)
- **상태**: `summarize_current_code` 도구 구현 및 VS 2022 연동 검증 완료
- **비목표**: GitHub Copilot 대체

## 구성

| 구성요소 | 역할 |
|----------|------|
| MCP Server | VS 2022 Agent mode의 MCP 요청을 수신·응답 |
| Tool Registry | MCP 도구 등록·조회·실행 |
| LLM Connector | 로컬 LLM과의 통신 추상화 |
| Resource Cache | 현장 필수 자료(문서, 표준, 참조)의 로컬 조회 |
| Configuration | 서버·모델·도구·캐시 설정 중앙 관리 |

## 요구사항

- Visual Studio 2022 **17.14 이상**
- .NET 9.0 SDK
- 로컬 LLM 엔드포인트 (권장: Ollama + Qwen 계열)
- 현장 자료 캐시 (사전 준비 필요)

## 빠른 시작

### 1. Ollama 실행

```bash
ollama serve
ollama pull qwen2.5-coder:7b
```

### 2. MCP 서버 실행

```bash
cd src/LocalMcpServer
dotnet run
```

서버가 `http://localhost:5100` 에서 시작된다.

### 3. VS 2022 연결

솔루션 루트의 `.vs/mcp.json` 이 이미 설정되어 있다. VS 2022에서 솔루션을 열고 Agent mode 채팅에서 MCP 도구를 사용할 수 있다.

### 현재 지원 도구

| 도구 | 설명 | 상태 |
|------|------|------|
| `summarize_current_code` | 코드 텍스트를 받아 한국어로 구조화된 요약 | ✅ 구현 완료 |
| `search_project_code` | 프로젝트 내 코드 검색 | 🔲 미구현 |
| `suggest_fix_from_error_log` | 에러 로그 기반 수정 제안 | 🔲 미구현 |
| `ask_local_docs` | 현장 문서 질의응답 | 🔲 미구현 |

## VS 2022 MCP 연결 설정

### 방법 1: 솔루션별 설정 (권장)

솔루션 루트에 `.vs/mcp.json` 파일을 생성한다:

```json
{
  "servers": {
    "local-mcp": {
      "type": "sse",
      "url": "http://localhost:5100/sse"
    }
  }
}
```

- `type`: 전송 방식 (`"sse"` 또는 `"stdio"`)
- `url`: MCP 서버 엔드포인트 URL (SSE 방식인 경우)
- `.vs/mcp.json`은 솔루션을 여는 모든 사용자에게 적용된다. Git에 포함하면 팀 공유 가능.

### 방법 2: VS 사용자 설정

1. **Tools → Options → GitHub Copilot → MCP Servers** 로 이동
2. **Add Server** 클릭
3. 서버 이름, 전송 방식, URL을 입력
4. 이 설정은 해당 VS 인스턴스의 모든 솔루션에 적용된다.

> **참고**: Agent mode에서 MCP 도구를 사용하려면 채팅 창의 모드를 "Agent"로 전환해야 한다.

## 프로젝트 구조

```
src/LocalMcpServer/
  .gitignore                        — Git 제외 설정 (bin/, obj/ 등)
  Program.cs                       — 진입점, DI 구성, 서버 시작
  Configuration/ServerConfig.cs    — 설정 모델 (contracts.md §5)
  LlmConnector/LlmModels.cs        — LLM 요청/응답 모델 (contracts.md §3)
  LlmConnector/OllamaConnector.cs  — Ollama /api/chat 클라이언트
  ToolRegistry/IMcpTool.cs          — 도구 인터페이스
  ToolRegistry/ToolRegistryService.cs — 도구 등록·조회
  ToolRegistry/SummarizeCurrentCodeTool.cs — summarize_current_code 구현
  ToolRegistry/PromptTemplateLoader.cs — 프롬프트 템플릿 로더
  McpServer/McpEndpoints.cs         — MCP SSE 엔드포인트 (JSON-RPC 2.0)
  prompts/                          — 프롬프트 템플릿 파일 (코드 수정 없이 튜닝 가능)
  appsettings.json                  — 서버 설정
.vs/mcp.json                        — VS 2022 MCP 연결 설정
```

## 프롬프트 튜닝

`src/LocalMcpServer/prompts/` 디렉터리의 `.prompt.md` 파일을 수정하면 서버 재시작 없이 프롬프트가 반영된다. `{{variable}}` 형식의 변수 치환을 지원한다.

## 알려진 제한사항

- VS 2022 Agent mode는 **GitHub Copilot 확장이 설치**되어 있어야 MCP 도구를 사용할 수 있다
- 현재 Copilot의 클라우드 모델이 MCP 도구 호출 여부를 판단하므로, **완전 오프라인 환경에서는 별도 MCP 클라이언트가 필요**하다
- 7B 모델의 요약 품질은 제한적이다. 더 큰 모델(14b+)을 사용하면 품질이 향상된다

## 문서

프로젝트 설계와 규칙은 `.agents/` 디렉터리에서 관리한다:

- `.agents/system.md` — 시스템 정의·경계·원칙
- `.agents/modules.md` — 모듈 구성·책임·의존
- `.agents/contracts.md` — 모듈 간 데이터 계약
- `.agents/pipeline.md` — 런타임 데이터 흐름
- `.agents/rules.md` — 변경 규칙