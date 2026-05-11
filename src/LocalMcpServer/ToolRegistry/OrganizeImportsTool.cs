using LocalMcpServer.LlmConnector;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// using/import 구문만 정리하는 도구.
/// 코드 로직·변수명·메서드 시그니처를 변경하지 않는다.
/// contracts.md §7d-2 준수.
/// </summary>
public sealed class OrganizeImportsTool : CodeToolBase
{
    public OrganizeImportsTool(OllamaConnector llm, PromptTemplateLoader promptLoader)
        : base(llm, promptLoader) { }

    public override string Name => "organize_imports";

    public override string Description => "using/import 구문만 정리합니다 (추가·삭제·정렬). 코드 로직은 변경하지 않습니다.";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "정리 대상 코드 텍스트" },
            language = new { type = "string", description = "프로그래밍 언어 (선택)" }
        },
        required = new[] { "code" }
    };

    protected override LlmOptions GetLlmOptions() => new()
    {
        Temperature = 0.1,
        MaxTokens = 4096,
        NumCtx = 8192
    };
}
