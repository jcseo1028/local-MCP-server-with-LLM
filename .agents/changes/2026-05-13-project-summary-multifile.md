# Specification: 프로젝트 전체 구조 요약 기능 개선 (v2.7.x+)

**결정 날짜**: 2026-05-13  
**담당**: RunOrchestrator, VectorSearchEngine, ResourceCache, IntentResolver  
**상태**: 설계/스펙 확정 (구현 미시작)

---

## 1. 배경 및 문제점

- 기존: "프로젝트 전체 구조 요약" 요청 시 실제로는 단일 파일만 요약 대상으로 처리됨
- 원인: RunOrchestrator/VectorSearchEngine가 멀티파일 컨텍스트 수집 및 조립을 지원하지 않음
- 결과: 사용자가 기대하는 "전체 프로젝트 요약"이 아닌, 특정 파일 요약만 반환됨

---

## 2. 개선 목표

- analyze_project_structure 등 "프로젝트 전체" 의도일 때
  - 전체 주요 코드 파일(.cs, .json, .md 등)에서 의미 단위(Chunk) 추출
  - 여러 파일의 Chunk를 한 번에 RAG Context로 조립
  - LLM에 "프로젝트 전체 구조"를 요약할 수 있는 충분한 컨텍스트 제공

---


## 3. 상세 설계

### 3.1 의도 분석 및 도구 선택
- IntentResolver에서 "프로젝트 전체 구조 분석" 의도(tool=analyze_project_structure) 감지 시
  - RunOrchestrator에 멀티파일 컨텍스트 수집 플래그 전달

### 3.2 멀티파일 Chunk 수집
- ResourceCache/VectorSearchEngine에서
  - 프로젝트 내 주요 코드 파일(.cs, .json, .md 등) 전체 순회
  - 각 파일별로 대표 Chunk(클래스, region, 주요 method 등) 추출
  - (파일 수가 많을 경우 Top-N 파일/Chunk만 우선 추출, 설정값으로 제한)

### 3.3 RAG Context 조립
- BuildRagContext()에서
  - 여러 파일의 Chunk를 파일별/섹션별로 정리
  - 예시: 
    ```
    === [파일: src/LocalMcpServer/RunOrchestrator.cs] ===
    [Class] RunOrchestrator ...
    ...
    === [파일: src/LocalMcpServer/ResourceCache/ResourceCacheService.cs] ===
    [Class] ResourceCacheService ...
    ...
    ```
  - LLM에 "아래는 프로젝트의 주요 파일/구조입니다" 형태로 전달

### 3.4 LLM 프롬프트 템플릿 개선
- "프로젝트 전체 구조 요약" 요청 시
  - 프롬프트에 "아래는 프로젝트의 주요 파일/클래스/기능입니다. 전체 구조를 요약해 문서로 작성해 주세요." 등 명확한 안내 추가

### 3.5 설정값 추가
- appsettings.json에 프로젝트 요약용 최대 파일/Chunk 수 제한값 추가 (예: MaxProjectSummaryFiles, MaxProjectSummaryChunks)

### 3.6 프로젝트 요약 결과 파일 저장
- 프로젝트 전체 구조 요약 결과(LLM 응답)를 프로젝트 작업 폴더에 자동 저장
  - 기본 경로: `<solution-root>/docs/project-summary.md` (docs 폴더가 없으면 `.project-summary/` 등 대체 경로 사용)
  - 파일명: `project-summary.md` (또는 설정값으로 커스터마이즈)
- 저장 방식:
  - 요약 요청이 성공적으로 완료되면, LLM 응답(프로젝트 구조 요약 문서)을 해당 경로에 파일로 저장
  - 기존 파일이 있을 경우 백업 또는 덮어쓰기 정책 적용(설정값으로 제어)
- 저장 경로/정책은 appsettings.json에 추가 옵션으로 지정 가능
  - 예: `ProjectSummary.OutputPath`, `ProjectSummary.BackupOld`, `ProjectSummary.Enabled`

---

## 4. 예시 플로우

1. 사용자가 "현재 프로젝트의 구조를 하나의 문서로 요약해줘" 입력
2. IntentResolver가 analyze_project_structure 의도 감지
3. RunOrchestrator가 전체 주요 파일 목록을 수집
4. 각 파일별로 대표 Chunk 추출 (클래스/region/주요 method)
5. BuildRagContext에서 여러 파일의 Chunk를 조립해 LLM에 전달
6. LLM이 전체 프로젝트 구조 요약 문서 생성
7. 요약 결과를 `<solution-root>/docs/project-summary.md` 등 지정 경로에 파일로 저장

---

## 5. 성공 기준

- "프로젝트 전체 구조 요약" 요청 시 실제로 여러 파일의 구조/클래스/기능이 포함된 요약 문서가 반환될 것
- 단일 파일 요약과 명확히 구분되는 결과
- 대형 프로젝트의 경우 설정값에 따라 Top-N 파일/Chunk만 포함 가능

- 프로젝트 요약 결과가 지정된 작업 폴더(예: docs/project-summary.md)에 파일로 저장될 것

---

## 6. 문서/코드 반영

- `.agents/changes/2026-05-13-project-summary-multifile.md` (본 문서)
- `.agents/contracts.md` — RunOrchestrator, VectorSearch, ResourceCache, IntentResolver 계약에 멀티파일 요약 플래그/구조 반영
- `.agents/modules.md` — RunOrchestrator, VectorSearch, ResourceCache, IntentResolver 책임/역할에 멀티파일 요약 명시
- `.agents/rules.md` — analyze_project_structure 의도 시 멀티파일 요약 규칙 추가
- `README.md` — "프로젝트 전체 요약 지원" 안내 추가

---

## 7. 참고/추가 고려사항

- 대형 프로젝트에서 LLM 입력 한도 초과 방지: Top-N 파일/Chunk, 요약 우선순위, 파일/클래스명만 포함 등 단계적 축약 지원
- 향후: 폴더 구조, 파일간 의존관계, 주요 외부 참조 등도 요약에 포함 가능
