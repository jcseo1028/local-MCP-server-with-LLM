# 2026-05-11 변경 기록: v2.6 multi-tool plan + confirm/revert 구현

## 결정 사항

- Contract-First 원칙에 따라 `.agents/contracts.md` §11에 먼저 다중 도구 플랜 및 confirm/revert API를 반영했다.
- 기존 `approval` 흐름은 하위 호환을 위해 유지하고, `confirm/revert`는 PendingPatch 상태 전이용 API로 추가했다.
- 다중 도구 플랜은 LLM 플랜 텍스트를 재해석하지 않고 메시지 키워드 기반으로 안전하게 상한(`maxPlanSteps`) 내 생성한다.

## 코드 변경 요약

### 서버
- `src/LocalMcpServer/McpServer/RunModels.cs`
  - `PlanExecutionMode`, `PlanStepStatus`, `PendingPatchState` 추가
  - `RunPlanStep`, `PendingPatch` 모델 추가
  - `RunData`에 `PlanSteps`, `CurrentStepIndex`, `ExecutionMode`, `PendingPatch`, `AllowMultiToolPlan`, `MaxPlanSteps` 추가
  - `ChatRunStartRequest`에 `allowMultiToolPlan`, `maxPlanSteps` 추가

- `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
  - `InitializePlan`, `BuildToolPlan`, `ExecutePlanFromCurrentStepAsync`, `BuildPendingPatch` 추가
  - `GenerateProposalAsync`를 step별 toolName 실행 방식으로 확장
  - 승인/거부 시 plan step 상태와 pending patch 상태 동기화
  - `ProcessConfirm`, `ProcessRevert` 추가
  - client-result 수신 후 남은 plan step이 있으면 다음 단계 실행

- `src/LocalMcpServer/McpServer/McpEndpoints.cs`
  - `POST /api/chat/runs/{runId}/confirm`
  - `POST /api/chat/runs/{runId}/revert`
  - `BuildRunSnapshot`에 `executionMode`, `planSteps`, `currentStepIndex`, `pendingPatch` 포함

### VSIX
- `src/LocalMcpVsExtension/Services/McpRestClient.cs`
  - Run 시작 DTO에 `AllowMultiToolPlan`, `MaxPlanSteps` 추가
  - Run snapshot DTO에 `ExecutionMode`, `PlanSteps`, `CurrentStepIndex`, `PendingPatch` 추가
  - `SendRunConfirmAsync`, `SendRunRevertAsync` 메서드 추가

## 문서 동기화

- `.agents/contracts.md` §11 확장
- `.agents/modules.md` v2.6 Run REST/RunOrchestrator 설명 반영
- `.agents/pipeline.md` Chat Run pipeline에 confirm/revert 및 multi-step 복귀 흐름 반영
- `README.md` v2.6 기능 항목 추가

## 검증

- `src/LocalMcpServer`: `dotnet build` 성공
- `src/LocalMcpVsExtension`: MSBuild Release 빌드 성공 (`LocalMcpVsExtension.vsix` 생성)

## 미결 항목

- VSIX UI에서 confirm/revert 버튼을 실제로 노출/연결하는 화면 흐름은 후속 작업
- plan step 생성을 키워드 기반에서 LLM 구조화 출력 기반으로 고도화 가능
