using System.Text.Json;
using LocalMcpServer.Configuration;
using LocalMcpServer.LlmConnector;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// code + language 인자를 받아 LLM에 프롬프트를 전달하는 도구의 공통 기반 클래스.
/// 각 도구는 Name, Description, InputSchema만 오버라이드하면 된다.
/// </summary>
public abstract class CodeToolBase : IMcpTool
{
    private readonly OllamaConnector _llm;
    private readonly PromptTemplateLoader _promptLoader;
    private readonly ServerConfig _config;

    protected CodeToolBase(OllamaConnector llm, PromptTemplateLoader promptLoader, IOptions<ServerConfig> config)
    {
        _llm = llm;
        _promptLoader = promptLoader;
        _config = config.Value;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract object InputSchema { get; }

    /// <summary>LLM 옵션 — 서브클래스에서 오버라이드하여 도구별 설정 가능</summary>
    protected virtual LlmOptions GetLlmOptions() => new()
    {
        Temperature = 0.3,
        MaxTokens = 8192,
        NumCtx = 16384
    };

    /// <summary>코드 수정 도구는 기본적으로 코드 모델(DefaultModel)로 실행한다.</summary>
    protected virtual string ResolveToolModel(Dictionary<string, object?> arguments)
    {
        var overrideModel = GetStringArg(arguments, "model");
        if (!string.IsNullOrWhiteSpace(overrideModel))
            return overrideModel;

        return _config.Llm.DefaultModel;
    }

    public async Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var code = GetStringArg(arguments, "code")
            ?? throw new ArgumentException("code 인자가 필요합니다.");

        var language = GetStringArg(arguments, "language") ?? "";

        var filesContext = GetStringArg(arguments, "files_context") ?? "";
        var relatedFilesContext = GetStringArg(arguments, "related_files_context") ?? "";
        var isMultiFile = !string.IsNullOrWhiteSpace(filesContext);
        var singleCodeSection = isMultiFile
            ? ""
            : $"{language} 코드:\n```{language}\n{code}\n```";

        var prompt = await _promptLoader.LoadAndRenderAsync(
            Name,
            new Dictionary<string, string>
            {
                ["code"] = code,
                ["language"] = language,
                ["files_context"] = filesContext,
                ["related_files_context"] = relatedFilesContext,
                ["single_code_section"] = singleCodeSection
            },
            ct);

        var llmResponse = await _llm.GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            Model = ResolveToolModel(arguments),
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
