# 2026-04-08 — Initial spec: offline VS2022 Agent mode MCP server

## Decisions

- 시스템 목적을 "오프라인 현장에서 VS 2022 Agent mode에 로컬 MCP 서버 제공"으로 구체화
- MCP 클라이언트를 VS 2022 Agent mode (17.14+)로 확정
- "GitHub Copilot 대체"를 명시적 Non-Goal로 선언
- 오프라인 전용 설계: 원격 LLM 연동을 비목표로 설정, LLM Connector에 로컬 전용 제약 추가
- Resource Cache 모듈 신설: 현장 필수 자료(문서, 표준, API 참조)를 로컬에서 조회
- Resource Cache 자료는 사전 준비(pre-populated) 필수, 런타임 다운로드 금지
- Tool Registry가 LLM Connector 및 Resource Cache 양쪽을 호출 가능 (MCP Server는 직접 접근 불가)

## Changed Files

| File | Summary |
|------|---------|
| `.agents/system.md` | Purpose를 오프라인 VS2022 MCP 서버로 구체화, Context 섹션 추가, Boundaries에 오프라인 전용·VS2022 클라이언트 명시, Core Responsibilities에 Resource Cache 추가, Non-Goals에 Copilot 대체·온라인 LLM 추가, Design Principles에 오프라인 우선 추가 |
| `.agents/modules.md` | 4→5개 모듈로 확장, Resource Cache(Module 4) 추가, LLM Connector에 로컬 전용 제약 추가, Tool Registry 의존에 Resource Cache 추가, MCP Server 비의존에 Resource Cache 추가, Configuration 책임에 캐시 설정 추가 |
| `.agents/contracts.md` | 섹션 1을 "VS 2022 Agent Mode ↔ MCP Server"로 변경, 섹션 4(Tool Registry → Resource Cache) 추가: CacheLookupRequest/Response 정의, 섹션 5(Configuration Schema)에 cache 항목 추가, 기존 섹션 번호 재배정 |
| `.agents/pipeline.md` | Client를 VS 2022 Agent Mode로 변경, Tool Call Flow에 Cache Sub-Pipeline 분기 추가, Cache Sub-Pipeline 섹션 신설, LLM Sub-Pipeline에 "로컬" 명시, Startup Sequence에 Resource Cache 초기화 단계 추가 (Step 2), Error Flow 응답 대상을 VS 2022 Agent Mode로 변경 |
| `README.md` | 프로젝트 설명·구성·요구사항·문서 안내 추가 |

## Open Items

- 구체적인 MCP 도구 목록 정의 (어떤 보조 기능을 도구로 제공할 것인지)
- Resource Cache 자료 포맷 및 색인 방식 결정
- VS 2022 MCP 연결 설정 방법 문서화 (`.vscode/mcp.json` 또는 VS 설정)
- 로컬 LLM 엔드포인트 최소 사양 및 권장 모델 가이드라인
