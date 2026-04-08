# Pipeline

## Overview

시스템의 런타임 데이터 흐름을 순서대로 기술한다.  
각 단계의 `[]` 안 이름은 `modules.md`에 정의된 모듈명이다. `(외부)`는 시스템 외부 엔티티를 의미한다.

---

## Main Request Pipeline

```
1. [Client (외부)]
   │  MCP 요청 전송 (JSON-RPC)
   ▼
2. [MCP Server] 요청 수신 및 파싱
   │  프로토콜 유효성 검증
   ▼
3. [MCP Server] 메서드 라우팅
   ├─ tools/list  → (4a)
   ├─ tools/call  → (4b)
   └─ 미지원 메서드 → Error Response 반환
```

### 4a. Tool List Flow

```
4a. [MCP Server] → [Tool Registry] ToolListRequest 전달
    │
    ▼
5a. [Tool Registry] 등록된 도구 목록 반환 (ToolListResponse)
    │
    ▼
6a. [MCP Server] Response 구성 → Client 응답
```

### 4b. Tool Call Flow

```
4b. [MCP Server] → [Tool Registry] ToolCallRequest 전달
    │
    ▼
5b. [Tool Registry] 도구 이름으로 핸들러 조회, 입력 검증
    │
    ▼
6b. [Tool Registry] 도구 실행
    │  LLM 호출이 필요한 경우 → (LLM Sub-Pipeline)
    ▼
7b. [Tool Registry] ToolCallResponse 반환
    │
    ▼
8b. [MCP Server] Response 구성 → Client 응답
```

### LLM Sub-Pipeline

Tool Registry가 도구 실행 중 LLM 호출이 필요할 때 사용한다.

```
1. [Tool Registry] → [LLM Connector] LLMRequest 전달
   │
   ▼
2. [LLM Connector] 프롬프트 포맷팅 및 옵션 적용
   │
   ▼
3. [LLM Connector] → [LLM Endpoint (외부)] 호출
   │
   ▼
4. [LLM Connector] 응답 파싱 → LLMResponse 반환
```

---

## Error Flow

```
임의 단계에서 에러 발생 시:
  → 에러를 MCP Error Response (contracts.md Response.error)로 래핑
  → Client에 반환
```

---

## Startup Sequence

```
1. [Configuration] 설정 로드 (파일 / 환경 변수)
2. [Tool Registry] 초기화 (도구 스캔 및 등록)
3. [LLM Connector] 초기화 (엔드포인트 연결 확인)
4. [MCP Server] 시작 (Config.server.transport에 따라 리스닝)
```
