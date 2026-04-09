# Contracts

## Overview

모듈 간 데이터 교환 인터페이스를 정의한다.  
구현 전에 계약을 먼저 정의하며, 변경 시에도 이 문서를 우선 갱신한다.

각 계약의 호출 방향은 `→`로 표시한다. `↔`는 양방향 통신을 의미한다.

---

## 1. MCP 클라이언트 ↔ MCP Server

MCP 프로토콜(JSON-RPC 2.0) 기반 통신과 동기 REST API를 지원한다.

- **SSE 클라이언트**: Visual Studio 2022 (17.14+) Agent mode
- **REST 클라이언트**: PowerShell, curl, 또는 임의 HTTP 클라이언트 (오프라인 환경용)

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
  model: string | null    // 사용할 모델 네임 (null이면 Config.llm.defaultModel 사용)
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

## 4. Tool Registry → Resource Cache

Tool Registry가 도구 실행 중 현장 자료 조회 또는 코드 검색이 필요할 때 사용한다. MCP Server는 Resource Cache를 직접 접근하지 않는다.

### 4a. 자료 조회

```
CacheLookupRequest {
  query: string           // 검색 질의 텍스트
  category: string | null // 자료 분류 필터 (null이면 전체 검색)
  maxResults: number      // 최대 반환 건수
}

CacheLookupResponse {
  results: [
    {
      title: string       // 자료 제목
      content: string     // 자료 본문 (텍스트)
      source: string      // 자료 출처 식별자 (파일 경로 또는 문서명)
      category: string    // 자료 분류
    }
  ]
}
```

### 4b. 코드 검색

```
CodeSearchRequest {
  query: string           // 검색 키워드 (클래스명, 함수명, 식별자 등)
  scope: string | null    // 검색 범위 (파일 경로 패턴, null이면 전체 프로젝트)
  maxResults: number      // 최대 반환 건수
}

CodeSearchResponse {
  results: [
    {
      filePath: string    // 소스 파일 경로
      symbol: string      // 매칭된 심볼 (클래스명, 함수명 등)
      lineNumber: number  // 시작 줄 번호
      snippet: string     // 주변 코드 스니펫
    }
  ]
}
```

## 5. Configuration Schema

모든 모듈이 Configuration 모듈을 통해 설정을 읽는다.

```
Config {
  server: {
    host: string          // 바인드 주소
    port: number          // 포트 번호
    transport: string     // 전송 방식 식별자 (예: "stdio", "sse")
  }
  llm: {
    provider: string      // 로컬 LLM 제공자 식별자 (권장: "ollama")
    endpoint: string      // 로컬 LLM 엔드포인트 URL
    defaultModel: string  // 기본 모델명 (추론용, 권장: Qwen 계열)
    summaryModel: string | null  // 보조 요약 모델명 (선택, 권장: Gemma 계열, null이면 defaultModel 사용)
  }
  tools: {
    directory: string     // 도구 정의 디렉터리 경로
    promptsDirectory: string // 프롬프트 템플릿 디렉터리 경로
  }
  cache: {
    directory: string     // 캐시 자료 디렉터리 경로
    categories: [string]  // 사용 가능한 자료 분류 목록
  }
  codeIndex: {
    rootPath: string      // 프로젝트 루트 경로
    filePatterns: [string] // 색인 대상 파일 패턴 (예: ["*.cs", "*.xaml"])
    strategy: string      // 색인 전략: "hybrid" (default) | "text" | "ast"
  }
}
```

---

## 7. MCP Tool Definitions

Tool Registry에 등록되는 최소 도구 4종의 이름, 설명, 입출력을 정의한다.  
각 도구의 `inputSchema`와 출력은 아래를 따른다.

### 7a. summarize_current_code

현재 파일 또는 선택 영역의 코드를 요약한다.

```
Input {
  code: string            // 요약 대상 코드 텍스트
  language: string | null // 프로그래밍 언어 (null이면 자동 감지)
}

Output {
  summary: string         // 코드 요약 텍스트
}
```

- **사용 모듈**: LLM Connector (모델 선택은 OllamaConnector에 위임 — summaryModel → defaultModel → 하드코딩 fallback)
- **프롬프트**: `prompts/summarize_current_code.prompt.md` — 구조화된 요약 (목적, 구성요소, 알고리즘, 주의사항)

### 7b. search_project_code

프로젝트 내부의 클래스, 함수, 키워드를 검색한다.

```
Input {
  query: string           // 검색 키워드
  scope: string | null    // 검색 범위 (파일 경로 패턴, null이면 전체)
  maxResults: number | null // 최대 결과 수 (null이면 기본값 사용)
}

Output {
  results: [
    {
      filePath: string    // 소스 파일 경로
      symbol: string      // 매칭된 심볼
      lineNumber: number  // 시작 줄 번호
      snippet: string     // 주변 코드 스니펫
    }
  ]
}
```

- **사용 모듈**: Resource Cache (CodeSearchRequest)

### 7c. suggest_fix_from_error_log

예외 로그 또는 에러 메시지를 기반으로 수정 방향을 제안한다.

```
Input {
  errorLog: string        // 에러 로그 또는 예외 메시지 텍스트
  codeContext: string | null // 관련 코드 컨텍스트 (null이면 에러 로그만 사용)
}

Output {
  suggestion: string      // 수정 방향 제안 텍스트
  references: [           // 관련 참조 자료 (있는 경우)
    {
      title: string
      source: string
    }
  ]
}
```

- **사용 모듈**: LLM Connector (defaultModel), 선택적으로 Resource Cache (관련 문서 조회)

### 7d. ask_local_docs

현장 대응 문서(PDF, Excel, 체크리스트 등)에 대해 질의응답한다.

```
Input {
  question: string        // 질의 텍스트
  category: string | null // 자료 분류 필터 (null이면 전체)
}

Output {
  answer: string          // 질의 응답 텍스트
  sources: [              // 답변 근거 자료
    {
      title: string
      source: string
      category: string
    }
  ]
}
```

- **사용 모듈**: Resource Cache (CacheLookupRequest) → LLM Connector (defaultModel, 검색 결과 기반 답변 생성)

---

## 8. Direct REST API

SSE 세션 없이 MCP 도구를 직접 호출하는 동기 REST 엔드포인트. 오프라인 CLI 호출용.

```
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
```

```
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

## Contract Rules

- 모듈 간 통신은 이 문서에 정의된 계약만 사용한다.
- 계약에 없는 필드를 암묵적으로 추가하지 않는다.
- 계약 변경 시 이 문서를 먼저 갱신하고, `modules.md`를 확인한 후, 구현을 수정한다.
- 새 모듈 간 통신이 필요하면 이 문서에 계약을 먼저 추가한다.
