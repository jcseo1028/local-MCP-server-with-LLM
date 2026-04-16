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
   │  model 필드로 코드/일반/보조 모델 선택
   │  (코드 도구 → DefaultModel, 의도분석·계획·대화·요약 → GeneralModel)
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

## Chat Run Pipeline (v2.1)

Chat Pipeline을 다단계 오케스트레이션으로 확장한다. (contracts.md §11)  
상세 스펙: `.agents/changes/2026-04-16-vsix-chat-orchestration-spec.md`

```
1. [사용자 (외부)] 채팅 메시지 입력
   │
   ▼
2. [VS Extension] 컨텍스트 수집
   │  활성 파일 경로, 코드, 언어, 선택 영역, 솔루션 경로, 진단 정보
   │
   ▼
3. [VS Extension] → [MCP Server] POST /api/chat/runs (ChatRunStartRequest)
   │  ← runId + conversationId 수신
   │
   ▼
4. [MCP Server / RunOrchestrator] 비동기 단계 실행 시작
   │
   ├─ 4a. intent_analysis (의도분석)
   │      → IntentResolver (기존 의도 분석 + build/test 필요 여부 추론)
   │      → LLM Connector (로컬 전용, GeneralModel 사용)
   │      ← {toolName, confidence, description, needsBuildTest}
   │
   ├─ 4b. planning (계획수립)
   │      → IntentResolver.GeneratePlan()
   │      → LLM Connector (로컬 전용, GeneralModel 사용)
   │      ← planItems (최대 5개)
   │
   ├─ 4c. context_collection (컨텍스트수집)
   │      → 서버 측 컨텍스트 정규화 (코드 크기 검증 — 32KB 초과 시 절단, 부족 정보 판단)
   │
   ├─ 4d. document_search (문서검색)
   │      → DocumentSearcher → Resource Cache (로컬 파일만)
   │      → Config.documentSearch.directories 범위 내 검색
   │      → 문서 없으면 Skipped 처리
   │
   ├─ 4e. proposal_generation (수정안생성)
   │      ├─ toolName != null → [Tool Registry] ToolCallRequest (기존 Tool Call Flow 재활용, 코드 도구는 DefaultModel)
   │      └─ toolName == null → [LLM Connector] 일반 대화 응답 생성 (GeneralModel)
   │      → 코드 수정 도구이면 proposal.requiresApproval=true
   │
   ▼
5. [MCP Server] 상태 → WaitingForApproval 또는 Running.Summarizing
   │
   ▼
6. [VS Extension] GET /api/chat/runs/{runId} 폴링 (500ms~1s)
   │  → 단계 타임라인 실시간 갱신
   │
   ├─ WaitingForApproval 감지 시
   │  → diff 미리보기 + "✅ 승인" / "❌ 거부" 버튼 표시
   │  → 승인: POST /api/chat/runs/{runId}/approval (approved=true)
   │  → 거부: POST /api/chat/runs/{runId}/approval (approved=false) → Rejected
   │
   ▼
7. [VS Extension] 승인 후 클라이언트 측 실행
   │
   ├─ 7a. applying (적용)
   │      → ITextBuffer.CreateEdit (Undo 스택 자동 등록)
   │      → SelectionOnly/전체 파일 모드 분기
   │      → 원본 텍스트 매칭 실패 시 적용 실패 처리 (edit 취소)
   │      → 적용 실패/예외 시 build_test Skipped + Applied=false 보고
   │
   ├─ 7b. build_test (빌드/테스트)
   │      → 빌드: --no-restore 오프라인 모드 (패키지 복원 금지)
   │      → 테스트: 네이밍 기반 필터로 네트워크 의존 테스트 제외
   │      → 테스트 러너 없으면 Skipped
   │
   ▼
8. [VS Extension] → [MCP Server] POST /api/chat/runs/{runId}/client-result
   │  적용/빌드/테스트 결과 보고
   │
   ▼
9. [MCP Server] final_summary (결과요약)
   │  → 서버가 의도분석·계획·수정안 결과 + 클라이언트 보고를 종합
   │  → LLM Connector (로컬 전용) 로 요약 생성
   │  ← ChatRunClientResultResponse.finalSummary
   │
   ▼
10. [VS Extension] 최종 요약 표시 + run 카드 완료 상태
```

- 모든 LLM 호출은 로컬 엔드포인트(Ollama 등)만 사용한다. 원격 LLM API 금지.
- 문서검색은 로컬 파일 시스템만 대상으로 한다. 외부 네트워크 호출 금지.
- 빌드는 --no-restore 등 오프라인 옵션을 적용한다.
- 테스트는 네트워크 의존이 없는 단위 테스트만 자동 실행 대상이다.
- 설명형 요청(요약, 일반 대화)이면 approval/applying/build_test는 Skipped 처리한다.

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
