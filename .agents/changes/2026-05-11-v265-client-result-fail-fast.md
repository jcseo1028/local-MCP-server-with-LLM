# 2026-05-11 변경 기록: v2.6.5 client-result fail-fast

## 목적
- 확장에서 코드 적용 후 빌드/테스트 실패가 발생해도 서버가 다음 계획 step으로 진행하는 문제를 차단한다.
- 상태/로그를 실패 원인 중심으로 명확히 기록한다.

## 코드 변경
- `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
  - `ProcessClientResultAsync()`에 fail-fast 분기 추가
  - 적용 실패(`Applied=false`), 빌드 실패(`Build.Succeeded=false`), 테스트 실패(`Tests.Succeeded=false`) 시 즉시 Run `Failed` 전이
  - 현재 plan step 상태를 `Failed`로 반영하고 `run.Error`에 원인 기록
  - `_runLogger.LogFinalSummary()` 호출로 종료 상태 로그 일관화
- `src/LocalMcpServer/McpServer/McpEndpoints.cs`
  - client-result 수신 로그를 `attempted` 뿐 아니라 `succeeded` 값까지 출력하도록 개선

## 문서 반영
- `.agents/modules.md` v2.6.5 fail-fast 항목 추가
- `README.md` v2.6.5 fail-fast 항목 추가

## 검증
- `dotnet build` 성공
- 참고: 실행 중 프로세스 점유로 `LocalMcpServer.exe` 복사 경고(MSB3026) 발생 가능
