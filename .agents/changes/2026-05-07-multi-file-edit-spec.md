# 스펙: 멀티 파일 편집 확장 (2차)

## 전제: 현재 1차 구현의 구조적 한계

### 단일 파일 고정 포인트 목록

| 위치 | 현재 구조 | 제약 |
|------|----------|------|
| `contracts.md §11a` `ChatRunStartRequest` | `activeFilePath: string\|null`, `code: string\|null` | 파일 1개 + 코드 1개만 전달 가능 |
| `contracts.md §11b` `ChatRunSnapshot.proposal` | `original/modified: string\|null` | 변경 제안 1건만 보유 |
| `RunModels.cs` `RunProposal` | `Original/Modified: string?` | 서버 측 단건 |
| `RunModels.cs` `RunData` | `ActiveFilePath: string?`, `Code: string?` | 단일 파일 컨텍스트 |
| `ChatMessageViewModel.cs` `CodeChangeInfo` | `Original/Modified/SelectionOnly` — 파일 경로 없음 | 어떤 파일인지 모름 |
| `SummaryToolWindowControl` `ApproveRunChangeAsync` | `VS.Documents.GetActiveDocumentViewAsync()` — 현재 활성 문서만 대상 | 승인 시점에 활성 문서가 바뀌면 엉뚱한 파일에 적용됨 |

---

## 목표

하나의 LLM 요청으로 **여러 파일을 동시에 수정**할 수 있도록 시스템 전체를 확장한다.

### 기대 시나리오

- "인터페이스 메서드를 추가하고 모든 구현체에 반영해줘" → `IService.cs` + 3개 구현 파일 동시 수정
- "DTO 클래스명을 변경하고 참조 파일 모두 반영해줘" → 선언 파일 + N개 참조 파일
- "이 기능을 Controller/Service/Repository 3계층에 추가해줘" → 3개 파일 신규 + 수정

---

## 영향 범위

| 모듈 | 변경 필요 | 범위 |
|------|----------|------|
| **contracts.md** | ✅ 필수 | §11a, §11b, §11d 계약 구조 변경 |
| **MCP Server** (`RunModels.cs`, `RunOrchestrator.cs`) | ✅ 필수 | `RunProposal` 복수화, 오케스트레이터 변경 제안 생성 방식 확장 |
| **VS Extension VSIX** | ✅ 필수 | `CodeChangeInfo` 파일 경로 추가, 승인 로직 파일별 루프, UI 파일 목록 표시 |
| Tool Registry, LLM Connector, Resource Cache | ❌ 없음 | LLM 출력 형식은 서버가 파싱하여 다건으로 변환 |

---

## 설계 결정

### A. 요청 구조: 파일 목록 전달

현재는 `code` + `activeFilePath` 단건이지만, 멀티 파일 요청은 파일 목록 배열로 확장한다.
단, 하위 호환성을 위해 기존 단건 필드는 유지하고 `files[]`를 optional로 추가한다.

```
ChatRunStartRequest (변경)
  기존 유지:
    message, language, selectionOnly, conversationId, solutionPath, intentAndPlanOnly
    code: string | null          // 단일 파일 코드 (하위 호환)
    activeFilePath: string | null // 단일 파일 경로 (하위 호환)
  신규 추가:
    files: [                     // 멀티 파일 컨텍스트 (null이면 단건 필드 사용)
      {
        filePath: string         // 절대 경로
        code: string             // 파일 전체 코드
        language: string | null
        selectionOnly: boolean   // 이 파일에서 선택 영역만 수정 대상
        selectedCode: string | null // selectionOnly=true 일 때 선택된 텍스트
      }
    ] | null
```

서버는 `files`가 non-null이면 멀티 파일 모드로 처리한다.

### B. 제안 구조: 단건 → 배열

```
RunProposal (변경)
  기존 유지:
    summary: string
    requiresApproval: boolean
  변경:
    original: string | null      → 제거 (단건 하위 호환 필드로만 유지, 신규 코드에서 사용 금지)
    modified: string | null      → 제거 (동일)
  신규:
    changes: [                   // 파일별 변경 목록
      {
        filePath: string         // 대상 파일 절대 경로
        original: string         // 원본 코드 (파일 전체 또는 선택 영역)
        modified: string         // 수정된 코드
        selectionOnly: boolean
        isNewFile: boolean       // true이면 새 파일 생성
        description: string | null // 이 파일에서 무엇을 변경했는지 요약
      }
    ]
```

하위 호환: `changes` 배열이 없고 `original/modified`가 있으면 기존 단건 방식으로 처리.

### C. LLM 출력 형식 변경 (프롬프트 수준)

현재 코드 수정 도구는 수정된 코드 전체를 텍스트로 반환한다.
멀티 파일 지원을 위해 구조화된 응답 형식을 프롬프트에서 요구한다.

**신규 프롬프트 지시 (코드 수정 도구 공통)**:
```
여러 파일을 수정해야 할 경우 아래 형식으로 응답하라:

[FILE: <파일 경로>]
<수정된 전체 코드>
[/FILE]

파일이 하나일 경우에는 기존과 동일하게 코드만 반환해도 된다.
```

서버 `RunOrchestrator`가 LLM 응답을 파싱하여 `[FILE:]...[/FILE]` 블록 유무에 따라
단건/다건 `RunProposal.changes`를 생성한다.

### D. 승인 단위: 전체 일괄 or 파일별 선택

**1차 확장 (v2.2)**: 전체 일괄 승인/거부만 지원. UI는 파일별 diff를 순서대로 표시.
**2차 확장 (v2.3, 미결)**: 파일별 개별 승인/거부 (체크박스 기반).

이유: 전체 일괄 방식이 단순하고 LLM 수정이 파일 간 의존성을 갖는 경우 부분 적용이 더 위험함.

### E. 적용 로직: 파일 경로 기준으로 문서 오픈 + 적용

현재 `GetActiveDocumentViewAsync()`로 현재 활성 문서를 가져오는 방식에서,
`VS.Documents.OpenAsync(filePath)`로 대상 파일을 열고 `TextBuffer`를 얻는 방식으로 변경한다.

```
foreach (change in proposal.changes) {
    var docView = await VS.Documents.OpenAsync(change.FilePath);
    // 기존 hunk 적용 로직 재사용 (LineDiffEngine)
}
```

신규 파일(`isNewFile=true`)은 `File.WriteAllTextAsync`로 생성 후 VS에서 오픈한다.

---

## 계약 변경 상세 (contracts.md 갱신 대상)

### §11a `ChatRunStartRequest` 추가 필드

```
files: [
  {
    filePath: string
    code: string
    language: string | null
    selectionOnly: boolean
    selectedCode: string | null
  }
] | null
```

### §11b `ChatRunSnapshot.proposal` 변경

```
proposal: {
  summary: string
  requiresApproval: boolean
  // 하위 호환 (단건, deprecated):
  original: string | null
  modified: string | null
  // 신규 (다건):
  changes: [
    {
      filePath: string
      original: string
      modified: string
      selectionOnly: boolean
      isNewFile: boolean
      description: string | null
    }
  ] | null
} | null
```

### §11d `ChatRunClientResultRequest` 변경

기존 `appliedTargets: [string]`은 이미 파일 경로 목록 형태이므로 그대로 유지.
부분 실패를 위해 `applyResults` 배열 추가:

```
applyResults: [                 // 파일별 적용 결과 (null이면 단건 applied 필드 사용)
  {
    filePath: string
    applied: boolean
    message: string | null
  }
] | null
```

---

## 구현 스펙

### 1. 서버: `RunModels.cs` 변경

```csharp
// 기존 RunProposal 확장 (하위 호환 유지)
public sealed class RunProposal
{
    public string Summary { get; set; } = "";
    public bool RequiresApproval { get; set; }

    // 하위 호환 단건 (deprecated — 신규 코드에서 사용 금지)
    public string? Original { get; set; }
    public string? Modified { get; set; }

    // 신규 다건
    public List<FileChange>? Changes { get; set; }

    // 헬퍼: 단건/다건 모두 지원
    public bool IsMultiFile => Changes != null && Changes.Count > 0;
}

public sealed class FileChange
{
    public string FilePath { get; set; } = "";
    public string Original { get; set; } = "";
    public string Modified { get; set; } = "";
    public bool SelectionOnly { get; set; }
    public bool IsNewFile { get; set; }
    public string? Description { get; set; }
}
```

### 2. 서버: `RunOrchestrator.cs` — proposal_generation 단계 변경

현재: LLM 응답 전체를 `Modified`에 저장.
변경: `[FILE:...]...[/FILE]` 블록 파싱을 시도하고, 블록이 있으면 `Changes[]`로, 없으면 단건으로 저장.

```
파싱 로직 위치: RunOrchestrator.ParseProposalFromLlmOutput(string llmOutput, RunData run)
  → List<FileChange>가 비어있으면 단건(Original/Modified) 유지
  → List<FileChange>가 1건 이상이면 Changes에 저장
```

### 3. VSIX: `ChatMessageViewModel.cs` — `CodeChangeInfo` 확장

```csharp
internal sealed class CodeChangeInfo
{
    // 하위 호환 단건
    public string Original { get; set; } = "";
    public string Modified { get; set; } = "";
    public string ToolName { get; set; } = "";
    public bool SelectionOnly { get; set; }

    // 신규 다건
    public List<FileChangeInfo>? Files { get; set; }
    public bool IsMultiFile => Files != null && Files.Count > 0;
}

internal sealed class FileChangeInfo
{
    public string FilePath { get; set; } = "";
    public string Original { get; set; } = "";
    public string Modified { get; set; } = "";
    public bool SelectionOnly { get; set; }
    public bool IsNewFile { get; set; }
    public string? Description { get; set; }
}
```

### 4. VSIX: `ApproveRunChangeAsync` — 파일별 루프

```
현재:
  GetActiveDocumentViewAsync() → hunk 적용

변경:
  if (msg.CodeChange.IsMultiFile)
  {
      foreach (var fileChange in msg.CodeChange.Files)
      {
          if (fileChange.IsNewFile)
              await CreateNewFileAsync(fileChange);
          else
              await ApplyHunksToFileAsync(fileChange);  // LineDiffEngine 재사용
      }
  }
  else
  {
      // 기존 단건 로직 유지
  }
```

### 5. VSIX: `CreateDiffView` — 파일 목록 UI

멀티 파일 시: 파일별 아코디언(Expander) 구성
```
[▼ FileA.cs  +3줄 -1줄]   ← Expander 헤더
  [unified diff 뷰]        ← Expander 본문

[▼ FileB.cs  +10줄 -0줄]
  [unified diff 뷰]
```

단건 시: 현재 방식 그대로 (하위 호환).

### 6. VSIX: `BtnSend_Click` — 멀티 파일 컨텍스트 수집

현재: 활성 문서 1개만 `code`/`activeFilePath`로 전달.
v2.2 우선 방식: 활성 문서 1개 전달 + 사용자 메시지에서 파일 경로 언급이 있으면 추가 수집 (optional).

> **v2.2 범위 제한**: VSIX UI에서 파일을 명시적으로 선택하는 기능(파일 선택 다이얼로그 등)은 v2.3에서 추가한다.
> v2.2에서는 서버가 LLM에게 멀티 파일 수정이 필요하다고 판단하면 먼저 VSIX에 `"어떤 파일들을 수정해야 하나요?"` 안내 메시지를 반환하고, 사용자가 추가 파일을 명시하도록 유도하는 대화형 방식을 1차로 채택한다.

---

## 구현 난이도 평가

| 항목 | 난이도 | 이유 |
|------|--------|------|
| `RunProposal.Changes[]` 추가 (하위 호환) | 하 | 필드 추가, 기존 단건 경로 유지 |
| `[FILE:] 블록 파싱` (RunOrchestrator) | 하~중 | 정규식 파싱, LLM 출력 형식 변동성 감안 필요 |
| `CodeChangeInfo.Files[]` VSIX 확장 | 하 | 필드 추가 |
| `ApproveRunChangeAsync` 파일별 루프 | 하 | LineDiffEngine 재사용, 루프만 추가 |
| `VS.Documents.OpenAsync` 파일 오픈 | 하 | Community.VisualStudio.Toolkit API |
| 신규 파일 생성 (`IsNewFile`) | 하 | `File.WriteAllTextAsync` + `VS.Documents.OpenAsync` |
| `CreateDiffView` Expander UI | 중 | WPF Expander 구성, 파일별 diff |
| 멀티 파일 컨텍스트 수집 UI (v2.3) | 상 | 별도 스펙 |
| 파일별 개별 승인/거부 (v2.3) | 상 | 별도 스펙 |

**v2.2 전체 난이도: 중** — 약 2~3일 예상

---

## 구현 순서 (v2.2)

1. `contracts.md` §11a / §11b / §11d 계약 갱신
2. `RunModels.cs` — `RunProposal.Changes[]` + `FileChange` 추가
3. `RunOrchestrator.cs` — `ParseProposalFromLlmOutput` 파싱 로직 추가
4. `ChatMessageViewModel.cs` — `CodeChangeInfo.Files[]` + `FileChangeInfo` 추가
5. `SummaryToolWindowControl.cs` — `ApproveRunChangeAsync` 파일별 루프
6. `SummaryToolWindowControl.cs` — `CreateDiffView` Expander UI (단건 시 기존 유지)
7. 빌드 검증

---

## 미결 항목 (v2.3 이후 스펙)

- [ ] 파일별 개별 승인/거부 (체크박스 기반)
- [ ] VSIX UI에서 수정 대상 파일 직접 선택 (파일 선택 다이얼로그 or 트리뷰)
- [ ] 부분 적용 실패 시 롤백 정책 (파일 A는 성공, B는 실패 → A를 롤백할지 여부)
- [ ] 프롬프트 `[FILE:]` 블록 형식의 LLM 출력 안정성 검증 및 대안 파싱 방식 (JSON 구조화 출력)
- [ ] 대용량 멀티 파일 컨텍스트의 토큰 초과 대응 (파일별 청크 분할 전략)
