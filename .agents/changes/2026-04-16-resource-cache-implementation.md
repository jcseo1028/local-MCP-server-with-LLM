# Resource Cache 구현

날짜: 2026-04-16

## 변경 목적

modules.md §4 Resource Cache 모듈의 전체 구현. 현장 자료 조회(CacheLookup)와 프로젝트 코드 인덱스(CodeSearch) 기능 제공.

## 변경 내역

### Phase 1: Config + 인터페이스

- `Configuration/ServerConfig.cs`: `CacheSection`(Directory, Categories) + `CodeIndexSection`(RootPath, FilePatterns, Strategy) 추가
- `appsettings.json`: Cache, CodeIndex 섹션 기본값 추가
- `ResourceCache/IResourceCache.cs`: `SearchDocumentsAsync`, `SearchCodeAsync`, `IsAvailable` 인터페이스
- `ResourceCache/CacheModels.cs`: contracts.md §4a/§4b 준수 DTO — CacheLookupRequest/Response, CodeSearchRequest/Response
- `ResourceCache/ResourceCacheService.cs`: 전체 구현 (문서 캐시 + 심볼 추출 + 텍스트 역인덱스)
- `Program.cs`: DI 등록 + 시작 시 Initialize 호출

### Phase 2: DocumentSearcher 통합

- `McpServer/DocumentSearcher.cs`: `IResourceCache` 의존성 주입, Resource Cache 활성화 시 캐시 문서 병합 검색

### Phase 3: 코드 인덱스

- `ResourceCacheService.cs` 내에 구현:
  - `ExtractSymbols`: 정규식 기반 C# 클래스/인터페이스/구조체/열거형/레코드/메서드 심볼 추출
  - `BuildTextIndex`: 식별자 토큰 기반 역인덱스 (토큰당 최대 20건, 3자 이상)
  - `SearchCodeAsync`: 심볼 정확 매칭 우선 → 텍스트 역인덱스 폴백

### Phase 4: 도구 연결

- `ToolRegistry/SearchProjectCodeTool.cs`: search_project_code 도구 — Resource Cache CodeSearch 직접 호출 (LLM 불필요)
- `ToolRegistry/SuggestFixFromErrorLogTool.cs`: suggest_fix_from_error_log 도구 — LLM + 선택적 Resource Cache CacheLookup
- `prompts/suggest_fix_from_error_log.prompt.md`: 프롬프트 템플릿
- `Program.cs`: 2개 도구 DI 등록 + ToolRegistry 등록

## 문서 갱신

- `README.md`: 상태 업데이트 (6개 도구, Resource Cache 구현 완료), 프로젝트 구조 4개 파일 추가, 도구 테이블 갱신
- `.agents/modules.md`: Module 4 Resource Cache 구현 상태 갱신

## 설정 방법

```json
{
  "Cache": {
    "Directory": "/path/to/cache-docs",
    "Categories": ["standards", "api_references", "snippets", "best_practices"]
  },
  "CodeIndex": {
    "RootPath": "/path/to/project/src",
    "FilePatterns": ["*.cs", "*.xaml"],
    "Strategy": "hybrid"
  }
}
```

- `Cache.Directory`가 비어있으면 문서 캐시를 건너뜀
- `CodeIndex.RootPath`가 비어있으면 코드 인덱스를 건너뜀 (VSIX SolutionPath로 동적 인덱싱 가능)
- 카테고리별 하위 폴더 구조 지원 (standards/, api_references/ 등)

## 혼합형 CodeIndex 자동 참조 (추가 구현)

VSIX에서 열린 프로젝트의 SolutionPath를 서버에 전송하면, 해당 경로를 코드 인덱스 루트로 자동 사용한다.
`CodeIndex.RootPath` 설정은 CLI/서버 단독 실행 시 폴백으로 유지된다.

### 변경 파일

- `ResourceCache/IResourceCache.cs`: `CurrentIndexRoot`, `ReindexAsync(string rootPath)` 추가
- `ResourceCache/ResourceCacheService.cs`:
  - `ReindexAsync`: SolutionPath → 디렉터리 변환, 새 인덱스 구축 후 `ReaderWriterLockSlim`으로 원자적 교체
  - `SearchCodeAsync`: reader lock 적용으로 재인덱싱 중 검색 안전성 확보
  - `BuildCodeIndexInto()`: 인덱스 딕셔너리를 파라미터로 받는 공통 메서드로 리팩터
- `McpServer/RunOrchestrator.cs`: `IResourceCache` 의존성 추가, `StartRun` 시 SolutionPath가 있으면 `ReindexAsync` 호출
- `LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs`: `RunStartRequest` 생성 시 `SolutionPath = await GetSolutionPathAsync()` 할당

### 동작 흐름

1. VSIX가 Run 시작 → `SolutionPath` (예: `D:\MyProject\MyApp.sln`) 포함하여 서버에 POST
2. `RunOrchestrator.StartRun()` → `IResourceCache.ReindexAsync(solutionPath)` 호출
3. `ReindexAsync`가 .sln 경로를 디렉터리로 변환, 이미 같은 루트면 스킵
4. 새 인덱스를 백그라운드에서 구축 → writer lock으로 원자적 교체
5. 이후 `SearchCodeAsync` 호출 시 새 인덱스에서 검색
