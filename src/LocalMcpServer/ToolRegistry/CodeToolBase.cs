using System.Text.Json;
using LocalMcpServer.LlmConnector;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// code + language 인자를 받아 LLM에 프롬프트를 전달하는 도구의 공통 기반 클래스.
/// 각 도구는 Name, Description, InputSchema만 오버라이드하면 된다.
/// </summary>
public abstract class CodeToolBase : IMcpTool
{
    private readonly OllamaConnector _llm;
    private readonly PromptTemplateLoader _promptLoader;

    protected CodeToolBase(OllamaConnector llm, PromptTemplateLoader promptLoader)
    {
        _llm = llm;
        _promptLoader = promptLoader;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract object InputSchema { get; }

    /// <summary>LLM 옵션 — 서브클래스에서 오버라이드하여 도구별 설정 가능</summary>
    protected virtual LlmOptions GetLlmOptions() => new()
    {
        Temperature = 0.3,
        MaxTokens = 4096,
        NumCtx = 8192
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

        var llmResponse = await _llm.GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            Options = GetLlmOptions()
        }, ct);

        return new ToolCallResult
        {
            Content = [new ToolContent { Text = llmResponse.Text }]
        };
    }

    protected static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement je)
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();

        return value.ToString();
    }
}
