# v2.2 멀티 파일 편집 지원 — 구현 완료

## 날짜
2026-05-07

## 개요
`2026-05-07-multi-file-edit-spec.md` 스펙에 따라 v2.2 (멀티 파일 컨텍스트 전달 + `[FILE:]` 블록 파싱 + VSIX 분기 적용)를 구현했다.

## 변경된 파일

### 서버 (LocalMcpServer)

#### `src/LocalMcpServer/McpServer/RunModels.cs`
- `RunProposal`에 `List<FileChange>? Changes` + `bool IsMultiFile` 추가
- `RunData`에 `List<FileContext>? Files` 추가
- `ChatRunStartRequest`에 `List<FileContext>? Files` 추가
- `ChatRunClientResultRequest`에 `List<FileApplyResult>? ApplyResults` 추가
- 신규 클래스: `FileChange`, `FileContext`, `FileApplyResult`

#### `src/LocalMcpServer/McpServer/RunOrchestrator.cs`
- `StartRun`에서 `req.Files → run.Files` 매핑 추가
- `GenerateProposalAsync`: `ParseFileBlocks` 호출 추가, 결과에 따라 멀티/단일 파일 `Proposal` 분기
- 신규 메서드: `ParseFileBlocks(string llmOutput, RunData run) → List<FileChange>`
  - `[FILE: path]...[/FILE]` 정규식 파싱
  - `run.Files`에서 원본 코드 매핑
  - 원본이 없으면 `IsNewFile = true`

### VSIX (LocalMcpVsExtension)

#### `src/LocalMcpVsExtension/Services/ChatMessageViewModel.cs`
- `CodeChangeInfo`에 `List<FileChangeInfo>? Files` + `bool IsMultiFile` 추가
- 신규 클래스: `FileChangeInfo` (FilePath, Original, Modified, SelectionOnly, IsNewFile, Description)

#### `src/LocalMcpVsExtension/Services/McpRestClient.cs`
- `RunStartRequest`에 `RunFileContextDto[]? Files` 추가
- `RunProposalDto`에 `FileChangeDto[]? Changes` + `bool IsMultiFile` 추가
- `ClientResultRequest`에 `FileApplyResultDto[]? ApplyResults` 추가
- 신규 DTO 클래스: `FileChangeDto`, `RunFileContextDto`, `FileApplyResultDto`

#### `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`
- `WaitingForApproval` 처리: `proposal.Changes[]` → `CodeChangeInfo.Files[]` 매핑 추가
- `ApproveRunChangeAsync`: 멀티파일 분기 추가 (`if IsMultiFile` → 파일별 `ApplyHunksToFileAsync`/`CreateNewFileAsync`)
- `CreateDiffView`: 멀티파일이면 `Expander` per file UI; 단일파일은 `CreateSingleFileDiffView`로 위임
- 신규 메서드:
  - `CreateSingleFileDiffView` — 기존 단일파일 diff UI 로직 추출
  - `ApplyHunksToFileAsync(FileChangeInfo)` — 파일 오픈 + hunk 적용
  - `CreateNewFileAsync(string, string)` — 신규 파일 생성 + 에디터 열기

## 하위 호환성
- `RunProposal.Original / Modified` 필드 유지 (단일파일 폴백)
- `Changes == null`이면 기존 단일파일 경로로 동작
- VSIX: `IsMultiFile == false`이면 기존 로직 그대로 실행

## 빌드 결과
- `LocalMcpServer`: 컴파일 오류 없음 (서버 실행 중 EXE 잠금 경고는 런타임 이슈)
- `LocalMcpVsExtension`: 빌드 성공 → `bin/Debug/net48/LocalMcpVsExtension.vsix` 생성됨

## 미구현 (v2.3 이후)
- `RunStartRequest.Files[]` 실제 전송 (VSIX에서 현재 열린 파일들을 수집)
- 빌드 결과의 `ApplyResults[]` 전송
- LLM 프롬프트에 `[FILE:]` 블록 형식 안내 추가
