using System;
using System.Collections.Generic;

namespace LocalMcpVsExtension.Services
{
    /// <summary>
    /// 두 문자열을 라인 단위로 비교하여 DiffHunk 목록을 반환한다.
    /// Myers diff (O(ND)) 알고리즘 기반. 외부 의존성 없음.
    /// </summary>
    internal static class LineDiffEngine
    {
        /// <summary>
        /// 원본 코드와 수정 코드를 비교하여 변경된 구간(hunk) 목록을 반환한다.
        /// 변경 없는 라인은 hunk에 포함하지 않는다.
        /// </summary>
        public static IReadOnlyList<DiffHunk> Compute(string original, string modified)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (modified == null) throw new ArgumentNullException(nameof(modified));

            var origLines = SplitLines(original);
            var modLines  = SplitLines(modified);

            var ops = ComputeEditScript(origLines, modLines);
            return BuildHunks(ops, origLines, modLines);
        }

        // ── 라인 분할 ────────────────────────────────────────────

        private static string[] SplitLines(string text)
        {
            // 줄바꿈은 유지한 채로 분할 (각 라인 끝에 \n 포함)
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines.Add(text.Substring(start, i - start + 1));
                    start = i + 1;
                }
            }
            if (start < text.Length)
                lines.Add(text.Substring(start));
            return lines.ToArray();
        }

        // ── Myers diff: edit script 계산 ─────────────────────────

        private enum EditOp { Equal, Delete, Insert }

        private struct Edit
        {
            public EditOp Op;
            public int OldIndex; // origLines 기준 (-1이면 Insert)
            public int NewIndex; // modLines 기준 (-1이면 Delete)
        }

        private static List<Edit> ComputeEditScript(string[] orig, string[] mod)
        {
            int n = orig.Length;
            int m = mod.Length;

            if (n == 0 && m == 0) return new List<Edit>();

            // LCS 기반 DP: lcs[i,j] = orig[0..i-1] vs mod[0..j-1] 의 LCS 길이
            // n,m 이 큰 경우 메모리 절약을 위해 행 롤링
            int[] prev = new int[m + 1];
            int[] curr = new int[m + 1];
            int[,] lcsLen = new int[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    if (orig[i - 1] == mod[j - 1])
                        lcsLen[i, j] = lcsLen[i - 1, j - 1] + 1;
                    else
                        lcsLen[i, j] = Math.Max(lcsLen[i - 1, j], lcsLen[i, j - 1]);
                }
            }

            // LCS에서 edit script 역추적
            var edits = new List<Edit>();
            int oi = n, mi = m;
            while (oi > 0 || mi > 0)
            {
                if (oi > 0 && mi > 0 && orig[oi - 1] == mod[mi - 1])
                {
                    edits.Add(new Edit { Op = EditOp.Equal, OldIndex = oi - 1, NewIndex = mi - 1 });
                    oi--; mi--;
                }
                else if (mi > 0 && (oi == 0 || lcsLen[oi, mi - 1] >= lcsLen[oi - 1, mi]))
                {
                    edits.Add(new Edit { Op = EditOp.Insert, OldIndex = -1, NewIndex = mi - 1 });
                    mi--;
                }
                else
                {
                    edits.Add(new Edit { Op = EditOp.Delete, OldIndex = oi - 1, NewIndex = -1 });
                    oi--;
                }
            }

            edits.Reverse();
            return edits;
        }

        // ── Edit script → DiffHunk 변환 ─────────────────────────

        private static List<DiffHunk> BuildHunks(List<Edit> edits, string[] orig, string[] mod)
        {
            var hunks = new List<DiffHunk>();
            int i = 0;
            while (i < edits.Count)
            {
                // Equal 구간 건너뜀
                if (edits[i].Op == EditOp.Equal)
                {
                    i++;
                    continue;
                }

                // 변경 구간 시작
                int hunkOrigStart = edits[i].Op == EditOp.Delete
                    ? edits[i].OldIndex
                    : (edits[i - 1].Op == EditOp.Equal ? edits[i - 1].OldIndex + 1 : 0);

                // Delete/Insert 가 섞인 연속 구간 수집
                int origStart = -1;
                int origEnd   = -1;
                var newTextParts = new System.Text.StringBuilder();

                while (i < edits.Count && edits[i].Op != EditOp.Equal)
                {
                    var e = edits[i];
                    if (e.Op == EditOp.Delete)
                    {
                        if (origStart < 0) origStart = e.OldIndex;
                        origEnd = e.OldIndex + 1;
                    }
                    else // Insert
                    {
                        newTextParts.Append(mod[e.NewIndex]);
                    }
                    i++;
                }

                // Delete만 있는 경우 (origStart는 설정됐지만 Insert 없음)
                // Insert만 있는 경우 (origStart < 0 → 삽입 위치 결정 필요)
                if (origStart < 0)
                {
                    // 순수 삽입: 직전 Equal의 OldIndex 다음 위치
                    int insertBefore = (i < edits.Count && edits[i].Op == EditOp.Equal)
                        ? edits[i].OldIndex
                        : orig.Length;
                    // 삽입은 origStart==origEnd (빈 구간)
                    origStart = insertBefore;
                    origEnd   = insertBefore;
                }

                if (origEnd < 0)
                    origEnd = origStart;

                hunks.Add(new DiffHunk(origStart, origEnd, newTextParts.ToString()));
            }

            return hunks;
        }
    }

    /// <summary>
    /// 원본 코드에서 교체할 라인 범위와 교체 텍스트를 나타낸다.
    /// </summary>
    internal sealed class DiffHunk
    {
        /// <summary>원본 기준 시작 라인 (0-based, inclusive)</summary>
        public int OriginalStart { get; }

        /// <summary>원본 기준 끝 라인 (0-based, exclusive)</summary>
        public int OriginalEnd { get; }

        /// <summary>교체할 새 텍스트 (줄바꿈 포함)</summary>
        public string NewText { get; }

        public DiffHunk(int originalStart, int originalEnd, string newText)
        {
            OriginalStart = originalStart;
            OriginalEnd   = originalEnd;
            NewText       = newText;
        }
    }
}
