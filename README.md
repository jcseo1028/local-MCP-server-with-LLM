# local-MCP-server-with-LLM

인터넷이 없는 현장에서 Visual Studio 2022 안에서 최소한의 Agent형 코딩 보조 기능을 제공하는 로컬 MCP 서버.

## 개요

- **목적**: 오프라인 환경에서 로컬 LLM + 로컬 MCP 서버 + 현장 자료 캐시로 코딩 보조 대응력 확보
- **클라이언트**: Visual Studio 2022 (17.14 이상) Agent mode
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
- 로컬 LLM 엔드포인트 (권장: Ollama + Qwen 계열)
- 현장 자료 캐시 (사전 준비 필요)

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

## 문서

프로젝트 설계와 규칙은 `.agents/` 디렉터리에서 관리한다:

- `.agents/system.md` — 시스템 정의·경계·원칙
- `.agents/modules.md` — 모듈 구성·책임·의존
- `.agents/contracts.md` — 모듈 간 데이터 계약
- `.agents/pipeline.md` — 런타임 데이터 흐름
- `.agents/rules.md` — 변경 규칙