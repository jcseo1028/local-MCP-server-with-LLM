namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// MCP 도구 인터페이스. 각 도구는 이 인터페이스를 구현한다.
/// contracts.md §2 ToolCallRequest/Response 준수.
/// </summary>
public interface IMcpTool
{
    /// <summary>도구 고유 이름</summary>
    string Name { get; }

    /// <summary>도구 설명</summary>
    string Description { get; }

    /// <summary>JSON Schema 형식 입력 정의</summary>
    object InputSchema { get; }

    /// <summary>도구 실행</summary>
    Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default);
}

public sealed class ToolCallResult
{
    public required List<ToolContent> Content { get; set; }
}

public sealed class ToolContent
{
    public string Type { get; set; } = "text";
    public required string Text { get; set; }
}
