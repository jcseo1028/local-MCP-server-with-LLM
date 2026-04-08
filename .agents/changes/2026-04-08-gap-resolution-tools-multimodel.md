# 2026-04-08 — Spec gap resolution: tools, multi-model, code index, tech stack

## Decisions

- **아키텍처**: 단일 프로세스 통합 유지. Local API Server를 별도 프로세스로 분리하지 않고, Tool Registry + LLM Connector + Resource Cache 모듈로 커버
- **구현 기술 명시**: system.md에 Recommended Stack 섹션 추가 — C# (MCP Server), Ollama (LLM 런타임), Qwen 계열 (기본), Gemma 계열 (보조 요약, 선택)
- **다중 모델**: Config.llm을 defaultModel + summaryModel 구조로 변경, LLMRequest에 model 필드 추가
- **코드 인덱스**: Resource Cache 모듈 책임을 확장하여 프로젝트 코드 인덱싱 기능 포함, CodeSearchRequest/Response 계약 추가
- **도구 4종**: contracts.md §7에 summarize_current_code, search_project_code, suggest_fix_from_error_log, ask_local_docs 정의 추가
- **도구별 파이프라인**: pipeline.md에 Tool-Specific Flows 섹션 추가, 각 도구가 어떤 Sub-Pipeline을 조합하는지 명시

## Changed Files

| File | Summary |
|------|---------|
| `.agents/system.md` | Design Principles에 "계약은 기술 중립, 구현은 권장 스택" 표현 추가, Recommended Stack 섹션 신설 |
| `.agents/modules.md` | LLM Connector에 다중 모델·Ollama 명시, Resource Cache에 코드 인덱스 책임 추가, Configuration 책임에 코드 인덱스 설정 추가 |
| `.agents/contracts.md` | LLMRequest에 model 필드 추가, Config.llm을 defaultModel/summaryModel로 변경, §4를 4a(자료 조회)+4b(코드 검색)로 분리, Config.codeIndex 추가, §7 MCP Tool Definitions(도구 4종) 신설 |
| `.agents/pipeline.md` | LLM Sub-Pipeline에 모델 선택 표기 추가, Code Search Sub-Pipeline 신설, Tool-Specific Flows 섹션 신설, Startup Sequence에 코드 인덱스 구축·모델 가용성 확인 추가 |

## Open Items

- Resource Cache 코드 인덱스의 구체적 색인 알고리즘 (텍스트 검색 vs AST 파싱) 결정 필요
- 각 도구의 프롬프트 템플릿 설계
- VS 2022에서 MCP 서버 연결 설정 방법 문서화
