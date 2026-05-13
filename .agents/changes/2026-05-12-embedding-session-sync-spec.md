# Spec Change: Embedding 저장소-대화 세션 동기화 (v2.6.8 제안)

**작성일**: 2026-05-12  
**요청 배경**: 확장( VSIX ) 대화 세션과 서버 RAG embedding 저장소를 동기화하여, 서버 재시작 이후에도 이전 세션의 검색/요약 성능을 유지하고 초기 embedding 폭증을 줄인다.

---

## 1) 검토 결과 (현재 상태)

### 1.1 이미 가능한 것
- VSIX는 `conversationId`를 서버에 전달한다.
- 서버는 `ConversationStore`에서 `conversationId` 기반 세션을 유지한다.
- RAG 검색은 chunk 단위로 embedding 조회/저장을 `IResourceCache`로 위임한다.

### 1.2 현재 한계
- chunk embedding 저장이 메모리 fallback 중심이라, **서버 재시작 시 재사용 손실**이 발생한다.
- 대화 세션과 embedding 저장소 사이에 명시적 동기화 계약이 없어, 확장 세션 복원 시 RAG warm-start가 약하다.
- 결과적으로 요청 초기에 embedding 요청이 대량 발생할 수 있다.

---

## 2) 변경 목표

1. **세션 지속성**: 확장 대화 세션(`conversationId`)과 서버 embedding 캐시 상태를 연결한다.
2. **재시작 복원성**: 서버 재시작 후에도 세션 연관 RAG 상태를 복원한다.
3. **중복 계산 최소화**: 동일 솔루션/동일 chunk는 세션 간에도 embedding 재사용한다.
4. **안전한 폴백**: SQLite 문제 시 메모리 캐시로 자동 폴백하되, 동기화 계약은 유지한다.

---

## 3) 제안 아키텍처 (핵심)

### 3.1 2계층 캐시 모델
- **Global Embedding Store (영속)**
  - 키: `solution_hash + embedding_model + chunk_key`
  - 목적: 세션 간 공용 재사용
- **Conversation Session Index (영속 메타)**
  - 키: `conversation_id + solution_hash`
  - 목적: 최근 사용 chunk, 마지막 접근 시각, warm-start 힌트 관리

### 3.2 동기화 원칙
- embedding 본문 벡터는 Global Store에 단일 보관 (중복 저장 금지)
- conversation은 Global Store를 참조하는 인덱스/메타만 저장
- 서버 시작 시 conversation 메타를 로드하여 검색 초기에 warm chunk를 우선 조회

---

## 4) 계약 변경안 (초안)

## 4.1 Run 시작 요청 확장
`ChatRunStartRequest`에 아래 필드를 추가 (선택):
- `sessionSyncEnabled: bool` (기본 `true`)
- `sessionSnapshotVersion: string?` (확장-서버 동기화 버전 식별)

기존 `conversationId`는 유지하며, 해당 ID가 세션 동기화의 기준 키가 된다.

## 4.2 ResourceCache 계약 확장
신규 계약(초안):
- `GetConversationRagState(conversationId, solutionHash)`
- `UpsertConversationRagState(conversationId, solutionHash, metadata)`
- `TouchConversationChunk(conversationId, solutionHash, chunkKey)`

주의: 기존 `GetChunkEmbeddingAsync/StoreChunkEmbeddingAsync`는 유지한다.

---

## 5) 저장소 스키마 변경안 (SQLite)

기존: `rag_chunk_embeddings` 유지

신규 테이블(초안):
1. `rag_conversation_state`
- `conversation_id TEXT NOT NULL`
- `solution_hash TEXT NOT NULL`
- `embedding_model TEXT NOT NULL`
- `last_access_utc TEXT NOT NULL`
- `warm_chunk_keys_json TEXT NULL`
- PK: `(conversation_id, solution_hash, embedding_model)`

2. `rag_conversation_chunk_usage`
- `conversation_id TEXT NOT NULL`
- `solution_hash TEXT NOT NULL`
- `chunk_key TEXT NOT NULL`
- `last_used_utc TEXT NOT NULL`
- `hit_count INTEGER NOT NULL DEFAULT 0`
- PK: `(conversation_id, solution_hash, chunk_key)`

인덱스:
- `ix_conv_usage_last_used` on `last_used_utc`
- `ix_conv_usage_solution` on `(solution_hash, conversation_id)`

---

## 6) 동작 시나리오

### 6.1 첫 요청 (콜드 스타트)
1. 확장 → 서버: `conversationId` 포함 Run 시작
2. 서버: Global Store 조회
3. 미스한 chunk만 embedding 생성 후 Global Store 저장
4. conversation usage 메타 업데이트

### 6.2 재요청 (웜 스타트)
1. 동일 `conversationId` 요청
2. session usage 기준 warm chunk 우선 조회
3. 대부분 Global Store hit → embedding API 호출 감소

### 6.3 서버 재시작 후
1. conversation 메타 + global embedding 로드
2. 같은 `conversationId`로 요청 시 warm-start 재현
3. 초기 embedding 폭증 완화

---

## 7) 운영/정책

### 7.1 만료 정책
- `rag_conversation_state`: 마지막 접근 14일 경과 시 정리
- `rag_conversation_chunk_usage`: 30일 경과 저활용 항목 정리

### 7.2 무효화 정책
아래 변경 시 해당 key 범위 무효화:
- `embedding_model` 변경
- `solution_hash` 변경
- chunk content hash 변경(기존 chunk_key 변경으로 자동 반영)

### 7.3 장애 폴백
- SQLite 접근 실패 시 메모리 캐시 사용
- 복구 후 비동기 flush(선택) 또는 다음 요청부터 영속 재개

---

## 8) 기대 효과

- 서버 재시작 이후에도 세션별 RAG 체감 성능 유지
- embedding API 호출량/초기 지연 감소
- 동일 솔루션 내 반복 작업에서 응답 안정성 향상

---

## 9) 구현 범위 제안 (단계적)

### Phase A (필수)
- SQLite conversation 메타 테이블 추가
- `conversationId` 기반 usage 업데이트
- warm chunk 우선 조회 적용

### Phase A 구현 현황 (2026-05-12)
- [x] Run 시작 요청에 세션 동기화 필드 추가 (`sessionSyncEnabled`, `sessionSnapshotVersion`)
- [x] VectorSearch에서 conversation 기반 warm chunk 우선 정렬 적용
- [x] chunk 선택 결과를 conversation usage로 갱신 (`TouchConversationChunkAsync`)
- [x] ResourceCache 세션 상태 계약/구현 추가 (메모리 저장소 기반)
- [x] SQLite 영속 테이블 연동 (`rag_conversation_state`, `rag_conversation_chunk_usage`)

### Phase B (개선)
- sessionSnapshotVersion 협상
- 세션 통계 API(히트율/미스율) 노출
- 백그라운드 정리 작업

---

## 10) 수용 기준

1. 같은 `conversationId`로 2회 연속 요청 시 embedding 요청 수가 유의미하게 감소한다.
2. 서버 재시작 후 동일 `conversationId` 요청에서도 1회차 대비 embedding 요청 수가 감소한다.
3. SQLite 장애 시에도 기능은 중단되지 않고 메모리 폴백으로 진행된다.
4. 기존 API 클라이언트와 하위 호환된다(새 필드 optional).

---

## 11) 영향 파일(예상)

- `src/LocalMcpServer/McpServer/RunModels.cs` (Run 시작 DTO 확장)
- `src/LocalMcpServer/McpServer/McpEndpoints.cs` (요청 필드 수용)
- `src/LocalMcpServer/ResourceCache/IResourceCache.cs` (세션 동기화 계약 추가)
- `src/LocalMcpServer/ResourceCache/ResourceCacheService.cs` (SQLite 스키마/조회/저장)
- `src/LocalMcpVsExtension/Services/McpRestClient.cs` (요청 필드 전달)
- `.agents/contracts.md` (계약 반영)
- `.agents/modules.md` (모듈 책임 반영)

---

## 12) 결정 요약

- 채택 권고: **예**
- 권고 이유: 현재 아키텍처와 호환되며, `conversationId`가 이미 존재하므로 확장 비용 대비 성능 이득이 크다.
- 리스크: SQLite 초기화/잠금 이슈 재발 가능성
- 완화: 2계층 캐시 + 폴백 정책 + 단계적 롤아웃
