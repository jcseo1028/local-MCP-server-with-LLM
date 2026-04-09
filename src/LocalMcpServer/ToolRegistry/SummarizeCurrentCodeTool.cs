using System.Text.Json;
using LocalMcpServer.Configuration;
using LocalMcpServer.LlmConnector;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// contracts.md §7a summarize_current_code 구현.
/// 현재 파일 또는 선택 영역의 코드를 요약한다.
/// 사용 모듈: LLM Connector (summaryModel 우선, 없으면 defaultModel)
/// </summary>
public sealed class SummarizeCurrentCodeTool : IMcpTool
{
    private readonly OllamaConnector _llm;
    private readonly PromptTemplateLoader _promptLoader;
    private readonly ServerConfig _config;

    public SummarizeCurrentCodeTool(OllamaConnector llm, PromptTemplateLoader promptLoader, IOptions<ServerConfig> config)
    {
        _llm = llm;
        _promptLoader = promptLoader;
        _config = config.Value;
    }

    public string Name => "summarize_current_code";

    public string Description => "현재 파일 또는 선택 영역의 코드를 요약합니다.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "요약 대상 코드 텍스트" },
            language = new { type = "string", description = "프로그래밍 언어 (선택, null이면 자동 감지)" }
        },
        required = new[] { "code" }
    };

    public async Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var code = GetStringArg(arguments, "code")
            ?? throw new ArgumentException("code 인자가 필요합니다.");

        var language = GetStringArg(arguments, "language") ?? "";

        var prompt = await _promptLoader.LoadAndRenderAsync(
            Name,
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["language"] = language
            },
            ct);

        // summaryModel 우선, 없으면 defaultModel (model 선택은 OllamaConnector에 위임)
        var llmResponse = await _llm.GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            Options = new LlmOptions
            {
                Temperature = 0.3,
                MaxTokens = 512,
                NumCtx = 2048
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

        if (value is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();

        return value.ToString();
    }
}
