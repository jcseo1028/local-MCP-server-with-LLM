# 2026-04-08 — Resolve open items: code index, prompts, VS2022 setup

## Decisions

- **코드 인덱스**: 하이브리드 방식 채택
  - 정규식 기반 심볼 추출 (class/function/method 선언 패턴 감지)
  - 전문 텍스트 역인덱스 (키워드 검색)
  - Config.codeIndex.strategy 필드 추가 ("hybrid" 기본값)
  - 향후 AST 파싱(Roslyn) 업그레이드 경로 열어둠

- **프롬프트 템플릿**: 외부 파일 + Config 경로 방식 채택
  - 각 도구는 `{toolName}.prompt.md` 파일을 `Config.tools.promptsDirectory`에서 로드
  - 변수 치환 후 LLMRequest.prompt로 전달
  - 코드 수정 없이 현장에서 프롬프트 튜닝 가능

- **VS 2022 MCP 연결**: 두 가지 방법 모두 문서화
  - `.vs/mcp.json` (솔루션별, 권장) — 팀 공유 가능
  - Tools → Options (사용자별) — VS 전체에 적용

## Changed Files

| File | Summary |
|------|---------|
| `.agents/contracts.md` | Config.tools.promptsDirectory 추가, Config.codeIndex.strategy 추가 |
| `.agents/modules.md` | Tool Registry에 프롬프트 관리 책임 추가, Resource Cache에 하이브리드 코드 인덱스 상세 기술 |
| `.agents/pipeline.md` | Tool-Specific Flows에 템플릿 로드·변수 치환 단계 추가, Startup Sequence에 프롬프트 로드·인덱스 상세 단계 추가 |
| `README.md` | 권장 스택 반영, VS 2022 MCP 연결 설정 가이드 추가 (.vs/mcp.json + Tools → Options) |

## Open Items

- 없음 (전체 해소)
