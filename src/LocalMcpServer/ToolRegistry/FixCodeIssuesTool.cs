using LocalMcpServer.LlmConnector;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 코드에서 버그, 안티패턴, 보안 취약점을 찾아 수정하는 도구.
/// 발견된 이슈 목록과 수정된 전체 코드를 반환한다.
/// </summary>
public sealed class FixCodeIssuesTool : CodeToolBase
{
    public FixCodeIssuesTool(OllamaConnector llm, PromptTemplateLoader promptLoader)
        : base(llm, promptLoader) { }

    public override string Name => "fix_code_issues";

    public override string Description => "코드에서 버그, 안티패턴, 보안 취약점을 찾아 수정합니다.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "검사할 코드 텍스트" },
            language = new { type = "string", description = "프로그래밍 언어 (선택)" }
        },
        required = new[] { "code" }
    };

    protected override LlmOptions GetLlmOptions() => new()
    {
        Temperature = 0.2,
        MaxTokens = 8192,   // 수정된 전체 코드 출력
        NumCtx = 16384
    };
}
