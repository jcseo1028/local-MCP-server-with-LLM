# 버그 수정 세션 (2026-05-08)

## 개요

이번 세션에서는 v2.3/v2.4 구현 이후 발견된 런타임 버그 3건을 수정하고,
README.md 중복 섹션을 정리했다.

---

## 1. B-2 런타임 버그: 파일 선택 버튼이 열린 파일을 인식하지 못하는 문제

### 증상
여러 소스 파일이 VS 에디터에 열려 있어도 파일 선택 패널에 "열린 코드 파일이 없습니다." 표시.

### 원인
`IEnumVsRunningDocumentTable.Next()`는 COM 열거자 표준 동작으로 마지막 배치에서 `S_FALSE(1)`를 반환한다.  
기존 코드는 `S_OK(0)`인 경우에만 루프를 진행했으므로, 열린 파일이 32개 미만이면  
첫 배치부터 `S_FALSE` → 즉시 루프 종료 → 0개 반환.

### 수정 파일
**`src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`**

- `CollectOpenFilesAsync()` 내 열거 루프 수정
- `while (S_OK)` → `do { } while (S_OK)` 구조로 변경
- `fetched == 0`이면 break, 그 외 S_FALSE여도 해당 배치를 처리 후 루프 종료

```csharp
// 변경 전
while (enumerator.Next(...) == S_OK && fetched > 0) { ... }

// 변경 후
int hr;
do {
    hr = enumerator.Next(...);
    if (fetched == 0) break;
    // ... 처리 ...
} while (hr == S_OK);
```

---

## 2. Hunk 적용 로직 교체: 줄바꿈/정렬 불일치 문제

### 증상
함수 리팩터링 등 여러 hunk가 생성되는 경우, 적용 후 줄바꿈 누락·들여쓰기 깨짐 발생.

### 원인
`GetLineSpanInSnapshot()`이 VS TextBuffer char offset으로 라인 범위를 변환하는 과정에서:
- 연속 hunk 적용 시 앞 hunk 적용 후 뒤 hunk span이 어긋남
- 순수 삽입 hunk(origStart == origEnd)에서 빈 span 위치 불일치
- `\r\n` vs `\n` 줄바꿈 경계 오차

### 수정 파일
**`src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`**

- `ApplyHunksToFileAsync()` 전면 교체
  - `GetLineSpanInSnapshot()` 개별 Replace 방식 제거
  - `SplitToLinesList()` — LineDiffEngine과 동일한 `\n` 기준 라인 분할
  - 라인 배열 레벨에서 역순 hunk 적용 후 `string.Concat()`으로 재조립
  - 최종 결과 텍스트를 단일 `edit.Replace()`로 교체 (Copilot agent 방식)
- `ApproveRunChangeAsync()` 단일 파일 경로의 hunk 적용 블록도 동일 방식으로 교체
- `GetLineSpanInSnapshot()` 메서드 제거
- `SplitToLinesList()` 정적 헬퍼 추가

---

## 3. `SummarizeCurrentCodeTool` 멀티 파일 요약 누락 버그

### 증상
파일을 2개 선택해도 요약 결과가 1개 파일 분량만 나타남.

### 원인
`SummarizeCurrentCodeTool.ExecuteAsync()`가 `code`와 `language` 인자만 읽고  
`RunOrchestrator`가 전달하는 `files_context`를 완전히 무시했다.  
프롬프트 템플릿에도 `{{files_context}}` 자리 표시자가 없었다.

### 수정 파일

**`src/LocalMcpServer/ToolRegistry/SummarizeCurrentCodeTool.cs`**
- `files_context` 인자 추출 추가
- `files_context`가 있으면 → `code_section = filesContext` (멀티 파일 모드)
- `files_context`가 없으면 → `code_section = "{language} 코드:\n```{language}\n{code}\n```"` (단건 모드)
- `LoadAndRenderAsync` 호출 시 `code_section` 단일 변수로 전달

**`src/LocalMcpServer/prompts/summarize_current_code.prompt.md`**
- `{{language}} 코드:\n```{{language}}\n{{code}}\n``` ` → `{{code_section}}` 으로 교체
- 단건/멀티 파일 모드 모두 동일 템플릿으로 처리

---

## 4. README.md 중복 섹션 정리

**`README.md`**
- `**v2.3 추가 기능:**` 섹션이 2회 등장하는 중복 제거 (line ~249 근처 두 번째 섹션 삭제)

---

## 영향 범위 요약

| 파일 | 변경 유형 |
|------|-----------|
| `src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs` | 버그 수정 (2건) |
| `src/LocalMcpServer/ToolRegistry/SummarizeCurrentCodeTool.cs` | 버그 수정 |
| `src/LocalMcpServer/prompts/summarize_current_code.prompt.md` | 버그 수정 |
| `README.md` | 중복 섹션 정리 |

## 빌드 검증

- `LocalMcpVsExtension` (Debug): 컴파일 오류 없음
- `LocalMcpServer`: 컴파일 오류 없음
