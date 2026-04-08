namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 프롬프트 템플릿 로더.
/// Config.tools.promptsDirectory에서 {toolName}.prompt.md 파일을 로드하고 변수를 치환한다.
/// </summary>
public sealed class PromptTemplateLoader
{
    private readonly string _promptsDirectory;
    private readonly ILogger<PromptTemplateLoader> _logger;

    public PromptTemplateLoader(string promptsDirectory, ILogger<PromptTemplateLoader> logger)
    {
        _promptsDirectory = promptsDirectory;
        _logger = logger;
    }

    /// <summary>
    /// 지정된 도구의 프롬프트 템플릿을 로드하고 변수를 치환하여 반환한다.
    /// </summary>
    public async Task<string> LoadAndRenderAsync(string toolName, Dictionary<string, string> variables, CancellationToken ct = default)
    {
        var filePath = Path.Combine(_promptsDirectory, $"{toolName}.prompt.md");

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("프롬프트 템플릿 없음: {Path}. 기본 프롬프트 사용.", filePath);
            return BuildFallbackPrompt(toolName, variables);
        }

        var template = await File.ReadAllTextAsync(filePath, ct);

        foreach (var (key, value) in variables)
        {
            template = template.Replace($"{{{{{key}}}}}", value);
        }

        _logger.LogDebug("프롬프트 렌더링 완료: {Tool}, 길이={Length}", toolName, template.Length);
        return template;
    }

    private static string BuildFallbackPrompt(string toolName, Dictionary<string, string> variables)
    {
        return toolName switch
        {
            "summarize_current_code" =>
                $"다음 코드를 간결하게 요약해주세요.\n\n```{variables.GetValueOrDefault("language", "")}\n{variables.GetValueOrDefault("code", "")}\n```",
            _ => string.Join("\n", variables.Select(kv => $"{kv.Key}: {kv.Value}"))
        };
    }
}
