# 2026-04-17 LLM 응답 절단(truncation) 문제 수정

## 문제

- 코드 수정 도구(add_comments, fix_code_issues, refactor_current_code)의 LLM 출력이 잘림
- 일반 채팅 응답이 잘림
- 최종 요약이 수정안 내용을 충분히 표시하지 못함

## 근본 원인

| 위치 | 기존 MaxTokens | 기존 NumCtx | 문제 |
|------|-------------|---------|------|
| CodeToolBase (기본값) | 4096 | 8192 | 큰 코드 수정 시 출력 절단 |
| 도구별 오버라이드 (3개) | 4096 | 8192 | 위와 동일 |
| GenerateChatResponseAsync | 1024 | 4096 | 채팅 응답 절단 |
| GenerateSummaryAsync | 512 | 2048 | 요약이 극히 짧음 + tool_result로 컨텍스트 초과 |
| LlmOptions 기본값 | 1024 | 4096 | 기본값 자체가 낮음 |

추가 문제: `GenerateSummaryAsync`가 수정된 전체 코드를 `tool_result`로 프롬프트에 포함하여 작은 NumCtx를 초과함.

## 변경 사항

### 토큰 한도 증가

| 위치 | MaxTokens | NumCtx |
|------|-----------|--------|
| LlmOptions 기본값 | 1024 → **4096** | 4096 → **16384** |
| CodeToolBase (기본값) | 4096 → **8192** | 8192 → **16384** |
| AddCommentsTool | 4096 → **8192** | 8192 → **16384** |
| FixCodeIssuesTool | 4096 → **8192** | 8192 → **16384** |
| RefactorCurrentCodeTool | 4096 → **8192** | 8192 → **16384** |
| GenerateChatResponseAsync | 1024 → **4096** | 4096 → **16384** |
| GenerateSummaryAsync | 512 → **2048** | 2048 → **16384** |

### 요약 컨텍스트 보호

- `IntentResolver.TruncateForSummary()` 추가: 요약 프롬프트에 넣기 전에 `tool_result`를 6000자로 절단
- `run_summary.prompt.md`: "3~5문장" 제약 해제 → 충분한 길이 허용

## 수정된 파일

- `src/LocalMcpServer/LlmConnector/LlmModels.cs`
- `src/LocalMcpServer/ToolRegistry/CodeToolBase.cs`
- `src/LocalMcpServer/ToolRegistry/AddCommentsTool.cs`
- `src/LocalMcpServer/ToolRegistry/FixCodeIssuesTool.cs`
- `src/LocalMcpServer/ToolRegistry/RefactorCurrentCodeTool.cs`
- `src/LocalMcpServer/McpServer/IntentResolver.cs`
- `src/LocalMcpServer/prompts/run_summary.prompt.md`

## 미결 항목

- Ollama 모델별 실제 지원 컨텍스트 윈도우 크기 확인 필요 (qwen2.5-coder:7b 기본 32K, gemma4 기본 값 확인)
- 사용자가 appsettings.json에서 토큰 한도를 설정할 수 있는 구성 옵션 추가 검토
