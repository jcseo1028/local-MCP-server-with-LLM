# 2026-05-12 RAG 문서 동기화

## 결정 사항
- RAG 구현은 완료 상태로 유지한다.
- 남은 스펙 불일치는 문서 동기화 항목으로 처리한다.
- RAG 저장소 정책은 SQLite `rag-index.sqlite` 기준으로 정리한다.

## 변경 요약
- `README.md`에 RAG 인프라 상태와 SQLite 저장 경로를 추가했다.
- `.agents/system.md`에 RAG/Vector Search 책임을 추가했다.
- `.agents/rules.md`에 RAG 저장소 규칙을 추가했다.
- `.agents/changes/2026-05-12-rag-implementation-spec.md`는 기존 구현 완료 상태를 유지한다.

## 미결 항목
- 현재 스펙 기준으로 남은 구현 항목은 없다.
- 다음 세션에서는 실제 RAG 검색 품질 조정이나 SQLite 인덱스 확장만 필요할 수 있다.
