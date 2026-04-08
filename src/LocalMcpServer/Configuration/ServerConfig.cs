namespace LocalMcpServer.Configuration;

/// <summary>
/// 전체 서버 설정 (contracts.md §5 Config 스키마 준수).
/// appsettings.json 의 루트 구조와 1:1 매핑한다.
/// 최소 기능(summarize_current_code)에 필요한 섹션만 우선 구현한다.
/// </summary>
public sealed class ServerConfig
{
    public ServerSection Server { get; set; } = new();
    public LlmSection Llm { get; set; } = new();
    public ToolsSection Tools { get; set; } = new();
}

public sealed class ServerSection
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5100;
    public string Transport { get; set; } = "sse";
}

public sealed class LlmSection
{
    public string Provider { get; set; } = "ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string DefaultModel { get; set; } = "qwen2.5-coder:7b";
    public string? SummaryModel { get; set; }
}

public sealed class ToolsSection
{
    public string Directory { get; set; } = "tools";
    public string PromptsDirectory { get; set; } = "prompts";
}
