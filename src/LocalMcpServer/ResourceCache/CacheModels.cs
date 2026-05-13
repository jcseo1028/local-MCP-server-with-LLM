namespace LocalMcpServer.ResourceCache;

// ── contracts.md §4a: 자료 조회 ──

public sealed class CacheLookupRequest
{
    public string Query { get; set; } = "";
    public string? Category { get; set; }
    public int MaxResults { get; set; } = 10;
}

public sealed class CacheLookupResponse
{
    public List<CacheDocument> Results { get; set; } = [];
}

public sealed class CacheDocument
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Source { get; set; } = "";
    public string Category { get; set; } = "";
}

// ── contracts.md §4b: 코드 검색 ──

public sealed class CodeSearchRequest
{
    public string Query { get; set; } = "";
    public string? Scope { get; set; }
    public int MaxResults { get; set; } = 20;
}

public sealed class CodeSearchResponse
{
    public List<CodeSearchResult> Results { get; set; } = [];
}

public sealed class CodeSearchResult
{
    public string FilePath { get; set; } = "";
    public string Symbol { get; set; } = "";
    public int LineNumber { get; set; }
    public string Snippet { get; set; } = "";
}

// ── contracts.md §4b: RAG 세션 동기화 ──

public sealed class ConversationRagState
{
    public string ConversationId { get; set; } = "";
    public string SolutionHash { get; set; } = "";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
    public List<string> WarmChunkKeys { get; set; } = [];
}

public sealed class ConversationChunkUsage
{
    public string ConversationId { get; set; } = "";
    public string SolutionHash { get; set; } = "";
    public string ChunkKey { get; set; } = "";
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public int HitCount { get; set; }
}
