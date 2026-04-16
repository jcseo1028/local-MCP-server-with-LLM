# v2.1 Gap Implementation Completion

날짜: 2026-04-16

## 변경 목적

v2.1 채팅 오케스트레이션 스펙 대비 미구현 항목 5건 구현 완료.

## 변경 내역

### 1. ChatRunViewModel 분리 (§9c)

- **파일**: `src/LocalMcpVsExtension/Services/ChatMessageViewModel.cs`
- `ChatRunState` 열거형 (6개 상태), `ChatStageStatus` 열거형 (5개 상태)
- `ChatRunStageViewModel` 클래스 (StageId, Title, Status, Message, ParseStatus)
- `ChatRunViewModel` 클래스 (RunId, State, Stages, PlanItems, References, ProposalSummary, CodeChange, FinalSummary, Error, ParseState, UpdateFrom)
- `ChatMessageViewModel.RunViewModel` 속성 추가 (기존 RunState/RunStages 문자열 속성 제거)

### 2. UI 색상 구분 (§9b)

- **파일**: `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`
- 단계별 상태 색상: Completed=#4EC9B0, InProgress=#569CD6, Pending=#808080, Failed=#D16969, Skipped=#808080+취소선
- `UpdateRunStageDisplay` 메서드 전면 교체

### 3. Run 카드 섹션 분리 (§9a)

- **파일**: `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`
- 계획 섹션 (📋): 검정 테두리 카드, planItems 불릿 렌더링
- 참조 섹션 (🔍): 검정 테두리 카드, References 툴팁 렌더링
- `CreateRunSection`, `InsertAfter` 헬퍼 메서드 추가

### 4. 컨텍스트 검증 (§8c)

- **파일**: `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- context_collection 단계에서 코드 크기 검증 추가 (32KB 초과 시 절단)
- 절단 시 원본 크기와 절단 크기를 로그 및 단계 메시지에 표시

### 5. 적용 실패 분기 (§8g)

- **파일**: `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`
- `ApproveRunChangeAsync`에서 원본 텍스트 매칭 실패 시 edit 취소 (using 블록 종료로 자동 취소)
- edit.Apply() 예외 발생 시 catch로 적용 실패 처리
- 적용 실패 시 `Applied=false`, Build/Tests `Attempted=false`로 client-result 전송 (build_test Skipped)
- 적용 성공/실패 모두 최종 요약 폴링까지 진행

## 문서 갱신

- `.agents/modules.md`: Module 1 RunOrchestrator 설명에 코드 크기 검증 추가, Module 6 코드 적용 모드에 §8g 적용 실패 분기 반영, 구현 파일 목록에 ChatMessageViewModel 역할 상세 추가
- `.agents/pipeline.md`: 4c context_collection에 32KB 절단 명시, 7a applying에 원본 매칭 실패/예외 시 Skipped 처리 명시
