# Contracts

## Overview

모듈 간 데이터 교환 인터페이스를 정의한다.  
구현 전에 계약을 먼저 정의하며, 변경 시에도 이 문서를 우선 갱신한다.

각 계약의 호출 방향은 `→`로 표시한다. `↔`는 양방향 통신을 의미한다.

---

## 1. Client ↔ MCP Server

MCP 프로토콜(JSON-RPC 2.0) 기반 통신.

```
Request {
  method: string       // MCP 메서드명 (예: "tools/list", "tools/call")
  params: object       // 메서드별 파라미터
  id: string | number  // 요청 식별자
}

Response {
  result: object       // 성공 시 결과
  error: {             // 실패 시 에러 정보 (없으면 null)
    code: number
    message: string
  } | null
  id: string | number  // 요청 식별자
}
```

## 2. MCP Server → Tool Registry

MCP Server가 도구 목록 조회 및 도구 실행을 Tool Registry에 위임한다.

```
ToolListRequest {}

ToolListResponse {
  tools: [
    {
      name: string          // 도구 고유 이름
      description: string   // 도구 설명
      inputSchema: object   // JSON Schema 형식 입력 정의
    }
  ]
}

ToolCallRequest {
  name: string       // 호출할 도구 이름
  arguments: object  // 도구 입력 인자 (inputSchema 준수)
}

ToolCallResponse {
  content: [
    {
      type: string   // "text"
      text: string   // 결과 텍스트
    }
  ]
}
```

## 3. Tool Registry → LLM Connector

Tool Registry가 도구 실행 중 LLM 호출이 필요할 때 사용한다. MCP Server는 LLM Connector를 직접 호출하지 않는다.

```
LLMRequest {
  prompt: string          // 프롬프트 텍스트
  context: object | null  // 추가 컨텍스트 (도구별 정의)
  options: {
    temperature: number   // 0.0 ~ 1.0
    maxTokens: number     // 최대 토큰 수
  }
}

LLMResponse {
  text: string            // LLM 응답 텍스트
  usage: {                // 선택 필드 — LLM 제공자가 지원하는 경우에만 포함
    promptTokens: number | null
    completionTokens: number | null
  } | null
}
```

## 4. Configuration Schema

모든 모듈이 Configuration 모듈을 통해 설정을 읽는다.

```
Config {
  server: {
    host: string          // 바인드 주소
    port: number          // 포트 번호
    transport: string     // 전송 방식 식별자 (예: "stdio", "sse")
  }
  llm: {
    provider: string      // LLM 제공자 식별자
    endpoint: string      // LLM 엔드포인트 URL
    model: string         // 모델명
  }
  tools: {
    directory: string     // 도구 정의 디렉터리 경로
  }
}
```

---

## Contract Rules

- 모듈 간 통신은 이 문서에 정의된 계약만 사용한다.
- 계약에 없는 필드를 암묵적으로 추가하지 않는다.
- 계약 변경 시 이 문서를 먼저 갱신하고, `modules.md`를 확인한 후, 구현을 수정한다.
- 새 모듈 간 통신이 필요하면 이 문서에 계약을 먼저 추가한다.
