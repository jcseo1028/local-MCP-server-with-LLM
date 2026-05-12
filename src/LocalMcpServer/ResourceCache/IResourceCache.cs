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

    /// <summary>
    /// 코드 인덱스에서 프로젝트 전체 파일/심볼 구조를 요약 텍스트로 반환한다.
    /// analyze_project_structure 도구가 사용한다.
    /// </summary>
    string GetProjectStructureSummary();

    /// <summary>
    /// 현재 코드 인덱스 루트 기준으로 색인 대상 파일 경로를 반환한다.
    /// RAG/검색 계층이 chunk 생성을 위해 사용한다.
    /// </summary>
    IReadOnlyList<string> GetIndexedCodeFiles();

    /// <summary>
    /// chunk key에 해당하는 embedding을 조회한다.
    /// </summary>
    Task<float[]?> GetChunkEmbeddingAsync(string chunkKey, CancellationToken ct = default);

    /// <summary>
    /// chunk key에 해당하는 embedding을 저장한다.
    /// </summary>
    Task StoreChunkEmbeddingAsync(string chunkKey, float[] embedding, CancellationToken ct = default);
}
