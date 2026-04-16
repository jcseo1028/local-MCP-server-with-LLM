# VSIX 채팅 오케스트레이션 확장 스펙

- 날짜: 2026-04-16
- 상태: 스펙 (미구현)
- 대상: VSIX 채팅 입력 이후 실행 흐름 고도화
- 목표 버전: VSIX v2.1

---

## 1. 목적

사용자가 VSIX 채팅 창에 요청을 입력했을 때, 단일 요청/응답 형태가 아니라 아래의 명시적 단계로 처리 과정을 노출한다.

의도분석 → 계획수립 → 컨텍스트수집 → 문서검색 → 수정안생성 → 승인 → 적용 → 빌드/테스트 → 결과요약

이 스펙의 목적은 다음과 같다.

1. 사용자가 현재 어떤 단계가 진행 중인지 즉시 이해할 수 있어야 한다.
2. 코드 변경 전에는 반드시 수정안 미리보기와 승인 단계를 거쳐야 한다.
3. 적용 이후에는 빌드/테스트 결과까지 같은 대화 문맥 안에서 확인할 수 있어야 한다.
4. 현재 구현된 단일 ChatResponse 구조를 단계 기반 실행 구조로 확장하되, 기존 VSIX/서버 모듈 경계를 유지해야 한다.

---

## 2. 현재 상태와 갭

현재 구현은 대체로 아래 흐름이다.

1. 사용자가 메시지 입력
2. VSIX가 현재 코드/언어를 수집하여 POST /api/chat 호출
3. 서버가 의도 분석 후 도구 실행
4. 결과 또는 코드 변경안을 단일 응답으로 반환
5. 코드 변경안이 있으면 사용자가 승인/거부
6. 승인 시 VSIX가 에디터에 적용

현재 구조의 한계는 다음과 같다.

1. 계획수립, 컨텍스트수집, 문서검색, 빌드/테스트, 결과요약이 독립 단계로 드러나지 않는다.
2. 서버 응답이 완료될 때까지 진행 상태를 세분화해서 보여줄 수 없다.
3. 적용 이후 빌드/테스트는 채팅 파이프라인에 연결되어 있지 않다.
4. 문서검색 단계가 실패하거나 생략된 경우 그 이유를 사용자에게 설명하지 못한다.

---

## 3. 목표 사용자 경험

사용자가 채팅에 예를 들어 "이 클래스 null 처리 보강하고 빌드까지 확인해줘"라고 입력하면, VSIX는 하나의 봇 응답만 보여주는 대신 단계형 실행 카드 또는 타임라인을 생성한다.

### 단계별 UX

1. 의도분석
   - 사용자 요청이 요약/설명/수정/버그수정/일반대화 중 무엇인지 판별한다.
   - 결과는 간단한 한 줄 설명으로 노출한다.

2. 계획수립
   - 서버가 이번 요청에서 수행할 하위 작업을 2~5개의 짧은 항목으로 정리한다.
   - 예: "Null 참조 가능 지점 확인", "방어 코드 추가", "빌드 검증"

3. 컨텍스트수집
   - VSIX가 현재 활성 문서, 선택 영역, 언어, 필요 시 진단 정보/프로젝트 정보를 수집한다.
   - 서버는 전달된 컨텍스트를 정규화하고 부족한 정보를 판단한다.

4. 문서검색
   - 로컬 문서/프롬프트/캐시에서 관련 규칙 또는 참조를 찾는다.
   - 검색 결과가 없으면 "사용 가능한 로컬 문서 없음"으로 명시하고 다음 단계로 진행한다.

5. 수정안생성
   - 서버가 최종 코드 변경안 또는 설명 응답을 생성한다.
   - 코드 변경이 있는 경우 원본/수정본 diff 미리보기를 표시한다.

6. 승인
   - 코드 변경이 있는 경우에만 표시한다.
   - 사용자는 승인 또는 거부를 선택한다.
   - 거부 시 실행은 종료되며, 결과요약 단계에서 "사용자 거부"를 기록한다.

7. 적용
   - 승인된 변경을 VSIX가 현재 문서 또는 선택 영역에 적용한다.
   - 적용 대상 파일, 적용 방식(선택 영역/전체 파일), 실패 시 원인을 기록한다.

8. 빌드/테스트
   - VSIX가 가능한 범위에서 솔루션 빌드와 테스트를 실행한다.
   - 테스트 프로젝트가 없거나 명시적 테스트 명령을 찾을 수 없으면 "테스트 대상 없음"으로 처리한다.

9. 결과요약
   - 최종적으로 무엇을 분석했고, 어떤 변경을 적용했고, 빌드/테스트 결과가 어땠는지 한 번에 요약한다.

---

## 4. 범위

### 포함

1. VSIX 채팅 UI에서 단계별 진행 상태 표시
2. 서버 측 단계 오케스트레이션 상태 관리
3. 승인 이후 적용/빌드/테스트/결과요약의 대화 내 연결
4. 실패/생략/거부 상태를 단계별로 사용자에게 노출

### 제외

1. 멀티파일 자동 수정 확장
2. Git 커밋/브랜치 자동 생성
3. 외부 인터넷 문서 검색
4. VS 외부 프로세스의 장기 실행 오케스트레이션

---

## 5. 설계 원칙

1. **오프라인 전용**: 모든 단계는 인터넷 연결 없이 동작해야 한다. 외부 SaaS API, 원격 LLM 엔드포인트, 원격 문서 검색, 원격 패키지 복원, 원격 도구 다운로드를 호출해서는 안 된다.
2. 기존 모듈 경계를 유지한다. VSIX는 UI, 에디터 적용, 빌드/테스트 실행만 담당한다.
3. 서버는 의도분석, 계획수립, 수정안생성, 대화 상태 관리를 담당한다.
4. 의도분석, 계획수립, 수정안생성, 결과요약에 사용되는 LLM 호출은 로컬 MCP Server가 연결한 로컬 LLM 엔드포인트(Ollama 등)로 한정한다. 원격 LLM API를 사용하지 않는다.
5. 문서검색은 Resource Cache 또는 로컬 프롬프트/규칙 문서 범위 내에서만 수행한다. 외부 웹 검색이나 원격 문서 저장소를 호출하지 않는다.
6. 코드 변경은 승인 전까지 에디터에 반영하지 않는다.
7. 빌드/테스트는 코드가 실제로 적용된 뒤에만 수행한다.
8. 빌드/테스트는 오프라인 모드로 수행한다. NuGet restore, npm install 등 네트워크가 필요한 패키지 복원을 트리거하지 않는다. 사전 복원된 로컬 의존성만 사용한다.

---

## 6. 목표 상태 머신

### 6a. 실행 상태

```
Queued
Running.IntentAnalysis
Running.Planning
Running.ContextCollection
Running.DocumentSearch
Running.ProposalGeneration
WaitingForApproval
Running.Applying
Running.BuildAndTest
Running.Summarizing
Completed
Rejected
Failed
```

### 6b. 단계 상태

각 단계는 아래 상태를 가진다.

```
Pending | InProgress | Completed | Skipped | Failed
```

### 6c. 단계 정의

| 순서 | 단계 ID | 소유 주체 | 비고 |
|------|---------|-----------|------|
| 1 | intent_analysis | MCP Server | 필수 |
| 2 | planning | MCP Server | 필수 |
| 3 | context_collection | VSIX + MCP Server | 필수 |
| 4 | document_search | MCP Server | 조건부, 결과 없으면 Skipped/Completed |
| 5 | proposal_generation | MCP Server | 필수 |
| 6 | approval | 사용자 + VSIX | 코드 변경 시 필수 |
| 7 | applying | VSIX | 승인된 코드 변경 시 필수 |
| 8 | build_test | VSIX | 적용 성공 시 필수 |
| 9 | final_summary | MCP Server 또는 VSIX | 필수 |

---

## 7. 권장 아키텍처 변경

현재의 POST /api/chat 단건 응답만으로는 단계별 진행 상황을 안정적으로 표현하기 어렵다. 따라서 채팅 실행을 비동기 Run 단위로 분리한다.

### 7a. 신규 실행 단위

- conversationId: 대화 세션 식별자
- runId: 한 번의 사용자 요청 처리 식별자

하나의 conversation 안에 여러 run이 순차적으로 쌓인다.

### 7b. 권장 API 초안

#### 1. 실행 시작

```
POST /api/chat/runs

ChatRunStartRequest {
  message: string
  code: string | null
  language: string | null
  selectionOnly: boolean
  conversationId: string | null
  activeFilePath: string | null
  solutionPath: string | null
}

ChatRunStartResponse {
  conversationId: string
  runId: string
  state: string
}
```

#### 2. 실행 상태 조회

```
GET /api/chat/runs/{runId}

ChatRunSnapshot {
  conversationId: string
  runId: string
  state: string
  stages: [
    {
      stageId: string
      title: string
      status: string
      message: string | null
      startedAt: string | null
      completedAt: string | null
    }
  ]
  intent: {
    toolName: string | null
    confidence: number
    description: string
  } | null
  planItems: [string]
  references: [
    {
      title: string
      source: string
      excerpt: string
    }
  ]
  proposal: {
    summary: string
    original: string | null
    modified: string | null
    requiresApproval: boolean
  } | null
  finalSummary: string | null
  error: string | null
}
```

#### 3. 승인/거부

```
POST /api/chat/runs/{runId}/approval

ChatRunApprovalRequest {
  approved: boolean
}
```

#### 4. 적용 및 검증 결과 보고

```
POST /api/chat/runs/{runId}/client-result

ChatRunClientResultRequest {
  applied: boolean
  applyMessage: string | null
  appliedTargets: [string]
  build: {
    attempted: boolean
    succeeded: boolean | null
    summary: string | null
  }
  tests: {
    attempted: boolean
    succeeded: boolean | null
    summary: string | null
  }
}

ChatRunClientResultResponse {
  finalSummary: string
}
```

### 7c. 통신 방식

초기 구현은 폴링 방식을 권장한다.

1. VSIX가 POST /api/chat/runs 로 시작
2. 500ms~1000ms 간격으로 GET /api/chat/runs/{runId} 조회
3. WaitingForApproval 상태가 되면 승인 UI 노출
4. 승인 후 VSIX가 적용/빌드/테스트 수행
5. 결과를 /client-result 로 보고하고 최종 요약 수신

이유는 다음과 같다.

1. 현재 REST 클라이언트 구조를 크게 바꾸지 않는다.
2. .NET Framework 4.8 기반 VSIX에서 구현 복잡도가 낮다.
3. SSE 추가 없이도 단계 UI를 안정적으로 표시할 수 있다.

---

## 8. 단계별 상세 처리 규칙

### 8a. 의도분석

입력:

- 사용자 메시지
- 사용 가능한 도구 목록
- 언어 정보
- 최근 대화 이력

출력:

- toolName
- confidence
- description
- build/test 필요 여부 추론 플래그

규칙:

1. 코드 수정 요청이면 수정안생성까지 진행한다.
2. 설명형 요청이면 승인/적용/빌드 단계는 Skipped 처리한다.
3. 의도가 불명확하면 일반 대화로 응답하되, 계획수립에는 "추가 정보 요청"을 포함할 수 있다.

### 8b. 계획수립

출력은 사용자에게 노출 가능한 짧은 작업 계획이어야 한다.

예:

1. 현재 코드의 null 경로를 확인한다.
2. 방어 조건을 추가한다.
3. 적용 후 빌드 성공 여부를 확인한다.

규칙:

1. 최대 5개 항목으로 제한한다.
2. 구현 세부정보보다 사용자 관점 작업을 우선한다.

### 8c. 컨텍스트수집

VSIX 수집 대상:

1. 활성 파일 경로
2. 선택 영역 텍스트 또는 전체 문서 텍스트
3. 언어 정보
4. 가능하면 솔루션 경로
5. 가능하면 Error List 또는 현재 문서 진단 요약

규칙:

1. 코드 수정 요청인데 코드 컨텍스트가 없으면 사용자 확인 후 전체 파일을 첨부한다.
2. 컨텍스트가 너무 크면 서버는 요약 또는 절단 기준을 적용할 수 있다.

### 8d. 문서검색

검색 우선순위:

1. 로컬 규칙 문서(.agents)
2. 프롬프트 템플릿 관련 문서
3. Resource Cache가 구현된 경우 로컬 문서 캐시

규칙:

1. 검색 실패는 전체 실행 실패 사유가 아니다.
2. 검색 결과가 없으면 단계 메시지에 명시하고 수정안생성으로 진행한다.

### 8e. 수정안생성

출력:

1. 사용자용 변경 요약
2. 수정된 코드 또는 설명 결과
3. 승인 필요 여부

규칙:

1. 코드 변경이 있으면 반드시 original/modified를 함께 보관한다.
2. 수정안은 적용 가능 단위여야 한다.
3. 여러 파일 수정은 초기 버전에서 허용하지 않는다.

### 8f. 승인

규칙:

1. 승인 전에는 적용, 빌드/테스트를 시작하지 않는다.
2. 거부 시 applying/build_test는 Skipped 처리한다.
3. 거부 결과도 final_summary에 포함한다.

### 8g. 적용

VSIX 책임:

1. 선택 영역 교체 또는 전체 문서 교체
2. Undo 스택 보존
3. 적용 성공/실패 메시지 생성

규칙:

1. 원본 텍스트 매칭 실패 시 적용 실패로 처리한다.
2. 적용 실패 시 build_test는 Skipped 처리한다.

### 8h. 빌드/테스트

VSIX 책임:

1. 솔루션 또는 프로젝트 빌드 실행
2. 테스트 프로젝트/명령이 확인되면 테스트 실행

오프라인 제약:

1. 빌드는 `--no-restore` (dotnet) 또는 동등한 옵션으로 실행하여 NuGet/npm 등의 원격 패키지 복원을 방지한다.
2. 의존성이 사전 복원되지 않아 빌드가 실패하면 "오프라인 환경: 패키지 복원 필요"를 단계 메시지에 명시한다.
3. 테스트는 네트워크 의존이 없는 단위 테스트만 자동 실행 대상으로 한다. 외부 API, 원격 DB 등을 호출하는 통합 테스트는 자동 실행 대상에서 제외하거나, 실행 결과가 네트워크 오류이면 "오프라인 환경: 네트워크 의존 테스트 생략"으로 처리한다.
4. 테스트 프레임워크 자체가 설치되지 않은 경우에도 "테스트 러너 없음"으로 Skipped 처리하고 run 전체를 실패로 종료하지 않는다.

권장 동작:

1. 빌드는 항상 시도한다.
2. 테스트는 가능할 때만 시도한다.
3. 결과는 성공/실패뿐 아니라 핵심 오류 한두 줄을 요약한다.

### 8i. 결과요약

포함 항목:

1. 의도 분석 결과
2. 수행 계획 요약
3. 실제 적용 여부
4. 빌드 결과
5. 테스트 결과
6. 후속 조치 제안

---

## 9. VSIX UI 변경 스펙

### 9a. 메시지 카드 구조

각 사용자 요청마다 하나의 실행 카드(run card)를 생성한다.

표시 요소:

1. 상단 요약: 사용자 요청 문장
2. 단계 타임라인: 9개 단계의 상태 표시
3. 계획 섹션: 계획수립 결과
4. 참조 섹션: 문서검색 결과 또는 생략 사유
5. 수정안 섹션: Markdown 설명 + diff 미리보기
6. 승인 액션: 승인/거부 버튼
7. 검증 섹션: 적용/빌드/테스트 로그 요약
8. 최종 요약 섹션

### 9b. 상태 표시 규칙

- Pending: 회색
- InProgress: 파란색
- Completed: 초록색
- Skipped: 회색 점선 또는 "생략"
- Failed: 빨간색

### 9c. 기존 메시지 모델 확장 초안

```csharp
enum ChatRunState
{
    Queued,
    Running,
    WaitingForApproval,
    Completed,
    Rejected,
    Failed
}

enum ChatStageStatus
{
    Pending,
    InProgress,
    Completed,
    Skipped,
    Failed
}

class ChatRunStageViewModel
{
    string StageId;
    string Title;
    ChatStageStatus Status;
    string Message;
}

class ChatRunViewModel
{
    string RunId;
    ChatRunState State;
    List<ChatRunStageViewModel> Stages;
    List<string> PlanItems;
    string ProposalSummary;
    CodeChangeInfo CodeChange;
    string FinalSummary;
}
```

---

## 10. 서버 내부 오케스트레이션 스펙

### 10a. MCP Server 책임 확장

MCP Server는 기존 IntentResolver + ConversationStore 외에 "Run 상태 저장" 책임을 가진다.

필요 데이터:

1. runId별 단계 상태
2. 계획 항목
3. 참조 문서 결과
4. 수정안
5. 승인 대기 여부
6. 최종 요약

### 10b. 대화 저장소 확장

ConversationStore는 아래 둘을 함께 관리하도록 확장한다.

1. conversation 단위 이력
2. conversation 내부 run 단위 실행 상태

### 10c. 실패 처리

단계 실패 시 원칙:

1. 의도분석/계획수립/수정안생성 실패는 run 전체 Failed
2. 문서검색 실패는 해당 단계 Failed 또는 Skipped 후 계속 진행 가능
3. 적용 실패는 final_summary까지 진행하되 build_test는 Skipped
4. 빌드/테스트 실패는 run 전체를 Failed로 종료하지 않고 Completed with issues로 요약 가능

---

## 11. 구현 대상 파일 초안

### VSIX

1. src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs
   - 단계 타임라인 UI
   - run 카드 렌더링
   - 승인 후 적용/빌드/테스트 흐름 연결

2. src/LocalMcpVsExtension/Services/McpRestClient.cs
   - run 시작/조회/승인/클라이언트 결과 보고 API 추가

3. src/LocalMcpVsExtension/Services/ChatMessageViewModel.cs
   - run/stage 상태 모델 추가

4. src/LocalMcpVsExtension/Services/
   - 필요 시 빌드/테스트 실행 보조 서비스 추가

### MCP Server

1. src/LocalMcpServer/McpServer/McpEndpoints.cs
   - /api/chat/runs 계열 엔드포인트 추가

2. src/LocalMcpServer/McpServer/ConversationStore.cs
   - run 상태 저장 확장

3. src/LocalMcpServer/McpServer/IntentResolver.cs
   - 계획수립 입력/출력 확장

4. src/LocalMcpServer/prompts/
   - 계획수립용 프롬프트 추가 또는 기존 intent/general 프롬프트 확장

---

## 12. 수용 기준

아래가 모두 충족되면 본 스펙의 1차 구현이 완료된 것으로 본다.

1. 사용자가 채팅 입력 후 9개 단계의 진행 상태를 순서대로 확인할 수 있다.
2. 코드 변경 요청 시 수정안생성 이후 반드시 승인 단계에서 멈춘다.
3. 승인 후 적용 결과가 카드에 기록된다.
4. 적용 성공 시 빌드가 자동 수행되고 결과가 표시된다.
5. 테스트 실행이 불가능한 경우에도 그 사유가 표시된다.
6. 최종 요약에 적용 여부와 빌드/테스트 상태가 포함된다.

---

## 13. 미결 항목

1. 문서검색 단계에서 실제로 어떤 로컬 문서 집합을 기본 대상으로 볼지 확정 필요 -> 미리 지정한 폴더 내에서만 검색한다. 해당 폴더 내 문서가 없으면 "로컬 문서 없음"으로 처리한다.
2. VSIX에서 테스트 실행 기준을 솔루션/프로젝트/명령 중 무엇으로 정할지 확정 필요 -> 우선은 솔루션 레벨로 시도하되, 프로젝트 레벨 또는 명령어 기반으로 확장할 수 있도록 설계한다.
3. 빌드/테스트를 UI 스레드 차단 없이 실행하는 방식 확정 필요 -> VS 2022 의 확장이니, 굳이 백그라운드로 실행하지 않아도 되지 않나?
4. final_summary를 서버가 생성할지, VSIX가 조립할지 최종 결정 필요 -> 서버가 의도 분석, 계획 수립, 수정안 생성 결과를 모두 포함하는 최종 요약을 생성한다. VSIX는 적용/빌드/테스트 결과를 별도로 final_summary에 추가할 수 있도록 한다.
5. 네트워크 의존 테스트를 자동 식별하는 기준(어트리뷰트, 카테고리, 네이밍 등) 확정 필요 -> 우선은 네이밍 기반으로 시도하되, 필요 시 어트리뷰트 기반으로 확장할 수 있도록 설계한다.

---

## 14. 이번 세션 변경 요약

1. 구현 코드는 변경하지 않았다.
2. VSIX 채팅 입력 이후의 다단계 실행 흐름에 대한 목표 스펙 문서를 추가했다.
3. 현재 구현과의 차이, 권장 API, 상태 머신, UI/서버 책임 분리를 명시했다.