# 변경 스펙: CLI 직접 호출 지원

## 목적

현재 MCP 서버는 SSE 전송 방식만 지원하며, VS 2022 Copilot Agent가 유일한 클라이언트이다.
오프라인 환경에서 Copilot 없이도 MCP 도구를 직접 호출할 수 있도록, **동기 REST 엔드포인트**를 MCP Server에 추가한다.

## 배경

| 구분 | 현재 | 변경 후 |
|------|------|---------|
| 클라이언트 | VS 2022 Copilot Agent (온라인 필수) | + PowerShell/curl/CLI (오프라인 가능) |
| 전송 방식 | SSE only | SSE + 동기 REST |
| 도구 호출 판단 | Copilot 클라우드 모델 | 사용자가 직접 도구 선택 |
| MCP 서버 변경 | — | `POST /api/tools/call`, `GET /api/tools/list` 추가 |

## 설계 원칙

- **MCP 서버 코드 내부에 추가**: 새 모듈을 만들지 않는다.
- **기존 SSE 엔드포인트 유지**: VS 2022 연동은 그대로 동작한다.
- **Tool Registry 재사용**: 동기 REST 엔드포인트도 동일한 ToolRegistryService를 통해 도구를 실행한다.
- **독립 검증 가능**: PowerShell 한 줄로 테스트 가능해야 한다.

---

## 1. contracts.md 변경

### §1 수정: 클라이언트 범위 확장

현재:
> MCP 프로토콜(JSON-RPC 2.0) 기반 통신. 클라이언트는 Visual Studio 2022 (17.14+) Agent mode이다.

변경:
> MCP 프로토콜(JSON-RPC 2.0) 기반 통신과 동기 REST API를 지원한다.
> - SSE 클라이언트: Visual Studio 2022 (17.14+) Agent mode
> - REST 클라이언트: PowerShell, curl, 또는 임의 HTTP 클라이언트 (오프라인 환경용)

### §8 신규: Direct REST API

```
--- 8. Direct REST API ---

GET /api/tools/list

Response {
  tools: [
    {
      name: string
      description: string
      inputSchema: object
    }
  ]
}

---

POST /api/tools/call
Content-Type: application/json

Request {
  name: string          // 호출할 도구 이름
  arguments: object     // 도구 입력 인자
}

Response {
  content: [
    {
      type: string      // "text"
      text: string      // 결과 텍스트
    }
  ]
  error: string | null  // 에러 발생 시 메시지
}
```

- ToolCallRequest/ToolCallResponse (§2)와 동일한 데이터를 단순 REST로 래핑한 것이다.
- JSON-RPC 프레이밍, SSE 세션이 불필요하다.

---

## 2. modules.md 변경

### §1 MCP Server 수정

추가:
> - **동기 REST**: `GET /api/tools/list`, `POST /api/tools/call` — SSE 세션 없이 직접 도구 실행. 오프라인 CLI 호출용.

구현 항목에 추가:
> `McpServer/McpEndpoints.cs` — SSE 전송 방식 + 동기 REST 엔드포인트

---

## 3. pipeline.md 변경

### Direct REST Pipeline 추가

```
1. [CLI 클라이언트 (외부)]
   │  HTTP 요청 전송 (REST)
   ▼
2. [MCP Server] 요청 수신
   ├─ GET /api/tools/list  → Tool Registry.ListTools() → JSON 응답
   └─ POST /api/tools/call → Tool Registry.GetTool() → ExecuteAsync() → JSON 응답
```

- 기존 Main Request Pipeline, LLM Sub-Pipeline은 변경 없음.
- MCP Server 내부에서 Tool Registry를 호출하는 경로는 동일.

---

## 4. 코드 변경 범위

### 변경 파일: 1개

| 파일 | 변경 내용 |
|------|----------|
| `McpServer/McpEndpoints.cs` | `MapMcpEndpoints()`에 `GET /api/tools/list`, `POST /api/tools/call` 2개 엔드포인트 추가 |

### 변경하지 않는 파일

- `Program.cs` — 변경 없음 (기존 `app.MapMcpEndpoints()` 호출이 새 엔드포인트도 포함)
- `ToolRegistry/*` — 변경 없음 (기존 서비스 그대로 호출)
- `LlmConnector/*` — 변경 없음
- `appsettings.json` — 변경 없음

### 추가 구현 사항

`McpEndpoints.cs`에 아래 2개 엔드포인트 추가:

```
GET /api/tools/list
  → registry.ListTools() 호출
  → JSON 배열 반환

POST /api/tools/call
  → Body에서 name, arguments 파싱
  → registry.GetTool(name) → tool.ExecuteAsync(arguments)
  → ToolCallResponse 형식으로 JSON 반환
  → 에러 시 { error: message } 반환, HTTP 200 유지 (클라이언트 처리 편의)
```

---

## 5. 테스트 방법

### 도구 목록 조회

```powershell
Invoke-RestMethod http://localhost:5100/api/tools/list
```

### 코드 요약 호출 (파일 내용 직접 전달)

```powershell
$code = Get-Content "C:\MyProject\MyClass.cs" -Raw
$body = @{ name = "summarize_current_code"; arguments = @{ code = $code; language = "csharp" } } | ConvertTo-Json
Invoke-RestMethod http://localhost:5100/api/tools/call -Method POST -ContentType "application/json" -Body $body
```

### 파이프라인 조합 (한 줄)

```powershell
@{ name="summarize_current_code"; arguments=@{ code=(Get-Content .\MyClass.cs -Raw); language="csharp" } } | ConvertTo-Json | Invoke-RestMethod http://localhost:5100/api/tools/call -Method POST -ContentType "application/json" -Body { $input }
```

---

## 6. 완료 조건

- [x] `GET /api/tools/list`가 도구 목록을 JSON으로 반환한다
- [x] `POST /api/tools/call`이 `summarize_current_code` 도구를 실행하고 결과를 반환한다
- [x] 기존 SSE 엔드포인트(`GET /sse`, `POST /message`)가 변경 없이 동작한다
- [x] PowerShell에서 위 테스트 명령이 성공한다
- [x] `.agents/` 문서(contracts, modules, pipeline)가 갱신된다
- [x] README.md에 REST API 사용법, 접속 방식 비교, API 레퍼런스 반영

---

## 7. 향후 확장 (이번 범위 아님)

이번 변경은 "가장 빠르고 단순한 테스트"를 위한 것이다. 이후 필요에 따라:

| 확장 | 설명 |
|------|------|
| 대화형 CLI 앱 | 메뉴 선택 → 파일 경로 입력 → 도구 실행 → 결과 출력 루프 |
| 파일 쓰기 도구 | `apply_code_edit` MCP 도구 추가 (자동 코드 수정) |
| 로컬 도구 선택 LLM | 사용자 질문을 로컬 LLM이 분석하여 적절한 도구 자동 선택 |
| VS Code 확장 | VS Code 내 채팅 패널에서 로컬 MCP 도구 직접 호출 |
