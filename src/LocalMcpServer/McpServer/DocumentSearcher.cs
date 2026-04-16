using LocalMcpServer.Configuration;
using LocalMcpServer.ResourceCache;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.McpServer;

/// <summary>
/// 로컬 파일 시스템에서 문서를 검색한다.
/// contracts.md §5 DocumentSearch 설정 기반, 오프라인 전용.
/// Resource Cache가 활성화된 경우 캐시 문서도 함께 검색한다.
/// </summary>
public sealed class DocumentSearcher
{
    private readonly DocumentSearchSection _config;
    private readonly IResourceCache _cache;
    private readonly ILogger<DocumentSearcher> _logger;

    public DocumentSearcher(IOptions<ServerConfig> config, IResourceCache cache, ILogger<DocumentSearcher> logger)
    {
        _config = config.Value.DocumentSearch;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// 쿼리(키워드)를 기준으로 설정된 디렉터리에서 문서를 검색한다.
    /// </summary>
    public async Task<List<DocumentReference>> SearchAsync(string query, CancellationToken ct)
    {
        var results = new List<DocumentReference>();

        if (_config.Directories.Length == 0)
        {
            _logger.LogDebug("DocumentSearch 디렉터리가 설정되지 않았습니다.");
            return results;
        }

        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keywords.Length == 0)
            return results;

        foreach (var dir in _config.Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("DocumentSearch 디렉터리 없음: {Dir}", dir);
                continue;
            }

            foreach (var pattern in _config.FilePatterns)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var files = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();

                        var content = File.ReadAllText(file);
                        if (!keywords.Any(kw => content.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var excerpt = ExtractExcerpt(content, keywords, maxLength: 300);
                        var title = Path.GetFileNameWithoutExtension(file);

                        results.Add(new DocumentReference
                        {
                            Title = title,
                            Source = file,
                            Excerpt = excerpt
                        });

                        if (results.Count >= 10)
                            return results;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "파일 검색 오류: {Dir}/{Pattern}", dir, pattern);
                }
            }
        }

        _logger.LogInformation("DocumentSearch 결과: {Count}건 (query={Query})", results.Count, query);

        // Resource Cache 문서도 함께 검색 (활성화된 경우)
        if (_cache.IsAvailable && results.Count < 10)
        {
            try
            {
                var cacheResult = await _cache.SearchDocumentsAsync(
                    new CacheLookupRequest { Query = query, MaxResults = 10 - results.Count }, ct);

                foreach (var doc in cacheResult.Results)
                {
                    // 이미 동일 소스가 있으면 건너뜀
                    if (results.Any(r => r.Source == doc.Source))
                        continue;

                    results.Add(new DocumentReference
                    {
                        Title = doc.Title,
                        Source = doc.Source,
                        Excerpt = doc.Content.Length <= 300 ? doc.Content : doc.Content[..300] + "..."
                    });

                    if (results.Count >= 10)
                        break;
                }

                if (cacheResult.Results.Count > 0)
                    _logger.LogDebug("Resource Cache 추가 결과: {Count}건", cacheResult.Results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resource Cache 문서 검색 실패");
            }
        }

        return results;
    }

    private static string ExtractExcerpt(string content, string[] keywords, int maxLength)
    {
        // 첫 번째 키워드 매치 위치를 찾아 주변 텍스트를 추출
        var bestIndex = -1;
        foreach (var kw in keywords)
        {
            var idx = content.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (bestIndex < 0 || idx < bestIndex))
                bestIndex = idx;
        }

        if (bestIndex < 0)
            return content.Length <= maxLength ? content : content[..maxLength] + "...";

        var start = Math.Max(0, bestIndex - maxLength / 3);
        var end = Math.Min(content.Length, start + maxLength);
        var excerpt = content[start..end].Trim();
        if (start > 0) excerpt = "..." + excerpt;
        if (end < content.Length) excerpt += "...";
        return excerpt;
    }
}
