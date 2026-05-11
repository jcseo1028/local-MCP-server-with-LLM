# 2026-05-11 변경 기록: v2.6.4 파일별 단건 편집 조합

## 목적
- 멀티파일 편집 응답 형식([FILE: ...])을 모델이 직접 출력해야 하는 의존성을 줄인다.
- organize_imports, refactor_current_code를 파일별 단건 호출로 실행하고 서버가 결과를 조합한다.
- 다른 선택 파일은 요약된 보조 컨텍스트로만 전달한다.

## 코드 변경
- `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
  - 멀티파일 편집 도구 요청 시 `GeneratePerFileProposalAsync()`로 분기
  - 파일별 단건 실행 후 `FileChange` 목록으로 조합
  - 관련 파일 요약(`BuildRelatedFilesContext`, `SummarizeFileForContext`) 추가
  - 파일별 using/import 검증을 기존 단건 검증 함수로 수행
- `src/LocalMcpServer/ToolRegistry/CodeToolBase.cs`
  - 프롬프트 변수 `related_files_context` 전달 추가
- `src/LocalMcpServer/prompts/refactor_current_code.prompt.md`
  - 관련 파일 요약은 참고용이며 현재 파일만 출력하도록 지시 추가
- `src/LocalMcpServer/prompts/organize_imports.prompt.md`
  - 관련 파일 요약은 참고용이며 현재 파일만 출력하도록 지시 추가

## 문서 반영
- `.agents/modules.md`에 파일별 단건 조합/보조 컨텍스트 동작 반영
- `README.md`에 v2.6.4 항목 추가

## 검증
- `dotnet build` 성공
