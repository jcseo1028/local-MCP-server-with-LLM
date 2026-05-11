# 2026-05-11 변경 기록: Run 안정화(키워드·계획 파싱·타임아웃 예산·취소 사유)

## 배경

- 의도/계획 단계에서 `gemma4` 빈 응답이 반복되면 초반 대기 시간이 누적됨
- 멀티파일 포맷 파싱 실패 시 재시도가 추가되어 Run 전체 시간 예산을 빠르게 소진함
- 취소 로그가 `타임아웃 또는 서버 종료`로 합쳐져 원인 진단이 어려움
- `리팩토링` 표현이 다중 도구 계획 단계(`refactor_current_code`)로 연결되지 않는 케이스가 발생함

## 코드 변경

### 1) 계획 파서 빈 항목 정리
- 파일: `src/LocalMcpServer/McpServer/IntentResolver.cs`
- 변경: `ParsePlanResponse()`에서 LLM 결과가 빈/공백이면 `[]` 반환
- 효과: UI에 의미 없는 `1개 항목(빈 문자열)`이 표시되는 문제 완화

### 2) 다중 도구 계획 키워드 보강
- 파일: `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- 변경: `BuildToolPlan()`의 `refactor_current_code` 키워드에 `리팩토링` 추가
- 효과: 한국어 요청에서 리팩토링 단계 누락 감소

### 3) 멀티파일 재시도 시간 예산 가드
- 파일: `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- 변경:
  - `RunTimeout = 10분`, `MultiFileRetryMinBudget = 90초` 상수 추가
  - `[FILE:]` 파싱 실패 재시도 전에 남은 실행 시간 확인
  - 예산 부족 시 재시도 생략 + 명시적 안내 메시지 반환
- 효과: 장시간 대기 후 취소되는 비효율 감소

### 4) 취소 사유 분리 로깅
- 파일: `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- 변경: `OperationCanceledException` 처리 시
  - 타임아웃 취소 로그/에러 메시지
  - 외부 취소(서버 중단/요청 취소) 로그/에러 메시지
  로 분리
- 효과: 원인 분석 및 운영 관측성 향상

## 문서 반영

- `.agents/modules.md`: RunOrchestrator 안정화(v2.6.1) 항목 추가
- `README.md`: v2.6 추가 기능에 안정화 4개 항목(키워드/계획 파싱/재시도 예산/취소 로그) 반영

## 검증

- `src/LocalMcpServer`에서 `dotnet build` 성공
