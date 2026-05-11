# 2026-05-11 변경 기록: VSIX UI confirm/revert 연동

## 목적

- v2.6 서버 API(`confirm/revert`)를 VSIX 사용자 버튼 흐름에 연결한다.
- 다중 도구 계획 상태(`planSteps`)를 Run 카드에 표시한다.

## 변경 파일

- `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`
- `src/LocalMcpVsExtension/Services/ChatMessageViewModel.cs`
- `.agents/modules.md`
- `README.md`

## 구현 요약

1. Run 시작 요청에서 다중 도구 플랜 옵션 기본 전송
- `RunStartRequest.AllowMultiToolPlan = true`
- `RunStartRequest.MaxPlanSteps = 3`

2. 승인 대기 상태에서 PendingPatch 인식
- `snapshot.PendingPatch`가 `Pending`이면 메시지 모델에 `UseConfirmRevert=true`와 `PendingPatchId` 저장
- 버튼 라벨을 `확정/되돌리기`로 전환

3. 사용자 버튼 처리 분기
- `SendRunDecisionAsync(...)` 헬퍼 추가
- 우선 `confirm/revert` 호출 시도
- patch 식별자가 없거나 호출 실패 시 기존 `approval` API로 폴백

4. Run 카드 시각화 강화
- 기존 타임라인/Plan/References 섹션에 더해 `실행 단계` 섹션 추가
- `planSteps`와 `currentStepIndex`를 한눈에 표시

5. Nullability 정리
- 관련 경고를 제거하도록 지역 변수와 nullable 타입 선언 보강

## 검증

- VSIX Release 빌드 성공
  - `src/LocalMcpVsExtension/bin/Release/net48/LocalMcpVsExtension.vsix` 생성 확인
