using LocalMcpServer.Configuration;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.ResourceCache;

/// <summary>
/// Resource Cache 모듈 구현 (modules.md §4).
/// 시작 시 로컬 자료를 로드하고 인메모리 인덱스를 구축한다.
/// </summary>
public sealed class ResourceCacheService : IResourceCache
{
    private readonly CacheSection _cacheConfig;
    private readonly CodeIndexSection _codeIndexConfig;
    private readonly ILogger<ResourceCacheService> _logger;

    // 문서 캐시: 카테고리 → 문서 목록
    private readonly Dictionary<string, List<CacheDocument>> _documents = new(StringComparer.OrdinalIgnoreCase);

    // 코드 인덱스: 키워드(소문자) → 코드 검색 결과 목록
    private Dictionary<string, List<CodeSearchResult>> _symbolIndex = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<CodeSearchResult>> _textIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReaderWriterLockSlim _indexLock = new();
    private string? _currentIndexRoot;

    private bool _initialized;

    public ResourceCacheService(IOptions<ServerConfig> config, ILogger<ResourceCacheService> logger)
    {
        _cacheConfig = config.Value.Cache;
        _codeIndexConfig = config.Value.CodeIndex;
        _logger = logger;
    }

    public bool IsAvailable => _initialized;

    public string? CurrentIndexRoot => _currentIndexRoot;

    /// <summary>
    /// 지정 경로로 코드 인덱스를 재구축한다. 기존 인덱스를 원자적으로 교체.
    /// </summary>
    public Task ReindexAsync(string rootPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Task.CompletedTask;

        // SolutionPath는 .sln 파일 경로이므로, 디렉터리로 변환
        var dir = File.Exists(rootPath) ? Path.GetDirectoryName(rootPath)! : rootPath;

        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("ReindexAsync: 경로 없음 — {Dir}", dir);
            return Task.CompletedTask;
        }

        // 이미 같은 루트로 인덱싱된 경우 건너뜀
        if (string.Equals(_currentIndexRoot, dir, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            _logger.LogInformation("코드 인덱스 재구축 시작: {Dir}", dir);

            var newSymbolIndex = new Dictionary<string, List<CodeSearchResult>>(StringComparer.OrdinalIgnoreCase);
            var newTextIndex = new Dictionary<string, List<CodeSearchResult>>(StringComparer.OrdinalIgnoreCase);

            BuildCodeIndexInto(dir, newSymbolIndex, newTextIndex);

            // 원자적 교체 (writer lock)
            _indexLock.EnterWriteLock();
            try
            {
                _symbolIndex = newSymbolIndex;
                _textIndex = newTextIndex;
                _currentIndexRoot = dir;
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }

            _logger.LogInformation("코드 인덱스 재구축 완료: {Dir} ({Symbols}개 심볼, {Tokens}개 텍스트 토큰)",
                dir, newSymbolIndex.Count, newTextIndex.Count);
        }, ct);
    }

    /// <summary>
    /// 캐시 자료 로드 + 코드 인덱스 구축. 서버 시작 시 호출된다.
    /// </summary>
    public void Initialize()
    {
        LoadDocumentCache();
        BuildCodeIndex();
        _initialized = true;
    }

    public Task<CacheLookupResponse> SearchDocumentsAsync(CacheLookupRequest request, CancellationToken ct = default)
    {
        var response = new CacheLookupResponse();
        if (!_initialized || _documents.Count == 0)
            return Task.FromResult(response);

        var keywords = request.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (keywords.Length == 0)
            return Task.FromResult(response);

        IEnumerable<CacheDocument> candidates;
        if (!string.IsNullOrEmpty(request.Category) && _documents.TryGetValue(request.Category, out var catDocs))
            candidates = catDocs;
        else
            candidates = _documents.Values.SelectMany(d => d);

        foreach (var doc in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (keywords.Any(kw => doc.Content.Contains(kw, StringComparison.OrdinalIgnoreCase)
                                || doc.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            {
                response.Results.Add(doc);
                if (response.Results.Count >= request.MaxResults)
                    break;
            }
        }

        return Task.FromResult(response);
    }

    public Task<CodeSearchResponse> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct = default)
    {
        var response = new CodeSearchResponse();
        if (!_initialized)
            return Task.FromResult(response);

        var query = request.Query.Trim();
        if (string.IsNullOrEmpty(query))
            return Task.FromResult(response);

        _indexLock.EnterReadLock();
        try
        {
            // 1. 심볼 인덱스에서 정확 매칭 우선
            if (_symbolIndex.TryGetValue(query, out var symbolHits))
            {
                foreach (var hit in symbolHits)
                {
                    if (MatchesScope(hit.FilePath, request.Scope))
                    {
                        response.Results.Add(hit);
                        if (response.Results.Count >= request.MaxResults)
                            return Task.FromResult(response);
                    }
                }
            }

            // 2. 텍스트 역인덱스에서 키워드 매칭
            var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var kw in keywords)
            {
                ct.ThrowIfCancellationRequested();
                if (!_textIndex.TryGetValue(kw.ToLowerInvariant(), out var textHits))
                    continue;

                foreach (var hit in textHits)
                {
                    if (MatchesScope(hit.FilePath, request.Scope) &&
                        !response.Results.Any(r => r.FilePath == hit.FilePath && r.LineNumber == hit.LineNumber))
                    {
                        response.Results.Add(hit);
                        if (response.Results.Count >= request.MaxResults)
                            return Task.FromResult(response);
                    }
                }
            }
        }
        finally
        {
            _indexLock.ExitReadLock();
        }

        return Task.FromResult(response);
    }

    // ── 문서 캐시 로드 ──

    private void LoadDocumentCache()
    {
        if (string.IsNullOrWhiteSpace(_cacheConfig.Directory))
        {
            _logger.LogDebug("Cache.Directory가 설정되지 않았습니다. 문서 캐시를 건너뜁니다.");
            return;
        }

        if (!Directory.Exists(_cacheConfig.Directory))
        {
            _logger.LogWarning("Cache 디렉터리 없음: {Dir}", _cacheConfig.Directory);
            return;
        }

        var categories = _cacheConfig.Categories.Length > 0
            ? _cacheConfig.Categories
            : new[] { "general" };

        int totalDocs = 0;

        // 카테고리별 하위 폴더가 있으면 분류, 없으면 루트 파일을 general로 분류
        foreach (var category in categories)
        {
            var catDir = Path.Combine(_cacheConfig.Directory, category);

            IEnumerable<string> files;
            if (Directory.Exists(catDir))
            {
                files = Directory.EnumerateFiles(catDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsDocumentFile(f));
            }
            else if (category == categories[0]) // 첫 카테고리일 때 루트 파일 수집
            {
                files = Directory.EnumerateFiles(_cacheConfig.Directory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsDocumentFile(f));
            }
            else
            {
                continue;
            }

            var docs = new List<CacheDocument>();
            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    docs.Add(new CacheDocument
                    {
                        Title = Path.GetFileNameWithoutExtension(file),
                        Content = content,
                        Source = file,
                        Category = category
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "캐시 문서 로드 실패: {File}", file);
                }
            }

            if (docs.Count > 0)
            {
                _documents[category] = docs;
                totalDocs += docs.Count;
            }
        }

        _logger.LogInformation("문서 캐시 로드 완료: {Count}건 ({Categories}개 카테고리)",
            totalDocs, _documents.Count);
    }

    private static bool IsDocumentFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".txt" or ".html" or ".json" or ".xml" or ".yaml" or ".yml";
    }

    // ── 코드 인덱스 구축 ──

    private void BuildCodeIndex()
    {
        if (string.IsNullOrWhiteSpace(_codeIndexConfig.RootPath))
        {
            _logger.LogDebug("CodeIndex.RootPath가 설정되지 않았습니다. 코드 인덱스를 건너뜁니다.");
            return;
        }

        if (!Directory.Exists(_codeIndexConfig.RootPath))
        {
            _logger.LogWarning("CodeIndex 루트 없음: {Dir}", _codeIndexConfig.RootPath);
            return;
        }

        BuildCodeIndexInto(_codeIndexConfig.RootPath, _symbolIndex, _textIndex);
        _currentIndexRoot = _codeIndexConfig.RootPath;
    }

    private void BuildCodeIndexInto(
        string rootPath,
        Dictionary<string, List<CodeSearchResult>> symbolIndex,
        Dictionary<string, List<CodeSearchResult>> textIndex)
    {
        int fileCount = 0;
        int symbolCount = 0;

        foreach (var pattern in _codeIndexConfig.FilePatterns)
        {
            var files = Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories);
            foreach (var file in files)
            {
                // bin, obj, .vs 등 제외
                if (IsExcludedPath(file))
                    continue;

                try
                {
                    var lines = File.ReadAllLines(file);
                    fileCount++;

                    // 심볼 추출 (정규식 기반)
                    symbolCount += ExtractSymbols(file, lines, symbolIndex);

                    // 텍스트 역인덱스 (식별자 토큰 기반)
                    BuildTextIndex(file, lines, textIndex);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "코드 인덱싱 실패: {File}", file);
                }
            }
        }

        _logger.LogInformation("코드 인덱스 구축 완료: {Files}개 파일, {Symbols}개 심볼, {Tokens}개 텍스트 토큰",
            fileCount, symbolCount, textIndex.Count);
    }

    private int ExtractSymbols(string filePath, string[] lines, Dictionary<string, List<CodeSearchResult>> symbolIndex)
    {
        int count = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();

            // C# 패턴: class, interface, struct, enum, record 선언
            var symbol = TryExtractCSharpSymbol(line);
            if (symbol == null)
                // 메서드/함수 패턴
                symbol = TryExtractMethodSymbol(line);

            if (symbol != null)
            {
                var result = new CodeSearchResult
                {
                    FilePath = filePath,
                    Symbol = symbol,
                    LineNumber = i + 1,
                    Snippet = GetSnippet(lines, i, 3)
                };

                if (!symbolIndex.TryGetValue(symbol, out var list))
                {
                    list = [];
                    symbolIndex[symbol] = list;
                }
                list.Add(result);
                count++;
            }
        }
        return count;
    }

    private static string? TryExtractCSharpSymbol(string line)
    {
        // public class Foo / internal sealed class Foo<T> / public interface IFoo 등
        var keywords = new[] { "class ", "interface ", "struct ", "enum ", "record " };
        foreach (var kw in keywords)
        {
            int idx = line.IndexOf(kw, StringComparison.Ordinal);
            if (idx < 0) continue;

            // 앞에 public/private/internal/protected/static/sealed/abstract/partial 등이 올 수 있음
            var prefix = line[..idx].Trim();
            if (prefix.Length > 0 && !IsAccessModifier(prefix))
                continue;

            var afterKw = line[(idx + kw.Length)..].TrimStart();
            var name = ExtractIdentifier(afterKw);
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        return null;
    }

    private static string? TryExtractMethodSymbol(string line)
    {
        // 메서드 패턴: 접근제한자 반환형 이름( 형태
        // public void Foo(, public async Task<int> Bar(
        int parenIdx = line.IndexOf('(');
        if (parenIdx < 2) return null;

        // 괄호 앞의 공백이 아닌 마지막 토큰이 메서드 이름
        var beforeParen = line[..parenIdx].TrimEnd();
        int lastSpace = beforeParen.LastIndexOf(' ');
        if (lastSpace < 0) return null;

        var candidate = beforeParen[(lastSpace + 1)..];
        // 연산자 오버로드, 생성자 등 필터
        if (candidate.Length < 2 || !char.IsLetter(candidate[0]))
            return null;
        // if, for, while 등 제어문 제외
        if (candidate is "if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "lock" or "return" or "new" or "typeof" or "nameof" or "sizeof")
            return null;

        return candidate;
    }

    private static bool IsAccessModifier(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mods = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "internal", "protected",
            "static", "sealed", "abstract", "partial",
            "readonly", "virtual", "override", "async", "new", "unsafe"
        };
        return parts.All(p => mods.Contains(p));
    }

    private static string ExtractIdentifier(string text)
    {
        int end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;
        return text[..end];
    }

    private void BuildTextIndex(string filePath, string[] lines, Dictionary<string, List<CodeSearchResult>> textIndex)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var tokens = TokenizeLine(lines[i]);
            foreach (var token in tokens)
            {
                var key = token.ToLowerInvariant();
                if (key.Length < 3) continue; // 짧은 토큰 제외

                if (!textIndex.TryGetValue(key, out var list))
                {
                    list = [];
                    textIndex[key] = list;

                    // 메모리 제한: 토큰당 최대 20개 결과
                }

                if (list.Count < 20)
                {
                    // 같은 파일+줄 중복 방지
                    if (!list.Any(r => r.FilePath == filePath && r.LineNumber == i + 1))
                    {
                        list.Add(new CodeSearchResult
                        {
                            FilePath = filePath,
                            Symbol = token,
                            LineNumber = i + 1,
                            Snippet = GetSnippet(lines, i, 2)
                        });
                    }
                }
            }
        }
    }

    private static IEnumerable<string> TokenizeLine(string line)
    {
        // 식별자 토큰 추출 (CamelCase 분리 없이 원본 토큰)
        int start = -1;
        for (int i = 0; i <= line.Length; i++)
        {
            bool isIdChar = i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
            if (isIdChar && start < 0)
                start = i;
            else if (!isIdChar && start >= 0)
            {
                var token = line[start..i];
                if (token.Length >= 3 && !char.IsDigit(token[0]))
                    yield return token;
                start = -1;
            }
        }
    }

    private static string GetSnippet(string[] lines, int centerLine, int radius)
    {
        int start = Math.Max(0, centerLine - radius);
        int end = Math.Min(lines.Length - 1, centerLine + radius);
        return string.Join('\n', lines[start..(end + 1)]);
    }

    private static bool IsExcludedPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/');
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".vs", ".git", "node_modules", "packages", "TestResults"
        };
        return segments.Any(s => excluded.Contains(s));
    }

    private static bool MatchesScope(string filePath, string? scope)
    {
        if (string.IsNullOrEmpty(scope))
            return true;

        // 단순 경로 포함 검사
        return filePath.Replace('\\', '/').Contains(scope.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
