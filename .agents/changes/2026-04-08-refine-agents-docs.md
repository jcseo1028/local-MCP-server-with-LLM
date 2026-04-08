# 2026-04-08 — Refine .agents and copilot-instructions

## Decisions

- 모든 파일에서 모듈명을 영문 정식 명칭(MCP Server, Tool Registry, LLM Connector, Configuration)으로 통일
- MCP Server → LLM Connector 직접 의존을 제거하고, Tool Registry → LLM Connector 경로로 수정
- contracts.md 섹션 3을 "Tool Registry → LLM Connector"로 변경하여 모듈 독립성 보장
- LLMResponse.usage를 선택 필드(null 허용)로 변경하여 LLM 제공자 가정 제거
- ToolCallResponse.content.type에서 미정의 타입("image", "resource") 제거
- Response.error 구조를 `{code, message}`로 명시화
- rules.md에 Dependency Control 규칙(Rule 5) 신설
- pipeline.md에서 외부 엔티티를 `(외부)` 표기로 명시, 미정의 "Tool Handler" 용어를 "Tool Registry"로 통일
- system.md Core Responsibilities 테이블의 한국어 약어를 모듈 정식 명칭으로 교체
- copilot-instructions.md에서 "requirements.txt" 특정 파일 가정을 제거하고 일반화

## Changed Files

| File | Summary |
|------|---------|
| `.github/copilot-instructions.md` | Scope Restrictions 강화, Definition of Done 일반화, 용어 정렬 |
| `.agents/system.md` | Core Responsibilities 모듈명 통일, "리소스" 미정의 참조 제거, Boundaries 명확화, 모듈성 원칙에 contracts.md 참조 추가 |
| `.agents/modules.md` | MCP Server 의존에서 LLM Connector 제거, 각 모듈 입출력에 contracts.md 참조 명시, 비의존 관계 표기 추가, Interaction Rules 강화 |
| `.agents/contracts.md` | 섹션 3을 Tool Registry → LLM Connector로 변경, error 구조 명시, usage 선택 필드화, content.type 축소, 방향 표기 범례 추가 |
| `.agents/pipeline.md` | 외부 엔티티 표기 통일, "Tool Handler" → "Tool Registry" 용어 통일, 단계 번호 정리, Error Flow에 contracts.md 참조 추가 |
| `.agents/rules.md` | Rule 1에 금지 사항 목록 추가, Rule 3 순서 명확화, Rule 4에 modules.md/contracts.md 기준 명시, Rule 5(Dependency Control) 신설, 번호 재배정 |

## Open Items

- 없음
