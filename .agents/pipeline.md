# Pipeline

## Overview

시스템의 런타임 데이터 흐름을 순서대로 기술한다.  
각 단계의 `[]` 안 이름은 `modules.md`에 정의된 모듈명이다. `(외부)`는 시스템 외부 엔티티를 의미한다.

---

## Main Request Pipeline

```
1. [VS 2022 Agent Mode (외부)]
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
6a. [MCP Server] Response 구성 → VS 2022 Agent Mode 응답
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
    │  자료 조회가 필요한 경우 → (Cache Sub-Pipeline)
    ▼
7b. [Tool Registry] ToolCallResponse 반환
    │
    ▼
8b. [MCP Server] Response 구성 → VS 2022 Agent Mode 응답
```

### LLM Sub-Pipeline

Tool Registry가 도구 실행 중 LLM 호출이 필요할 때 사용한다.

```
1. [Tool Registry] → [LLM Connector] LLMRequest 전달
   │  model 필드로 기본/보조 모델 선택
   ▼
2. [LLM Connector] 프롬프트 포맷팅 및 옵션 적용
   │
   ▼
3. [LLM Connector] → [로컬 LLM Endpoint (외부, Ollama)] 호출
   │
   ▼
4. [LLM Connector] 응답 파싱 → LLMResponse 반환
```

### Cache Sub-Pipeline

Tool Registry가 도구 실행 중 현장 자료 조회가 필요할 때 사용한다.

```
1. [Tool Registry] → [Resource Cache] CacheLookupRequest 전달
   │
   ▼
2. [Resource Cache] 로컬 자료 검색
   │
   ▼
3. [Resource Cache] CacheLookupResponse 반환
```

### Code Search Sub-Pipeline

Tool Registry가 프로젝트 코드 검색이 필요할 때 사용한다.

```
1. [Tool Registry] → [Resource Cache] CodeSearchRequest 전달
   │
   ▼
2. [Resource Cache] 코드 인덱스에서 심볼/키워드 검색
   │
   ▼
3. [Resource Cache] CodeSearchResponse 반환
```

---

## Tool-Specific Flows

각 도구가 Sub-Pipeline을 어떻게 조합하는지 명시한다.  
모든 LLM 호출 도구는 실행 전 `Config.tools.promptsDirectory`에서 `{toolName}.prompt.md`를 로드하고 변수를 치환한 후 LLMRequest.prompt로 전달한다.

```
summarize_current_code:
  → 템플릿 로드 (summarize_current_code.prompt.md)
  → {{code}}, {{language}} 변수 치환
  → LLM Sub-Pipeline (summaryModel 우선)

search_project_code:
  → Code Search Sub-Pipeline (프롬프트 불필요, 인덱스 직접 검색)

suggest_fix_from_error_log:
  → 템플릿 로드 (suggest_fix_from_error_log.prompt.md)
  → (선택) Cache Sub-Pipeline (관련 문서 조회)
  → {{errorLog}}, {{codeContext}}, {{references}} 변수 치환
  → LLM Sub-Pipeline (defaultModel)

ask_local_docs:
  → Cache Sub-Pipeline (자료 검색)
  → 템플릿 로드 (ask_local_docs.prompt.md)
  → {{question}}, {{context}} 변수 치환
  → LLM Sub-Pipeline (defaultModel, 검색 결과 기반 답변 생성)
```

---

## Error Flow

```
임의 단계에서 에러 발생 시:
  → 에러를 MCP Error Response (contracts.md Response.error)로 래핑
  → VS 2022 Agent Mode 또는 REST 클라이언트에 반환
```

---

## Direct REST Pipeline

SSE 세션 없이 HTTP 요청으로 직접 도구를 호출한다. 오프라인 CLI 환경용.

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

## VS Extension Pipeline

VS 2022 Tool Window에서 코드 요약을 실행한다. Direct REST Pipeline의 UI 래퍼.

```
1. [사용자 (외부)] VS 2022 Tool Window에서 "현재 파일 요약" 또는 "선택 영역 요약" 클릭
   │
   ▼
2. [VS Extension] 활성 편집기에서 코드 텍스트 + 언어 획득
   │
   ▼
3. [VS Extension] → [MCP Server] POST /api/tools/call (contracts.md §8)
   │
   ▼
4. [MCP Server] Direct REST Pipeline 실행 → Tool Registry → LLM Sub-Pipeline
   │
   ▼
5. [VS Extension] 응답 수신 → Tool Window에 결과 표시
```

- VS Extension은 MCP Server의 REST 클라이언트로만 동작한다.
- 도구 실행 로직은 서버 측에서 처리한다.

---

## Chat Pipeline

VS 2022 채팅 UI에서 사용자 자연어 입력을 받아 의도 분석 → 도구 자동 선택 → 실행한다. (contracts.md §9, §10)

```
1. [사용자 (외부)] 채팅 메시지 입력
   │
   ▼
2. [VS Extension] 현재 에디터 코드 + 언어 + 선택 영역 정보 수집
   │  선택 영역 없이 코드 수정 요청 시 → "현재 파일 전체를 포함할까요?" 확인
   │
   ▼
3. [VS Extension] → [MCP Server] POST /api/chat (ChatRequest)
   │
   ▼
4. [MCP Server / IntentResolver] 의도 분석
   │  → 프롬프트 로드 (intent_analysis.prompt.md)
   │  → {{message}}, {{tools}}, {{language}} 변수 치환
   │  → [LLM Connector] 의도 분석 프롬프트 전송
   │  ← JSON 파싱: {toolName, confidence, description}
   │
   ├─ toolName != null (confidence ≥ 0.5)
   │  ▼
   │  5a. [MCP Server] → [Tool Registry] ToolCallRequest
   │      │  (기존 Tool Call Flow §4b 재활용)
   │      ▼
   │  6a. 코드 수정 도구 → ChatResponse (result + codeChange, requiresApproval=true)
   │      비수정 도구 → ChatResponse (result only, requiresApproval=false)
   │
   └─ toolName == null (confidence < 0.5)
      ▼
      5b. [MCP Server / IntentResolver] 일반 대화 응답 생성
          │  → 프롬프트 로드 (general_chat.prompt.md)
          │  → {{message}}, {{code}}, {{language}}, {{history}} 변수 치환
          │  → [LLM Connector] 일반 대화 프롬프트 전송
          ▼
      6b. ChatResponse (result only, requiresApproval=false)
   │
   ▼
7. [VS Extension] ChatResponse 수신 → 채팅 메시지로 표시 (Markdown 렌더링)
   │
   ├─ requiresApproval == true
   │  → side-by-side diff 뷰로 코드 변경 표시
   │  → "✅ 승인" / "❌ 거부" 인라인 버튼 표시
   │  → 승인 시: 에디터에 코드 적용
   │            선택 영역 모드(SelectionOnly) → 원본 텍스트를 문서에서 찾아 해당 부분만 교체
   │            전체 파일 모드 → ITextBuffer.CreateEdit로 전체 교체 (Undo 스택 자동 등록)
   │            + POST /api/chat/approve (approved=true)
   │  → 거부 시: POST /api/chat/approve (approved=false)
   │
   └─ requiresApproval == false
      → 결과만 표시
```

- 대화 이력은 서버 ConversationStore에 저장되며 LLM 컨텍스트에 포함된다.
- conversationId로 대화 세션을 식별한다 (null이면 새 대화 생성).
- ConversationTimeoutMinutes (기본 30분) 경과 시 서버가 자동 정리한다.

---

## Chat Session Backup/Restore Flow

VS Extension 내 코드 정규화 및 세션 백업 흐름.

```
새 대화 시작 또는 이전 대화 선택 시:
  1. [VS Extension] 현재 대화에 사용자 메시지가 있으면 BackupCurrentSession() 호출
     → 메시지 목록 스냅샷 (UI 참조 제외) + conversationId + 첫 사용자 메시지를 제목으로 저장
     → 동일 conversationId 기존 백업이 있으면 갱신, 없으면 새로 추가
     → 최대 20개 유지 (FIFO)
  2. 이력 ComboBox 갱신 ([HH:mm] 제목 형식)

이전 대화 복원 시:
  1. [VS Extension] BackupCurrentSession() → 현재 대화 저장
  2. RestoreSession(session) → 선택된 세션의 메시지 재렌더링
     → 승인/거부 상태가 있는 메시지는 버튼 비활성화
     → conversationId 복원으로 서버 측 대화 이어가기 가능
```

코드 적용 시 정규화 흐름:

```
1. NormalizeNewlines(코드, snapshot) → 문서의 줄바꿈 형식(\r\n / \n) 감지 → 일괄 변환
2. NormalizeIndentation(코드, snapshot) → 문서의 들여쓰기 형식(탭/스페이스) 감지
   → LLM 출력과 문서 형식이 다르면 변환 (문서의 줄바꿈 형식에 맞춰 join)
3. SelectionOnly 모드 → 원본 텍스트를 문서에서 IndexOf로 찾아 해당 부분만 Replace
   전체 파일 모드 → 전체 Replace
```

---

## Startup Sequence

```
1. [Configuration] 설정 로드 (파일 / 환경 변수)
2. [Resource Cache] 초기화
   a. 캐시 디렉터리 로드
   b. 코드 인덱스 구축 (하이브리드: 심볼 추출 + 텍스트 역인덱스)
3. [Tool Registry] 초기화
   a. 도구 4종 등록
   b. 프롬프트 템플릿 로드 (Config.tools.promptsDirectory)
4. [LLM Connector] 초기화 (로컬 LLM 엔드포인트 연결 확인, 모델 가용성 확인)
5. [MCP Server] 시작 (Config.server.transport에 따라 리스닝)
```
