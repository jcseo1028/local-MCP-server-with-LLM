# 스펙 변경: Diff 기반 코드 편집 적용 (Copilot Agent 방식)

## 배경 및 문제

### 현재 동작 (Full Overwrite)

`SummaryToolWindowControl.ApproveRunChangeAsync`에서 사용자가 코드 변경을 승인하면:

```
// 전체 파일 모드
edit.Replace(0, snapshot.Length, modifiedCode);  // ← 문서 전체 교체

// 선택 영역 모드
int idx = fullText.IndexOf(normalizedOriginal);
edit.Replace(idx, normalizedOriginal.Length, modifiedCode);  // ← 청크 교체
```

**문제점:**

| 항목 | 현재 방식 | 영향 |
|------|----------|------|
| 전체 교체 | LLM 출력 전체를 문서에 덮어씀 | LLM이 변경한 3줄 때문에 1,000줄 전체가 교체됨 |
| Undo 단위 | 단일 Replace 1건 | Undo 시 모든 변경이 한 번에 롤백됨 |
| 동시 편집 손실 | 코드가 서버에 전송된 뒤 사용자가 에디터를 수정했으면 해당 변경이 사라짐 | 데이터 손실 위험 |
| 포맷 불일치 | 줄바꿈·들여쓰기 정규화 후에도 LLM 출력의 미세 포맷 차이가 전파 | 불필요한 diff noise 발생 |
| SelectionOnly 취약 | 원본 텍스트 정확 매칭 실패 시 적용 불가 | LLM이 공백을 하나라도 바꾸면 실패 |

### Copilot Agent 방식

GitHub Copilot Agent는 아래 단계로 코드를 수정한다:

1. 원본 코드와 수정 코드를 비교하여 **Unified Diff(line-level)** 를 계산한다.
2. 변경된 라인 범위(hunk)만 `ITextBuffer.Replace`로 교체한다.
3. 변경이 없는 라인은 절대 건드리지 않는다.
4. 각 hunk는 독립적인 `ITextEdit` 트랜잭션으로 적용된다(혹은 하나의 트랜잭션에 모든 hunk를 묶되 span은 각각 정확히 지정).

---

## 목표

LLM 코드 수정 결과를 에디터에 적용할 때 **전체 교체** 대신 **라인 수준 Diff Hunk 적용** 방식으로 변경한다.

### 기대 효과

- 변경된 라인만 교체 → 불필요한 포맷 noise 없음
- 코드 전송 이후 에디터에서 수정한 내용이 변경되지 않은 라인이면 보존됨
- VS Undo 스택에 의미 있는 단위로 기록됨
- SelectionOnly 모드의 원본 매칭 실패 문제 해소 (라인 번호 기준으로 대체)

---

## 영향 범위

| 모듈 | 변경 필요 | 설명 |
|------|----------|------|
| **VS Extension (VSIX)** | ✅ 필수 | `ApproveRunChangeAsync` 교체, diff 엔진 추가 |
| MCP Server | ❌ 없음 | 서버는 여전히 수정된 전체 코드를 반환 |
| Tool Registry | ❌ 없음 | LLM 프롬프트·출력 형식 변경 없음 |
| LLM Connector | ❌ 없음 | |
| Resource Cache | ❌ 없음 | |
| Configuration | ❌ 없음 | |
| contracts.md | ⚠️ 선택 | CodeChange 구조체에 hunks 필드 추가 (선택, §10 / §11) |

---

## 설계 결정

### A. Diff 계산 위치: 클라이언트(VSIX) 측

서버는 수정된 전체 코드를 그대로 반환하고, VSIX가 원본과 비교하여 diff를 계산한다.

**이유:**
- 서버·프롬프트 변경 없이 VSIX만 수정하면 됨 (최소 변경 원칙)
- 원본 코드는 이미 VSIX(`msg.CodeChange.Original`)에 있음
- Diff 알고리즘은 순수 클라이언트 로직으로 구현 가능

### B. Diff 알고리즘: DiffMatchPatch (라이선스: Apache 2.0)

`google/diff-match-patch`의 C# 포트를 NuGet 패키지 없이 단일 소스 파일로 내장한다.
또는 Myers LCS 기반 간단한 라인 diff를 직접 구현한다 (외부 의존성 회피).

> **권장**: 간단한 LCS 기반 라인 diff를 `Services/LineDiffEngine.cs`로 직접 구현.  
> 이유: NuGet 추가 없이 오프라인 환경에서 안정적으로 동작.

### C. 적용 단위: 단일 ITextEdit 트랜잭션, 복수 Replace 연산

```
using (var edit = docView.TextBuffer.CreateEdit())
{
    // 뒤에서 앞으로 순서로 hunk 적용 (offset 불변 유지)
    foreach (var hunk in hunks.OrderByDescending(h => h.OriginalStart))
    {
        var span = GetLineSpan(snapshot, hunk.OriginalStart, hunk.OriginalEnd);
        edit.Replace(span, hunk.NewText);
    }
    edit.Apply();
}
```

- 단일 `Apply()`이므로 VS Undo 스택에 1건으로 기록됨
- hunk를 역순으로 처리해 앞 hunk 적용이 뒤 hunk의 offset을 바꾸는 문제를 방지

---

## 구현 스펙

### 1. `Services/LineDiffEngine.cs` (신규)

```
책임: 두 문자열을 라인 단위로 비교하여 DiffHunk 목록을 반환한다.

public sealed class LineDiffEngine
{
    // 원본 코드와 수정 코드를 비교하여 변경된 구간 목록 반환
    public static IReadOnlyList<DiffHunk> Compute(string original, string modified);
}

public sealed class DiffHunk
{
    public int OriginalStart { get; }   // 원본 기준 시작 라인 (0-based)
    public int OriginalEnd   { get; }   // 원본 기준 끝 라인 (exclusive)
    public string NewText    { get; }   // 교체할 새 텍스트 (줄바꿈 포함)
}
```

알고리즘: Myers diff (O(ND)) 또는 Longest Common Subsequence.
요구사항:
- 변경 없는 라인은 hunk에 포함하지 않는다.
- 연속 변경 라인은 하나의 hunk로 묶는다.
- 빈 diff (원본==수정) 시 빈 목록 반환.

### 2. `ToolWindows/SummaryToolWindowControl.cs` 수정

#### `ApproveRunChangeAsync` 변경 전/후

**Before (전체 교체):**
```csharp
using (var edit = docView.TextBuffer.CreateEdit())
{
    edit.Replace(0, snapshot.Length, modifiedCode);
    edit.Apply();
}
```

**After (Diff Hunk 적용):**
```csharp
var originalText = msg.CodeChange.SelectionOnly
    ? msg.CodeChange.Original
    : snapshot.GetText();

var hunks = LineDiffEngine.Compute(originalText, modifiedCode);

if (hunks.Count == 0)
{
    _txtStatus.Text = "변경 없음: LLM 출력이 원본과 동일합니다.";
    return;
}

// SelectionOnly: 문서 내 원본 위치를 찾아 offset 보정
int baseOffset = 0;
if (msg.CodeChange.SelectionOnly && !string.IsNullOrEmpty(msg.CodeChange.Original))
{
    var fullText = snapshot.GetText();
    var normalized = NormalizeNewlines(msg.CodeChange.Original, snapshot);
    baseOffset = fullText.IndexOf(normalized, StringComparison.Ordinal);
    if (baseOffset < 0)
    {
        applyFailed = true;
        applyErrorMsg = "원본 텍스트 매칭 실패: 문서가 변경되었을 수 있습니다.";
        goto ApplyFailed;
    }
}

using (var edit = docView.TextBuffer.CreateEdit())
{
    // 역순 적용으로 offset 불변 유지
    foreach (var hunk in hunks.OrderByDescending(h => h.OriginalStart))
    {
        var span = GetLineSpanInSnapshot(snapshot, hunk.OriginalStart,
                                         hunk.OriginalEnd, baseOffset);
        edit.Replace(span, hunk.NewText);
    }
    edit.Apply();
}
```

#### 신규 헬퍼: `GetLineSpanInSnapshot`

```csharp
// snapshot 내에서 baseOffset을 기준으로 시작 라인부터 끝 라인까지의 Span 반환
private static Span GetLineSpanInSnapshot(
    ITextSnapshot snapshot, int startLine, int endLine, int baseOffset)
```

#### SelectionOnly 로직 단순화

현재 `fullText.IndexOf(normalizedOriginal)` 방식은 유지하되,  
매칭 실패 시 에러 처리 로직은 기존과 동일하게 유지한다.  
(매칭 성공 시 `baseOffset`으로 보정)

### 3. 코드 길이 비율 검증 (기존 유지)

30% 미만 ratio 체크는 그대로 유지한다.  
Diff 계산 전에 실행하여 명백히 잘린 출력을 차단한다.

### 4. Side-by-side Diff UI (현재 유지)

Tool Window의 diff 표시 UI(현재 원본/수정 나란히 표시)는 변경하지 않는다.  
에디터 **적용** 방식만 변경한다.  
향후 per-hunk accept/reject UI 추가는 별도 스펙으로 분리한다.

---

## 계약 변경 (contracts.md)

서버 응답 형식은 변경하지 않는다. 선택적 필드만 추가한다.

```
// §10 / §11 CodeChange 구조체에 선택 필드 추가
CodeChange {
  original: string        // 원본 코드
  modified: string        // 수정 코드 (전체 텍스트, 기존과 동일)
  selectionOnly: boolean  // 선택 영역만 적용 여부
  hunks?: [               // (선택) 서버가 사전 계산한 diff hunks — VSIX가 없으면 직접 계산
    {
      originalStart: number   // 원본 기준 시작 라인 (0-based)
      originalEnd: number     // 원본 기준 끝 라인 (exclusive)
      newText: string         // 교체 텍스트
    }
  ]
}
```

서버가 `hunks`를 제공하지 않으면 VSIX가 직접 `LineDiffEngine.Compute`로 계산한다.

---

## 구현 난이도 평가

| 항목 | 난이도 | 이유 |
|------|--------|------|
| LineDiffEngine 구현 (Myers diff) | 중 | 알고리즘 구현 필요, 단 단독 모듈이라 테스트 용이 |
| ApproveRunChangeAsync 교체 | 하 | 기존 Replace 1줄을 hunk 루프로 교체 |
| GetLineSpanInSnapshot 구현 | 하 | VS ITextSnapshot API 사용 |
| SelectionOnly baseOffset 보정 | 하 | 기존 매칭 로직 재활용 |
| per-hunk UI (향후) | 상 | 별도 스펙 필요 |

**전체 난이도: 중** — 약 2-3일 작업 예상 (LineDiffEngine 테스트 포함)

---

## 구현 시 주의사항

1. **줄바꿈 통일**: `NormalizeNewlines` 처리를 diff 입력 전에 실행. LF/CRLF 혼재 시 hunk 경계 오계산 방지.
2. **역순 적용 필수**: hunk를 `OriginalStart` 내림차순으로 정렬 후 적용해야 앞 hunk 적용이 뒤 hunk의 offset을 무효화하지 않음.
3. **빈 hunk 방어**: `hunks.Count == 0`이면 "변경 없음" 처리 후 승인 플로우만 진행.
4. **비율 검증 선행**: 기존 30% ratio 체크를 diff 전에 실행해 LLM 잘린 출력 조기 차단.
5. **SelectionOnly + 라인 번호**: `baseOffset` 보정 시 `GetLineSpanInSnapshot`에 offset을 전달해 실제 문서 위치로 변환.

---

## 미결 항목 (향후 스펙)

- [ ] per-hunk accept/reject UI: 각 hunk별로 수락/거부 버튼 표시
- [ ] 서버 측 `hunks` 사전 계산: MCP Server가 LLM 응답 파싱 시 diff를 계산해 포함
- [ ] Inline diff 데코레이션: VS 에디터에 직접 추가/삭제 라인 하이라이트 표시 (IClassifier 기반)
