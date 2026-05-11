# 2026-05-11 변경 기록: 모델 분리(의도/계획 vs 일반대화/요약)

## 목표

- (1) `GeneralModel=qwen` 설정 실험 적용
- (2) 코드에서 모델 역할을 분리하여 의도/계획은 qwen, 일반 대화/요약은 gemma4로 분기

## 변경 파일

- `src/LocalMcpServer/appsettings.json`
- `src/LocalMcpServer/Configuration/ServerConfig.cs`
- `src/LocalMcpServer/McpServer/IntentResolver.cs`
- `README.md`
- `.agents/modules.md`

## 설정 변경

- `Llm.GeneralModel`: `gemma4` → `qwen2.5-coder:7b`
- `Llm.SummaryModel`: `gemma4`
- `Chat.IntentModel`: `qwen2.5-coder:7b`
- `Chat.ChatModel`: `gemma4` (신규)

## 코드 변경

- `ChatSection`에 `ChatModel` 추가
- `IntentResolver`에 모델 선택 헬퍼 추가:
  - `ResolveIntentPlanModel()`
  - `ResolveChatModel()`
  - `ResolveSummaryModel()`
- 적용 지점:
  - 의도 분석: `ResolveIntentPlanModel()`
  - 계획 수립: `ResolveIntentPlanModel()`
  - 일반 대화: `ResolveChatModel()`
  - 결과 요약: `ResolveSummaryModel()`

## 검증

- `src/LocalMcpServer`에서 `dotnet build` 성공

## 참고

- 이번 변경은 서버 측 설정/로직만 대상으로 수행했다.
