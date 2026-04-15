# VSIX 채팅 UI 전환 스펙 변경 문서

- **날짜**: 2026-04-15
- **상태**: 스펙 (구현 전)
- **버전 목표**: VSIX v2.0

---

## 1. 변경 목적

현재 VSIX Tool Window는 **도구 선택 → 버튼 클릭** 방식의 단방향 UI이다.
이를 **채팅 기반 대화형 UI**로 전환하여:

1. 사용자가 자연어로 의도를 입력하면 서버가 의도를 분석하고 적절한 도구를 자동 선택·실행한다.
2. 코드 수정 도구의 결과는 **백업본(미리보기)** 상태로 제시되고, 사용자가 **승인**해야 에디터에 최종 반영된다.
3. 대화 이력이 유지되어 맥락 기반 연속 작업이 가능하다.

---

## 2. 영향 범위

### 변경 대상 모듈

| 모듈 | 변경 유형 | 범위 |
|------|----------|------|
| **VS Extension (VSIX)** | UI 전면 교체 | ToolWindow, Services, Commands |
| **MCP Server** | 엔드포인트 추가 | McpEndpoints.cs |
| **Tool Registry** | 인터페이스 확장 없음 | 변경 없음 (기존 도구 그대로 사용) |
| **LLM Connector** | 의도 분석 호출 추가 | OllamaConnector (기존 인터페이스 재활용) |
| **Configuration** | 설정 항목 추가 | ServerConfig, appsettings.json |

### 변경 대상 계약

| 계약 | 변경 내용 |
|------|----------|
| contracts.md §8 (Direct REST) | `POST /api/chat` 엔드포인트 추가 |
| contracts.md §9 (신규) | Chat Request/Response 계약 정의 |

---

## 3. 신규 계약 정의

### §9. Chat API (VS Extension ↔ MCP Server)

사용자 자연어 입력을 받아 의도 분석 → 도구 선택 → 실행 → 결과 반환을 서버 측에서 일괄 처리한다.

```
POST /api/chat

ChatRequest {
  message: string              // 사용자 입력 메시지 (자연어)
  code: string | null          // 현재 에디터 코드 (선택 영역 또는 전체 파일)
  language: string | null      // 프로그래밍 언어
  selectionOnly: boolean       // 선택 영역 여부
  conversationId: string | null // 대화 세션 ID (null이면 새 대화)
}

ChatResponse {
  conversationId: string       // 대화 세션 ID
  intent: {                    // 분석된 의도 정보
    toolName: string | null    // 선택된 도구 (없으면 일반 대화)
    confidence: number         // 의도 확신도 (0.0 ~ 1.0)
    description: string        // 의도 요약 설명
  }
  result: string               // 결과 텍스트 (Markdown)
  codeChange: {                // 코드 변경 제안 (null이면 코드 변경 없음)
    original: string           // 원본 코드
    modified: string           // 변경된 코드
    toolName: string           // 사용된 도구명
  } | null
  requiresApproval: boolean    // 사용자 승인 필요 여부
}
```

### §10. Chat Approval API (VS Extension → MCP Server)

코드 변경 승인/거부를 처리한다. (서버가 대화 상태를 관리하므로 서버에 알림)

```
POST /api/chat/approve

ChatApprovalRequest {
  conversationId: string       // 대화 세션 ID
  approved: boolean            // 승인(true) / 거부(false)
}

ChatApprovalResponse {
  success: boolean
  message: string              // "적용 완료" 또는 "취소됨"
}
```

---

## 4. 의도 분석 설계

### 4a. Intent Resolver (MCP Server 내부)

서버 측에서 사용자 메시지를 분석하여 적절한 도구를 매핑한다.

**처리 흐름:**
```
1. 사용자 메시지 수신
2. 등록된 도구 목록 + 도구 설명 기반 의도 분석
   ├─ LLM 기반 분석: 사용자 메시지 + 도구 목록을 프롬프트로 전달
   │   → LLM이 {toolName, confidence, description} 반환
   └─ confidence < 0.5 → 도구 없이 일반 대화 응답
3. toolName이 결정되면 기존 ToolRegistry.ExecuteAsync() 호출
4. 코드 수정 도구(EditTools)이면 requiresApproval=true로 마킹
```

**의도 분석 프롬프트 (신규):**
- 파일: `prompts/intent_analysis.prompt.md`
- 입력 변수: `{{message}}`, `{{tools}}` (도구 목록 JSON), `{{language}}`
- 출력 형식: JSON (`{toolName, confidence, description}`)
- LLM 옵션: temperature=0.1 (결정적), maxTokens=200 (간결)

### 4b. 일반 대화 처리

도구 매핑이 안 되는 경우(confidence < 0.5), 코드 컨텍스트를 참고한 일반 대화 응답을 생성한다.

- 프롬프트: `prompts/general_chat.prompt.md`
- 입력 변수: `{{message}}`, `{{code}}`, `{{language}}`

---

## 5. UI 변경 설계

### 5a. 기존 UI (삭제 대상)

| 요소 | 상태 |
|------|------|
| 도구 ComboBox | 삭제 (의도 분석이 자동 선택) |
| "현재 파일" / "선택 영역" 버튼 | 삭제 (자동 감지) |
| "📋 적용" 버튼 | 승인/거부 인라인 버튼으로 대체 |
| 서버 주소 TextBox | 설정 영역으로 이동 (접기 가능) |
| FlowDocumentScrollViewer (단일 결과) | 채팅 메시지 목록으로 대체 |

### 5b. 신규 UI 레이아웃

```
┌──────────────────────────────────────────┐
│  ⚙ 서버: http://localhost:5100  [접기]   │  ← Row 0: 설정 바 (접기 가능)
├──────────────────────────────────────────┤
│                                          │
│  🤖 안녕하세요! 코드에 대해 질문하거나   │  ← Row 1: 채팅 메시지 영역
│     작업을 요청해 주세요.                │     (ScrollViewer + ItemsControl)
│                                          │
│  👤 이 코드를 리팩터링 해줘              │     사용자 메시지 (우측 정렬)
│                                          │
│  🤖 [의도: refactor_current_code]        │     봇 메시지 (좌측 정렬)
│     리팩터링 결과입니다:                 │     Markdown 렌더링
│     ```csharp                            │
│     ...변경된 코드...                    │
│     ```                                  │
│     ┌────────┐ ┌────────┐                │
│     │✅ 승인 │ │❌ 거부 │                │  ← 코드 변경 시 인라인 승인 버튼
│     └────────┘ └────────┘                │
│                                          │
├──────────────────────────────────────────┤
│  [메시지 입력...]              [전송 ▶]  │  ← Row 2: 입력 영역
│  ☑ 현재 코드 포함                        │     체크박스: 코드 컨텍스트 포함 여부
└──────────────────────────────────────────┘
│  상태: 준비                    언어: C#  │  ← Row 3: 상태 표시줄
└──────────────────────────────────────────┘
```

### 5c. 채팅 메시지 모델

```csharp
enum ChatMessageRole { User, Assistant, System }

class ChatMessageViewModel {
    ChatMessageRole Role;
    string Content;           // Markdown 텍스트
    DateTime Timestamp;
    bool HasCodeChange;       // 코드 변경 제안 포함 여부
    CodeChangeInfo? CodeChange;
    ApprovalState Approval;   // Pending, Approved, Rejected
}

enum ApprovalState { None, Pending, Approved, Rejected }

class CodeChangeInfo {
    string Original;
    string Modified;
    string ToolName;
}
```

---

## 6. 코드 변경 승인 흐름

### 6a. 백업 기반 안전 적용

```
1. 서버가 코드 수정 결과를 반환 (ChatResponse.codeChange != null)
2. VSIX가 결과를 채팅 메시지로 표시 + "✅ 승인" / "❌ 거부" 버튼 활성화
3. 사용자가 변경 코드를 리뷰 (diff 형태로 표시)

[승인 시]
4a. VSIX가 에디터에 변경 코드를 적용
    - VS의 Undo 스택으로 자동 백업 (ITextBuffer.CreateEdit)
    - 상태를 Approved로 변경
    - POST /api/chat/approve (approved=true)

[거부 시]
4b. 상태를 Rejected로 변경
    - 코드 적용 안 함
    - POST /api/chat/approve (approved=false)
    - "거부되었습니다. 다른 요청을 해주세요." 메시지 표시
```

### 6b. Undo 지원

- VS 에디터의 ITextBuffer.CreateEdit()는 자동으로 Undo 스택에 등록된다.
- 사용자가 Ctrl+Z로 언제든 변경을 되돌릴 수 있다.
- 별도의 백업 파일 생성은 하지 않는다 (VS의 내장 Undo가 충분).

---

## 7. 서버 측 변경 사항

### 7a. 신규 엔드포인트

| 메서드 | 경로 | 설명 |
|--------|------|------|
| POST | `/api/chat` | 채팅 메시지 처리 (의도 분석 → 도구 실행) |
| POST | `/api/chat/approve` | 코드 변경 승인/거부 |

### 7b. 신규 서비스: IntentResolver

- 위치: `McpServer/IntentResolver.cs` (MCP Server 모듈 내부)
- 의존: ToolRegistryService (도구 목록), LLM Connector (의도 분석)
- 책임: 사용자 메시지 → {toolName, confidence, description} 매핑

### 7c. 대화 상태 관리

- 메모리 내 `ConcurrentDictionary<string, ConversationState>` 로 관리
- ConversationState: conversationId, 마지막 코드 변경 정보, 타임스탬프
- 타임아웃: 30분 미사용 시 자동 삭제 (로컬 서버이므로 경량)

### 7d. 신규 프롬프트 파일

| 파일 | 용도 |
|------|------|
| `prompts/intent_analysis.prompt.md` | 의도 분석용 프롬프트 |
| `prompts/general_chat.prompt.md` | 일반 대화 응답 프롬프트 |

---

## 8. VSIX 측 변경 사항

### 8a. 파일 변경 목록

| 파일 | 변경 유형 | 설명 |
|------|----------|------|
| `ToolWindows/SummaryToolWindowControl.cs` | **전면 재작성** | 채팅 UI로 교체 |
| `Services/McpRestClient.cs` | **확장** | Chat API 호출 메서드 추가 |
| `Services/ChatMessageViewModel.cs` | **신규** | 채팅 메시지 뷰모델 |
| `Services/MarkdownToFlowDocument.cs` | 변경 없음 | 기존 그대로 재활용 |
| `Services/LanguageDetector.cs` | 변경 없음 | 기존 그대로 재활용 |
| `Commands/ShowSummaryWindowCommand.cs` | 변경 없음 | Tool Window 열기 |
| `ToolWindows/SummaryToolWindow.cs` | 변경 없음 | Tool Window 정의 |

### 8b. 입력 처리

- **Enter**: 메시지 전송 (Shift+Enter는 줄바꿈)
- **코드 자동 첨부**: "☑ 현재 코드 포함" 체크 시 현재 에디터의 코드를 자동으로 ChatRequest.code에 포함
- **선택 영역 감지**: 에디터에 선택 영역이 있으면 selectionOnly=true, 선택 코드만 전송

---

## 9. Configuration 변경

### appsettings.json 추가 항목

```json
{
  "Chat": {
    "IntentModel": null,         // 의도 분석용 모델 (null이면 DefaultModel)
    "ConversationTimeoutMinutes": 30,
    "MaxConversationHistory": 20  // 대화당 최대 메시지 수
  }
}
```

---

## 10. modules.md 갱신 예정 사항

### VS Extension (VSIX) 모듈 변경

- **입력**: ~~사용자 버튼 클릭~~ → 사용자 채팅 메시지 입력
- **출력**: ~~단일 결과 표시~~ → 채팅 메시지 목록 (대화 이력)
- **코드 적용**: ~~"📋 적용" 버튼~~ → 인라인 승인/거부 버튼
- **신규 의존**: `POST /api/chat`, `POST /api/chat/approve` (contracts.md §9, §10)

### MCP Server 모듈 변경

- **신규 서비스**: IntentResolver (의도 분석)
- **신규 엔드포인트**: `/api/chat`, `/api/chat/approve`
- **의존 추가**: LLM Connector (의도 분석 시 직접 호출 — 기존 규칙 예외)

> **설계 결정**: 의도 분석은 MCP Server 모듈에서 LLM Connector를 직접 호출한다.
> 이유: 의도 분석은 "도구 실행"이 아니라 "라우팅 결정"이므로 Tool Registry를 거칠 필요가 없다.
> modules.md의 MCP Server 비의존 항목에서 LLM Connector를 조건부 허용으로 갱신한다.

---

## 11. pipeline.md 갱신 예정 사항

### Chat Pipeline (신규)

```
1. [사용자 (외부)] 채팅 메시지 입력
   │
   ▼
2. [VS Extension] 현재 에디터 코드 + 언어 + 선택 영역 정보 수집
   │
   ▼
3. [VS Extension] → [MCP Server] POST /api/chat (ChatRequest)
   │
   ▼
4. [MCP Server / IntentResolver] 의도 분석
   │  → [LLM Connector] 의도 분석 프롬프트 전송
   │  ← {toolName, confidence, description}
   │
   ├─ toolName != null (confidence ≥ 0.5)
   │  ▼
   │  5a. [MCP Server] → [Tool Registry] ToolCallRequest
   │      │  (기존 Tool Call Flow 재활용)
   │      ▼
   │  6a. ChatResponse 구성 (result + codeChange)
   │
   └─ toolName == null (confidence < 0.5)
      ▼
      5b. [MCP Server / IntentResolver] 일반 대화 응답 생성
          │  → [LLM Connector] 일반 대화 프롬프트 전송
          ▼
      6b. ChatResponse 구성 (result only)
   │
   ▼
7. [VS Extension] ChatResponse 수신 → 채팅 메시지 표시
   │
   ├─ requiresApproval == true
   │  → 승인/거부 버튼 표시
   │  → 사용자 승인 시 에디터 적용 + POST /api/chat/approve
   │
   └─ requiresApproval == false
      → 결과만 표시
```

---

## 12. 구현 순서 (권장)

단계별로 독립 검증 가능하도록 증분적으로 구현한다.

| 단계 | 작업 | 검증 방법 |
|------|------|----------|
| **1** | contracts.md §9, §10 계약 추가 | 문서 리뷰 |
| **2** | modules.md, pipeline.md 갱신 | 문서 리뷰 |
| **3** | 프롬프트 파일 작성 (intent_analysis, general_chat) | 프롬프트 내용 검토 |
| **4** | Configuration 변경 (Chat 섹션) | appsettings.json 로드 확인 |
| **5** | IntentResolver 구현 | 단위 테스트 / REST 호출 |
| **6** | `/api/chat` 엔드포인트 구현 | curl/PowerShell 호출 테스트 |
| **7** | `/api/chat/approve` 엔드포인트 구현 | curl 테스트 |
| **8** | McpRestClient Chat API 메서드 추가 | — |
| **9** | ChatMessageViewModel 작성 | — |
| **10** | SummaryToolWindowControl 채팅 UI 재작성 | VS 2022 실행 테스트 |
| **11** | 승인/거부 흐름 구현 | 통합 테스트 |
| **12** | README.md 갱신 | — |

---

## 13. 하위 호환성

| 항목 | 호환성 |
|------|--------|
| 기존 REST API (`/api/tools/list`, `/api/tools/call`) | **유지** — 삭제하지 않음 |
| SSE 엔드포인트 (`/sse`, `/message`) | **유지** — VS Agent mode용 |
| 기존 4개 도구 | **유지** — 변경 없음 |
| VSIX v1.x Tool Window | **대체** — 채팅 UI로 전면 교체 |

---

## 14. 결정된 항목

- [x] 의도 분석 프롬프트 출력 형식: **JSON** (`{toolName, confidence, description}`)
- [x] 대화 이력: **LLM 컨텍스트에 포함**. 서버 메모리 내 `ConversationStore`로 캐시하고, 향후 SQLite 등 경량 DB 마이그레이션 가능하도록 인터페이스 분리.
- [x] diff 뷰어: **side-by-side** 방식. VSIX 내 WPF Grid 2열로 원본/변경 코드를 나란히 표시.
- [x] 선택 영역 없이 코드 수정 요청 시: VSIX가 채팅에 **"현재 파일 전체를 포함할까요?"** 확인 메시지를 표시하고, 사용자 승인 후 전체 코드를 서버에 전송.
