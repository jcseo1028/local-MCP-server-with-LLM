namespace LocalMcpServer.ResourceCache;

/// <summary>
/// Resource Cache 모듈 인터페이스 (contracts.md §4).
/// 현장 자료 조회 + 프로젝트 코드 인덱스 검색을 제공한다.
/// </summary>
public interface IResourceCache
{
    /// <summary>
    /// 캐시된 현장 자료에서 키워드 검색을 수행한다 (contracts.md §4a).
    /// </summary>
    Task<CacheLookupResponse> SearchDocumentsAsync(CacheLookupRequest request, CancellationToken ct = default);

    /// <summary>
    /// 프로젝트 코드 인덱스에서 심볼/키워드 검색을 수행한다 (contracts.md §4b).
    /// </summary>
    Task<CodeSearchResponse> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// 캐시가 사용 가능한 상태인지 확인한다.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 현재 코드 인덱스의 루트 경로. null이면 인덱스 미구축.
    /// </summary>
    string? CurrentIndexRoot { get; }

    /// <summary>
    /// 지정된 루트 경로로 코드 인덱스를 동적으로 재구축한다.
    /// VSIX에서 SolutionPath를 수신했을 때 호출된다.
    /// 기존 인덱스를 교체하며 스레드 안전하다.
    /// </summary>
    Task ReindexAsync(string rootPath, CancellationToken ct = default);
}
