namespace LocalMcpServer.LlmConnector;

/// <summary>
/// contracts.md §3 LLMRequest 준수.
/// </summary>
public sealed class LlmRequest
{
    public required string Prompt { get; set; }
    public object? Context { get; set; }
    public string? Model { get; set; }
    public LlmOptions Options { get; set; } = new();
}

public sealed class LlmOptions
{
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    /// <summary>Ollama num_ctx — 컨텍스트 윈도우 크기. 작을수록 빠름.</summary>
    public int NumCtx { get; set; } = 16384;
}

/// <summary>
/// contracts.md §3 LLMResponse 준수.
/// </summary>
public sealed class LlmResponse
{
    public required string Text { get; set; }
    public LlmUsage? Usage { get; set; }
}

public sealed class LlmUsage
{
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
}
