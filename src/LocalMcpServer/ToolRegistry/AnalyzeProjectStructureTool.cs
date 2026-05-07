using LocalMcpServer.LlmConnector;
using LocalMcpServer.ResourceCache;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 프로젝트 전체 구조를 분석하는 도구.
/// ResourceCache 코드 인덱스에서 파일/심볼 구조를 수집하고
/// LLM으로 아키텍처 요약을 생성한다.
/// </summary>
public sealed class AnalyzeProjectStructureTool : IMcpTool
{
    private readonly OllamaConnector _llm;
    private readonly PromptTemplateLoader _promptLoader;
    private readonly IResourceCache _cache;

    public AnalyzeProjectStructureTool(
        OllamaConnector llm,
        PromptTemplateLoader promptLoader,
        IResourceCache cache)
    {
        _llm = llm;
        _promptLoader = promptLoader;
        _cache = cache;
    }

    public string Name => "analyze_project_structure";

    public string Description => "프로젝트 전체 구조를 분석합니다 (파일 구성, 모듈, 클래스/인터페이스 관계 등).";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "현재 열린 코드 (참고용, 선택)" },
            language = new { type = "string", description = "프로그래밍 언어 (선택)" }
        }
    };

    public async Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        if (!_cache.IsAvailable)
        {
            return new ToolCallResult
            {
                Content = [new ToolContent { Text = "코드 인덱스가 구성되지 않았습니다. SolutionPath를 전달하거나 appsettings.json의 CodeIndex.RootPath를 설정하세요." }]
            };
        }

        var structureSummary = _cache.GetProjectStructureSummary();

        // LLM 컨텍스트 보호: 구조 요약이 너무 길면 절단
        const int maxStructureLength = 12_000;
        if (structureSummary.Length > maxStructureLength)
            structureSummary = structureSummary[..maxStructureLength] + "\n\n... (이하 생략)";

        var prompt = await _promptLoader.LoadAndRenderAsync(
            Name,
            new Dictionary<string, string>
            {
                ["structure"] = structureSummary,
                ["language"] = GetStringArg(arguments, "language") ?? "C#"
            },
            ct);

        var llmResponse = await _llm.GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            Options = new LlmOptions
            {
                Temperature = 0.3,
                MaxTokens = 4096,
                NumCtx = 16384
            }
        }, ct);

        return new ToolCallResult
        {
            Content = [new ToolContent { Text = llmResponse.Text }]
        };
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.ToString();

        return value.ToString();
    }
}
