using System.Text;
using System.Text.Json;
using LocalMcpServer.LlmConnector;
using LocalMcpServer.ResourceCache;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 에러 로그 기반 수정 제안 도구 (contracts.md §7f).
/// LLM에 에러 로그를 분석시키고, Resource Cache에서 관련 문서를 선택적으로 조회한다.
/// </summary>
public sealed class SuggestFixFromErrorLogTool : IMcpTool
{
    private readonly OllamaConnector _llm;
    private readonly PromptTemplateLoader _promptLoader;
    private readonly IResourceCache _cache;

    public SuggestFixFromErrorLogTool(OllamaConnector llm, PromptTemplateLoader promptLoader, IResourceCache cache)
    {
        _llm = llm;
        _promptLoader = promptLoader;
        _cache = cache;
    }

    public string Name => "suggest_fix_from_error_log";

    public string Description => "에러 로그 또는 예외 메시지를 분석하여 수정 방향을 제안합니다.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            errorLog = new { type = "string", description = "에러 로그 또는 예외 메시지 텍스트" },
            codeContext = new { type = "string", description = "관련 코드 컨텍스트 (선택)" }
        },
        required = new[] { "errorLog" }
    };

    public async Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var errorLog = GetStringArg(arguments, "errorLog")
            ?? throw new ArgumentException("errorLog 인자가 필요합니다.");
        var codeContext = GetStringArg(arguments, "codeContext") ?? "";

        // Resource Cache에서 관련 문서 검색 (선택적)
        string references = "";
        if (_cache.IsAvailable)
        {
            try
            {
                var cacheResult = await _cache.SearchDocumentsAsync(
                    new CacheLookupRequest { Query = errorLog[..Math.Min(errorLog.Length, 200)], MaxResults = 3 }, ct);

                if (cacheResult.Results.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var doc in cacheResult.Results)
                        sb.AppendLine($"- {doc.Title} ({doc.Source}): {doc.Content[..Math.Min(doc.Content.Length, 200)]}");
                    references = sb.ToString();
                }
            }
            catch { /* 캐시 검색 실패는 무시 */ }
        }

        var prompt = await _promptLoader.LoadAndRenderAsync(
            Name,
            new Dictionary<string, string>
            {
                ["errorLog"] = errorLog,
                ["codeContext"] = codeContext,
                ["references"] = references
            },
            ct);

        var llmResponse = await _llm.GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            Options = new LlmOptions
            {
                Temperature = 0.3,
                MaxTokens = 1024,
                NumCtx = 4096
            }
        }, ct);

        return new ToolCallResult
        {
            Content = [new ToolContent { Text = llmResponse.Text }]
        };
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        return value.ToString();
    }
}
