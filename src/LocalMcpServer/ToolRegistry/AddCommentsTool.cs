using LocalMcpServer.LlmConnector;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 코드에 문서 주석과 인라인 주석을 자동 추가하는 도구.
/// 기존 코드 로직은 수정하지 않고 주석만 추가한다.
/// </summary>
public sealed class AddCommentsTool : CodeToolBase
{
    public AddCommentsTool(OllamaConnector llm, PromptTemplateLoader promptLoader)
        : base(llm, promptLoader) { }

    public override string Name => "add_comments";

    public override string Description => "코드에 문서 주석(XML doc, JSDoc 등)과 인라인 주석을 자동으로 추가합니다.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "주석을 추가할 코드 텍스트" },
            language = new { type = "string", description = "프로그래밍 언어 (선택)" }
        },
        required = new[] { "code" }
    };

    protected override LlmOptions GetLlmOptions() => new()
    {
        Temperature = 0.2,
        MaxTokens = 8192,   // 주석 추가 시 코드 길이가 늘어남
        NumCtx = 16384
    };
}
