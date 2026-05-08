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
        var code = GetStringArg(arguments, "code") ?? "";
        var language = GetStringArg(arguments, "language") ?? "";
        var filesContext = GetStringArg(arguments, "files_context") ?? "";

        // files_context가 있으면 멀티 파일 모드: files_context를 코드 섹션으로 사용
        string codeSection;
        if (!string.IsNullOrWhiteSpace(filesContext))
        {
            codeSection = filesContext;
        }
        else if (!string.IsNullOrEmpty(code))
        {
            codeSection = $"{language} 코드:\n```{language}\n{code}\n```";
        }
        else
        {
            throw new ArgumentException("code 또는 files_context 인자가 필요합니다.");
        }

        var prompt = await _promptLoader.LoadAndRenderAsync(
            Name,
            new Dictionary<string, string>
            {
                ["code_section"] = codeSection
            },
            ct);

        // summaryModel 우선, 없으면 defaultModel (model 선택은 OllamaConnector에 위임)
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
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();

        return value.ToString();
    }
}
