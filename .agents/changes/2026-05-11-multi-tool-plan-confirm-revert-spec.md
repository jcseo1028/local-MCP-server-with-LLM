# 스펙 제안: 다중 도구 플랜 실행 + 임시 적용 + 확정/되돌리기

## 1. 배경

현재 Run 오케스트레이션은 단일 의도(단일 도구 실행) 중심이며, 코드 수정 시 승인 후 즉시 적용된다.
사용자 요청이 복합 작업(예: using 정리 + 요약 + 이슈 수정)인 경우 한 번의 프롬프트로 단계적 실행이 어렵고,
코드 적용 이후 확정/되돌리기 결정을 분리하기 어렵다.

요구사항:
- 사용자 프롬프트를 분석해 다중 도구 실행 플랜 생성
- 코드 변경은 "임시 적용" 후 사용자 "확정" 또는 "되돌리기" 가능
- 실패 시 안전한 원복(rollback) 보장

---

## 2. 목표

1) 한 번의 사용자 프롬프트에서 다중 도구를 단계적으로 실행한다.
2) 코드 변경 단계는 즉시 확정하지 않고 "Pending Patch" 상태로 둔다.
3) 사용자가 확정하면 저장/후속 단계 진행, 되돌리면 즉시 원복한다.

비목표:
- Git 커밋 자동 생성
- 장기 브랜치 전략/PR 자동화

---

## 3. 핵심 시나리오

예시 프롬프트:
"선택한 2개 파일의 using 정리하고, 변경 요약한 다음 잠재 오류가 있으면 수정해줘"

실행:
1. `organize_imports` 실행
2. 코드 변경 결과를 임시 적용(에디터 메모리)
3. 사용자에게 확정/되돌리기 선택 요청
4. 확정 시 다음 단계(`summarize_current_code`, `fix_code_issues`) 진행
5. 어느 단계에서든 되돌리기 선택 시 마지막 확정 지점으로 원복

---

## 4. 설계 개요

### 4.1 Plan 모델

`RunData`에 실행 계획을 추가한다.

- `PlanSteps: List<RunPlanStep>`
- `CurrentStepIndex: int`
- `ExecutionMode: single | multi`

`RunPlanStep` 필드:
- `stepId: string`
- `toolName: string`
- `input: object`
- `requiresApproval: bool`
- `status: Pending | Running | WaitingConfirm | Completed | Failed | Reverted`
- `resultSummary: string | null`

### 4.2 Patch 스테이징 모델

임시 적용 추적을 위해 `PendingPatch`를 도입한다.

`PendingPatch` 필드:
- `patchId: string`
- `runId: string`
- `stepId: string`
- `createdAt: DateTime`
- `files: [ { filePath, original, modified, hunks } ]`
- `state: Pending | Confirmed | Reverted | Expired`

정책:
- 한 Run에서 동시에 1개의 PendingPatch만 허용
- 새 코드 변경 단계 시작 전에 기존 PendingPatch가 Confirmed/Reverted여야 함

### 4.3 확정/되돌리기 동작

- 확정(Confirm): PendingPatch 상태를 Confirmed로 전환, 다음 step 실행 허용
- 되돌리기(Revert): PendingPatch의 `original`로 즉시 복원, 상태 Reverted로 전환
- 타임아웃: 일정 시간(예: 15분) 미결정 시 자동 Revert

---

## 5. 계약 변경 제안

대상: `.agents/contracts.md` §11 (Run API)

### 5.1 Start 요청 확장

`ChatRunStartRequest`:
- `allowMultiToolPlan: boolean` (default false)
- `maxPlanSteps: number | null` (default 3, max 5)

### 5.2 Run Snapshot 확장

`ChatRunSnapshot`:
- `planSteps: [RunPlanStepDto]`
- `currentStepIndex: number`
- `pendingPatch: PendingPatchDto | null`

### 5.3 신규 API

- `POST /api/chat/runs/{runId}/confirm`
  - 요청: `{ patchId: string }`
  - 효과: PendingPatch Confirmed, 다음 step 진행

- `POST /api/chat/runs/{runId}/revert`
  - 요청: `{ patchId: string, reason?: string }`
  - 효과: PendingPatch Reverted, 코드 원복

참고:
- 기존 `approval` API는 단계 승인(진행 허용) 의미로 유지 가능
- confirm/revert는 실제 임시 적용본에 대한 상태 전이 전용

---

## 6. 파이프라인 변경 제안

대상: `.agents/pipeline.md` Chat Run Pipeline

신규 단계(개념):
- `proposal_generation` 이후 `staged_apply` 삽입
- `staged_apply` 완료 후 `user_confirm_or_revert` 대기
- Confirm 시 다음 step 또는 `build_test`
- Revert 시 `rollback_complete` 후 중단/재계획

오류 흐름:
- 임시 적용 실패 시 해당 step Failed
- 원복 실패 시 Run Failed + 긴급 알림

---

## 7. VSIX UI 변경 제안

대상: `SummaryToolWindowControl`

UI 추가:
- 기존 승인 버튼과 별도로 `확정` / `되돌리기` 버튼 제공
- PendingPatch 배지(파일 수/step 표시)
- 단계별 확정 이력 타임라인

동작:
- `적용`은 임시 적용으로만 동작
- `확정` 전까지는 다음 코드 수정 단계로 진행하지 않음
- `되돌리기` 클릭 시 즉시 원본 복원 후 상태 갱신

---

## 8. 안전성/일관성 정책

1. Atomic rollback: 멀티파일 임시 적용 중 한 파일 실패 시 전체 원복
2. 단일 소스 보존: PendingPatch는 반드시 원본 스냅샷 보관
3. 단계 격리: step 간 컨텍스트 누수 방지, 이전 step 출력만 다음 step 입력으로 전달
4. 상한 제한: plan step 최대 5개
5. 모델 오작동 방어: plan 생성 실패 시 기존 단일 도구 모드로 fallback

---

## 9. 수용 기준 (Acceptance Criteria)

AC-1: 단일 프롬프트로 2개 이상 도구 step이 생성/실행된다.
AC-2: 코드 변경 step은 임시 적용 후 Confirm/Revert 선택 전까지 확정되지 않는다.
AC-3: Revert 시 모든 파일이 원본과 동일하게 복원된다.
AC-4: Confirm 후 다음 step이 자동 진행된다.
AC-5: 타임아웃 시 PendingPatch가 자동 Revert된다.

---

## 10. 구현 우선순위

1) 계약 확장(§11): snapshot + confirm/revert API
2) 서버 오케스트레이터: step loop + pending patch 상태 머신
3) VSIX: 확정/되돌리기 UI + API 연결
4) 문서/README 동기화

---

## 11. 영향 파일(예상)

서버:
- `src/LocalMcpServer/McpServer/RunModels.cs`
- `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- `src/LocalMcpServer/McpServer/McpEndpoints.cs`
- `src/LocalMcpServer/McpServer/ConversationStore.cs`

VSIX:
- `src/LocalMcpVsExtension/Services/McpRestClient.cs`
- `src/LocalMcpVsExtension/Services/ChatMessageViewModel.cs`
- `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`

문서:
- `.agents/contracts.md`
- `.agents/pipeline.md`
- `.agents/modules.md`
- `README.md`

---

## 12. 결정 메모

- 기존 승인 단계는 유지하되, 실제 코드 확정은 confirm/revert로 분리하는 것이 안전하다.
- "임시 적용 후 확정" 모델은 현장 대응에서 오작동 리스크를 가장 크게 줄인다.
- 다중 도구 계획은 성능 상한(step 수/timeout)과 함께 도입해야 안정적이다.
