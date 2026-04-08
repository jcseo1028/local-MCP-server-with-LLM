namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// contracts.md §2 Tool Registry 구현.
/// 도구를 등록하고 이름으로 조회·실행한다.
/// </summary>
public sealed class ToolRegistryService
{
    private readonly Dictionary<string, IMcpTool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IMcpTool tool)
    {
        _tools[tool.Name] = tool;
    }

    public IReadOnlyList<IMcpTool> ListTools() => _tools.Values.ToList().AsReadOnly();

    public IMcpTool? GetTool(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }
}
