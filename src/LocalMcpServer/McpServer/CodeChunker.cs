using System.Text;
using System.Text.RegularExpressions;

namespace LocalMcpServer.McpServer;

/// <summary>
/// 코드 파일을 검색 친화적인 chunk로 분할한다.
/// </summary>
public sealed class CodeChunker
{
    private const int DefaultOverlapLines = 5;
    private readonly ILogger<CodeChunker> _logger;

    public CodeChunker(ILogger<CodeChunker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 코드를 chunk 목록으로 분할한다. C#은 구조 기반(class/region/method), 그 외는 라인 기반으로 처리한다.
    /// </summary>
    public List<CodeChunk> SplitIntoChunks(
        string code,
        string filePath,
        string language = "csharp",
        int maxChunkChars = 2000,
        int overlapLines = DefaultOverlapLines)
    {
        if (string.IsNullOrWhiteSpace(code))
            return [];

        if (maxChunkChars < 400)
            maxChunkChars = 400;

        if (overlapLines < 0)
            overlapLines = 0;

        var chunks = new List<CodeChunk>();

        if (!string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            chunks.AddRange(SplitByLines(code, filePath, maxChunkChars, overlapLines));
            return DeduplicateChunks(chunks);
        }

        chunks.AddRange(ExtractClassChunks(code, filePath, maxChunkChars, overlapLines));
        chunks.AddRange(ExtractRegionChunks(code, filePath, maxChunkChars, overlapLines));
        chunks.AddRange(ExtractMethodChunks(code, filePath, maxChunkChars, overlapLines));

        if (chunks.Count == 0)
        {
            _logger.LogDebug("구조 분할 결과가 없어 라인 분할로 폴백: {FilePath}", filePath);
            chunks.AddRange(SplitByLines(code, filePath, maxChunkChars, overlapLines));
        }

        return DeduplicateChunks(chunks);
    }

    private List<CodeChunk> ExtractClassChunks(
        string code,
        string filePath,
        int maxChunkChars,
        int overlapLines)
    {
        var chunks = new List<CodeChunk>();
        var pattern = @"(?:public|private|protected|internal)?\s*(?:partial\s+)?class\s+(\w+)[^{]*\{";
        var matches = Regex.Matches(code, pattern, RegexOptions.Multiline);

        foreach (Match match in matches)
        {
            var openBraceIndex = match.Index + match.Length - 1;
            var closeBraceIndex = FindMatchingBrace(code, openBraceIndex);
            if (closeBraceIndex <= openBraceIndex)
                continue;

            var startIndex = match.Index;
            var length = closeBraceIndex - startIndex + 1;
            var content = code.Substring(startIndex, length);
            var startLine = CountLineAt(code, startIndex);
            var endLine = CountLineAt(code, closeBraceIndex);
            var className = match.Groups[1].Value;

            AddChunkWithSizeLimit(chunks, new CodeChunk
            {
                FilePath = filePath,
                StartLine = startLine,
                EndLine = endLine,
                Type = ChunkType.Class,
                Name = className,
                Content = content,
                Summary = $"Class {className}"
            }, maxChunkChars, overlapLines);
        }

        return chunks;
    }

    private List<CodeChunk> ExtractRegionChunks(
        string code,
        string filePath,
        int maxChunkChars,
        int overlapLines)
    {
        var chunks = new List<CodeChunk>();
        var pattern = @"#region\s+([^\r\n]+)(.*?)#endregion";
        var matches = Regex.Matches(code, pattern, RegexOptions.Multiline | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var content = match.Value;
            var startLine = CountLineAt(code, match.Index);
            var endLine = CountLineAt(code, match.Index + match.Length - 1);
            var regionName = match.Groups[1].Value.Trim();

            AddChunkWithSizeLimit(chunks, new CodeChunk
            {
                FilePath = filePath,
                StartLine = startLine,
                EndLine = endLine,
                Type = ChunkType.Region,
                Name = regionName,
                Content = content,
                Summary = $"Region {regionName}"
            }, maxChunkChars, overlapLines);
        }

        return chunks;
    }

    private List<CodeChunk> ExtractMethodChunks(
        string code,
        string filePath,
        int maxChunkChars,
        int overlapLines)
    {
        var chunks = new List<CodeChunk>();

        var pattern =
            @"(?:public|private|protected|internal)?\s*(?:static|virtual|override|async|sealed|partial|new|extern|unsafe\s+)*[\w<>,\[\]\.?\s]+\s+(\w+)\s*\([^;{}]*\)\s*(?:where\s+[^{]+)?\{";
        var matches = Regex.Matches(code, pattern, RegexOptions.Multiline);

        foreach (Match match in matches)
        {
            var openBraceIndex = match.Index + match.Length - 1;
            var closeBraceIndex = FindMatchingBrace(code, openBraceIndex);
            if (closeBraceIndex <= openBraceIndex)
                continue;

            var startIndex = match.Index;
            var length = closeBraceIndex - startIndex + 1;
            var content = code.Substring(startIndex, length);
            var startLine = CountLineAt(code, startIndex);
            var endLine = CountLineAt(code, closeBraceIndex);
            var methodName = match.Groups[1].Value;

            AddChunkWithSizeLimit(chunks, new CodeChunk
            {
                FilePath = filePath,
                StartLine = startLine,
                EndLine = endLine,
                Type = ChunkType.Method,
                Name = methodName,
                Content = content,
                Summary = $"Method {methodName}"
            }, maxChunkChars, overlapLines);
        }

        return chunks;
    }

    private static List<CodeChunk> SplitByLines(
        string code,
        string filePath,
        int maxChunkChars,
        int overlapLines)
    {
        var chunks = new List<CodeChunk>();
        var lines = code.Replace("\r\n", "\n").Split('\n');

        var currentStart = 0;
        while (currentStart < lines.Length)
        {
            var sb = new StringBuilder();
            var currentEnd = currentStart;

            while (currentEnd < lines.Length)
            {
                var candidate = lines[currentEnd] + "\n";
                if (sb.Length > 0 && sb.Length + candidate.Length > maxChunkChars)
                    break;

                sb.Append(candidate);
                currentEnd++;
            }

            if (sb.Length == 0)
            {
                sb.Append(lines[currentStart]);
                currentEnd = currentStart + 1;
            }

            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                StartLine = currentStart + 1,
                EndLine = currentEnd,
                Type = ChunkType.LineBlock,
                Name = $"Lines {currentStart + 1}-{currentEnd}",
                Content = sb.ToString(),
                Summary = $"Lines {currentStart + 1}-{currentEnd}"
            });

            if (currentEnd >= lines.Length)
                break;

            var nextStart = Math.Max(currentStart + 1, currentEnd - overlapLines);
            currentStart = nextStart;
        }

        return chunks;
    }

    private static void AddChunkWithSizeLimit(
        List<CodeChunk> destination,
        CodeChunk chunk,
        int maxChunkChars,
        int overlapLines)
    {
        if (chunk.Content.Length <= maxChunkChars)
        {
            destination.Add(chunk);
            return;
        }

        var splitChunks = SplitByLines(chunk.Content, chunk.FilePath, maxChunkChars, overlapLines);
        foreach (var split in splitChunks)
        {
            destination.Add(new CodeChunk
            {
                FilePath = chunk.FilePath,
                StartLine = chunk.StartLine + split.StartLine - 1,
                EndLine = chunk.StartLine + split.EndLine - 1,
                Type = chunk.Type,
                Name = chunk.Name,
                Content = split.Content,
                Summary = chunk.Summary
            });
        }
    }

    private static int FindMatchingBrace(string code, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= code.Length || code[openBraceIndex] != '{')
            return -1;

        var depth = 1;
        for (var i = openBraceIndex + 1; i < code.Length; i++)
        {
            if (code[i] == '{')
                depth++;
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int CountLineAt(string code, int index)
    {
        if (index <= 0)
            return 1;

        var line = 1;
        for (var i = 0; i < index && i < code.Length; i++)
        {
            if (code[i] == '\n')
                line++;
        }

        return line;
    }

    private static List<CodeChunk> DeduplicateChunks(List<CodeChunk> chunks)
    {
        return chunks
            .OrderBy(c => c.StartLine)
            .ThenBy(c => c.EndLine)
            .ThenBy(c => c.Type)
            .DistinctBy(c => (c.FilePath, c.StartLine, c.EndLine, c.Type, c.Name ?? string.Empty))
            .ToList();
    }
}

public sealed class CodeChunk
{
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public ChunkType Type { get; set; }
    public string? Name { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public float[]? Embedding { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ChunkType
{
    Class,
    Method,
    Region,
    LineBlock,
    Config
}
