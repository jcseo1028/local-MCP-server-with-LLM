using LocalMcpServer.LlmConnector;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 코드를 리팩터링하는 도구.
/// 가독성 향상, 중복 제거, 구조 개선, 현대적 문법 적용을 수행한다.
/// 기존 동작은 보존한다.
/// </summary>
public sealed class RefactorCurrentCodeTool : CodeToolBase
{
    public RefactorCurrentCodeTool(OllamaConnector llm, PromptTemplateLoader promptLoader)
        : base(llm, promptLoader) { }

    public override string Name => "refactor_current_code";

    public override string Description => "코드를 리팩터링합니다 (가독성 향상, 중복 제거, 구조 개선, 현대적 문법 적용).";

    public override object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "리팩터링할 코드 텍스트" },
            language = new { type = "string", description = "프로그래밍 언어 (선택)" }
        },
        required = new[] { "code" }
    };

    protected override LlmOptions GetLlmOptions() => new()
    {
        Temperature = 0.3,
        MaxTokens = 2048,   // 리팩터링 결과 + 변경 요약
        NumCtx = 4096
    };
}
