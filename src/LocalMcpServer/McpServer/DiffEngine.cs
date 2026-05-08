namespace LocalMcpServer.McpServer;

/// <summary>
/// 두 문자열을 라인 단위로 비교하여 DiffHunkInfo 목록을 반환한다.
/// LCS 기반 diff 알고리즘. 외부 의존성 없음.
/// A-2: 서버 측 hunks 사전 계산 용도.
/// </summary>
internal static class DiffEngine
{
    /// <summary>
    /// 원본 코드와 수정 코드를 비교하여 변경된 구간(hunk) 목록을 반환한다.
    /// </summary>
    public static IReadOnlyList<DiffHunkInfo> Compute(string original, string modified)
    {
        var origLines = SplitLines(original);
        var modLines  = SplitLines(modified);

        var ops = ComputeEditScript(origLines, modLines);
        return BuildHunks(ops, origLines, modLines);
    }

    // ── 라인 분할 ────────────────────────────────────────────

    private static string[] SplitLines(string text)
    {
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

    // ── LCS 기반 edit script ──────────────────────────────────

    private enum EditOp { Equal, Delete, Insert }

    private struct Edit
    {
        public EditOp Op;
        public int OldIndex;
        public int NewIndex;
    }

    private static List<Edit> ComputeEditScript(string[] orig, string[] mod)
    {
        int n = orig.Length;
        int m = mod.Length;

        if (n == 0 && m == 0) return [];

        int[,] lcsLen = new int[n + 1, m + 1];
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                lcsLen[i, j] = orig[i - 1] == mod[j - 1]
                    ? lcsLen[i - 1, j - 1] + 1
                    : Math.Max(lcsLen[i - 1, j], lcsLen[i, j - 1]);

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

    // ── Edit script → DiffHunkInfo 변환 ─────────────────────

    private static List<DiffHunkInfo> BuildHunks(List<Edit> edits, string[] orig, string[] mod)
    {
        var hunks = new List<DiffHunkInfo>();
        int i = 0;

        while (i < edits.Count)
        {
            if (edits[i].Op == EditOp.Equal) { i++; continue; }

            int origStart = -1, origEnd = -1;
            var newText = new System.Text.StringBuilder();

            while (i < edits.Count && edits[i].Op != EditOp.Equal)
            {
                var e = edits[i];
                if (e.Op == EditOp.Delete)
                {
                    if (origStart < 0) origStart = e.OldIndex;
                    origEnd = e.OldIndex + 1;
                }
                else
                {
                    newText.Append(mod[e.NewIndex]);
                }
                i++;
            }

            if (origStart < 0)
            {
                int insertBefore = (i < edits.Count && edits[i].Op == EditOp.Equal)
                    ? edits[i].OldIndex : orig.Length;
                origStart = insertBefore;
                origEnd   = insertBefore;
            }

            if (origEnd < 0) origEnd = origStart;

            hunks.Add(new DiffHunkInfo(origStart, origEnd, newText.ToString()));
        }

        return hunks;
    }
}
