using System.Security.Cryptography;
using System.Text;
using LocalMcpServer.Configuration;
using LocalMcpServer.LlmConnector;
using LocalMcpServer.ResourceCache;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.McpServer;

/// <summary>
/// 코드 chunk를 embedding 기반으로 검색한다.
/// </summary>
public sealed class VectorSearchEngine
{
    private readonly IResourceCache _cache;
    private readonly CodeChunker _chunker;
    private readonly EmbeddingConnector _embeddingConnector;
    private readonly RagSection _rag;
    private readonly CodeIndexSection _codeIndex;
    private readonly ILogger<VectorSearchEngine> _logger;

    public VectorSearchEngine(
        IResourceCache cache,
        CodeChunker chunker,
        EmbeddingConnector embeddingConnector,
        IOptions<ServerConfig> config,
        ILogger<VectorSearchEngine> logger)
    {
        _cache = cache;
        _chunker = chunker;
        _embeddingConnector = embeddingConnector;
        _rag = config.Value.Rag;
        _codeIndex = config.Value.CodeIndex;
        _logger = logger;
    }

    public async Task<List<CodeChunkScore>> SearchAsync(
        string query,
        int topK = 5,
        float similarityThreshold = 0.5f,
        string? conversationId = null,
        string? solutionHash = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var rootPath = _cache.CurrentIndexRoot;
        if (string.IsNullOrWhiteSpace(rootPath))
            rootPath = _codeIndex.RootPath;

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            _logger.LogWarning("VectorSearch root path를 찾을 수 없습니다.");
            return [];
        }

        var queryEmbedding = await _embeddingConnector.EmbedAsync(query, _rag.EmbeddingModel, ct);
        if (queryEmbedding.Length == 0)
            return [];

        var files = _cache.GetIndexedCodeFiles();
        if (files.Count == 0)
        {
            files = EnumerateCodeFiles(rootPath).ToList();
        }

        var warmChunkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(conversationId) && !string.IsNullOrWhiteSpace(solutionHash))
        {
            var state = await _cache.GetConversationRagStateAsync(conversationId, solutionHash, _rag.EmbeddingModel, ct);
            if (state is not null && state.WarmChunkKeys.Count > 0)
                warmChunkKeys = state.WarmChunkKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var cacheHitCount = 0;
        var generatedEmbeddingCount = 0;
        var scored = new List<(CodeChunk Chunk, float Similarity, bool IsWarm, string ChunkKey)>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(file))
                continue;

            var chunks = _chunker.SplitIntoChunks(
                File.ReadAllText(file),
                file,
                InferLanguage(file),
                maxChunkChars: Math.Max(1200, _rag.MaxContextChars / 2),
                overlapLines: 3);

            foreach (var chunk in chunks)
            {
                var chunkKey = BuildChunkKey(chunk);
                var (embedding, fromCache) = await GetChunkEmbeddingAsync(chunk, chunkKey, ct);
                if (fromCache)
                    cacheHitCount++;
                else
                    generatedEmbeddingCount++;

                if (embedding.Length == 0)
                    continue;

                var similarity = CosineSimilarity(queryEmbedding, embedding);
                if (similarity < similarityThreshold)
                    continue;

                scored.Add((chunk, similarity, warmChunkKeys.Contains(chunkKey), chunkKey));
            }
        }

        var selected = scored
            .OrderByDescending(x => x.Similarity)
            .ThenByDescending(x => x.IsWarm)
            .ThenBy(x => x.Chunk.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(topK)
            .ToList();

        if (!string.IsNullOrWhiteSpace(conversationId) && !string.IsNullOrWhiteSpace(solutionHash))
        {
            foreach (var item in selected)
            {
                await _cache.TouchConversationChunkAsync(conversationId, solutionHash, item.ChunkKey, ct);
            }

            var mergedWarm = selected.Select(s => s.ChunkKey)
                .Concat(warmChunkKeys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            await _cache.UpsertConversationRagStateAsync(new ConversationRagState
            {
                ConversationId = conversationId,
                SolutionHash = solutionHash,
                EmbeddingModel = _rag.EmbeddingModel,
                LastAccessUtc = DateTime.UtcNow,
                WarmChunkKeys = mergedWarm
            }, ct);
        }

        var results = selected
            .Select(x => new CodeChunkScore(x.Chunk, x.Similarity))
            .ToList();

        _logger.LogInformation(
            "VectorSearch 결과: query={Query}, candidates={Count}, topK={TopK}, warmCandidates={WarmCandidateCount}, selectedWarm={SelectedWarmCount}, cacheHits={CacheHitCount}, generatedEmbeddings={GeneratedEmbeddingCount}",
            query,
            scored.Count,
            results.Count,
            warmChunkKeys.Count,
            selected.Count(x => x.IsWarm),
            cacheHitCount,
            generatedEmbeddingCount);
        return results;
    }

    private async Task<(float[] Embedding, bool FromCache)> GetChunkEmbeddingAsync(CodeChunk chunk, string chunkKey, CancellationToken ct)
    {
        var cached = await _cache.GetChunkEmbeddingAsync(chunkKey, ct);
        if (cached is { Length: > 0 })
            return (cached, true);

        var embedding = await _embeddingConnector.EmbedAsync(chunk.Content, _rag.EmbeddingModel, ct);
        if (embedding.Length > 0)
            await _cache.StoreChunkEmbeddingAsync(chunkKey, embedding, ct);

        return (embedding, false);
    }

    private static string BuildChunkKey(CodeChunk chunk)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Content));
        var hashText = Convert.ToHexString(hash);
        return $"{chunk.FilePath}:{chunk.StartLine}-{chunk.EndLine}:{hashText}";
    }

    private IEnumerable<string> EnumerateCodeFiles(string rootPath)
    {
        var patterns = _codeIndex.FilePatterns.Length > 0 ? _codeIndex.FilePatterns : ["*.cs", "*.xaml"];
        foreach (var pattern in patterns)
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories))
            {
                if (IsExcluded(file))
                    continue;

                yield return file;
            }
        }
    }

    private static bool IsExcluded(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".vs", ".git", "node_modules", "packages", "TestResults"
        };

        return parts.Any(excluded.Contains);
    }

    private static string InferLanguage(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".xaml" => "xml",
            ".json" => "json",
            _ => "text"
        };
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0;

        double dot = 0;
        double normA = 0;
        double normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : (float)(dot / denominator);
    }
}

public sealed record CodeChunkScore(CodeChunk Chunk, float Similarity);