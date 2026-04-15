using System.Collections.Concurrent;
using LocalMcpServer.Configuration;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.McpServer;

/// <summary>
/// 대화 상태를 메모리 내에서 관리한다.
/// contracts.md §9 conversationId 기반 세션 관리.
/// 향후 SQLite 등 경량 DB로 마이그레이션 가능하도록 인터페이스를 분리한다.
/// </summary>
public interface IConversationStore
{
    ConversationState GetOrCreate(string? conversationId);
    ConversationState? Get(string conversationId);
    void CleanupExpired();
}

public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();
    private readonly int _timeoutMinutes;
    private readonly int _maxHistory;

    public InMemoryConversationStore(IOptions<ServerConfig> config)
    {
        _timeoutMinutes = config.Value.Chat.ConversationTimeoutMinutes;
        _maxHistory = config.Value.Chat.MaxConversationHistory;
    }

    public ConversationState GetOrCreate(string? conversationId)
    {
        if (!string.IsNullOrEmpty(conversationId) && _conversations.TryGetValue(conversationId, out var existing))
        {
            existing.LastAccess = DateTime.UtcNow;
            return existing;
        }

        var state = new ConversationState
        {
            ConversationId = Guid.NewGuid().ToString("N"),
            MaxHistory = _maxHistory
        };
        _conversations[state.ConversationId] = state;
        return state;
    }

    public ConversationState? Get(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var state))
        {
            state.LastAccess = DateTime.UtcNow;
            return state;
        }
        return null;
    }

    public void CleanupExpired()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_timeoutMinutes);
        foreach (var kvp in _conversations)
        {
            if (kvp.Value.LastAccess < cutoff)
                _conversations.TryRemove(kvp.Key, out _);
        }
    }
}

public sealed class ConversationState
{
    public string ConversationId { get; set; } = "";
    public DateTime LastAccess { get; set; } = DateTime.UtcNow;
    public int MaxHistory { get; set; } = 20;

    /// <summary>대화 메시지 이력 (role, content)</summary>
    public List<ChatHistoryEntry> History { get; } = new();

    /// <summary>마지막 코드 변경 제안 (승인 대기 중)</summary>
    public PendingCodeChange? PendingChange { get; set; }

    public void AddMessage(string role, string content)
    {
        History.Add(new ChatHistoryEntry { Role = role, Content = content });

        // 최대 이력 수 초과 시 오래된 항목 제거 (시스템 메시지 제외)
        while (History.Count > MaxHistory)
            History.RemoveAt(0);
    }

    public string FormatHistoryForPrompt()
    {
        if (History.Count == 0) return "(없음)";

        var lines = new List<string>();
        foreach (var entry in History.TakeLast(10)) // LLM 컨텍스트에는 최근 10개만
        {
            var roleLabel = entry.Role == "user" ? "사용자" : "어시스턴트";
            lines.Add($"{roleLabel}: {entry.Content}");
        }
        return string.Join("\n", lines);
    }
}

public sealed class ChatHistoryEntry
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class PendingCodeChange
{
    public string Original { get; set; } = "";
    public string Modified { get; set; } = "";
    public string ToolName { get; set; } = "";
}
