using System.Text.Json;
using LocalMcpServer.ResourceCache;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 프로젝트 내 코드 검색 도구 (contracts.md §7b).
/// Resource Cache 코드 인덱스를 사용한다. LLM 호출 불필요.
/// </summary>
public sealed class SearchProjectCodeTool : IMcpTool
{
    private readonly IResourceCache _cache;

    public SearchProjectCodeTool(IResourceCache cache)
    {
        _cache = cache;
    }

    public string Name => "search_project_code";

    public string Description => "프로젝트 내 코드를 검색합니다 (클래스, 메서드, 키워드 등).";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "검색 키워드 (클래스명, 함수명, 식별자 등)" },
            scope = new { type = "string", description = "검색 범위 (파일 경로 패턴, 비워두면 전체 프로젝트)" },
            maxResults = new { type = "integer", description = "최대 반환 건수 (기본: 20)" }
        },
        required = new[] { "query" }
    };

    public async Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var query = GetStringArg(arguments, "query")
            ?? throw new ArgumentException("query 인자가 필요합니다.");
        var scope = GetStringArg(arguments, "scope");
        var maxResults = GetIntArg(arguments, "maxResults") ?? 20;

        if (!_cache.IsAvailable)
        {
            return new ToolCallResult
            {
                Content = [new ToolContent { Text = "코드 인덱스가 구성되지 않았습니다. appsettings.json의 CodeIndex.RootPath를 설정하세요." }]
            };
        }

        var response = await _cache.SearchCodeAsync(new CodeSearchRequest
        {
            Query = query,
            Scope = scope,
            MaxResults = maxResults
        }, ct);

        if (response.Results.Count == 0)
        {
            return new ToolCallResult
            {
                Content = [new ToolContent { Text = $"'{query}' 검색 결과가 없습니다." }]
            };
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### 검색 결과: '{query}' ({response.Results.Count}건)");
        sb.AppendLine();

        foreach (var r in response.Results)
        {
            sb.AppendLine($"**{r.Symbol}** — `{r.FilePath}:{r.LineNumber}`");
            sb.AppendLine("```");
            sb.AppendLine(r.Snippet);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return new ToolCallResult
        {
            Content = [new ToolContent { Text = sb.ToString() }]
        };
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return value.ToString();
    }

    private static int? GetIntArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.GetInt32();
        if (int.TryParse(value.ToString(), out var i)) return i;
        return null;
    }
}
