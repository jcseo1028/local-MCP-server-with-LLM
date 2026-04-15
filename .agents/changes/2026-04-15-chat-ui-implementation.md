# 2026-04-15: Chat UI 구현 + 후속 수정

## 요약

VSIX Tool Window를 버튼 기반에서 채팅 기반으로 전면 변경.
서버에 의도 분석(IntentResolver), 대화 관리(ConversationStore), Chat API 엔드포인트 추가.
코드 수정 시 side-by-side diff + 승인/거부 흐름 구현.

### 후속 수정 (같은 날)

1. **선택 영역 적용 버그 수정**: 선택 영역만 보냈을 때 전체 파일이 대체되던 문제 해결. `CodeChangeInfo.SelectionOnly` 추가, `ApproveChangeAsync`에서 원본 텍스트를 문서에서 찾아 해당 부분만 교체.
2. **NormalizeIndentation 줄바꿈 버그 수정**: 탭↔스페이스 변환 후 `"\r\n"` 하드코드 → 문서의 실제 줄바꿈 형식(docNewline) 감지하여 사용.
3. **대화 세션 백업/복원 기능**: 새 대화 시작 시 현재 대화 자동 백업, 📋 버튼으로 이전 대화 목록 표시 및 복원. 최대 20개 세션 유지.

## 변경 파일

### 서버 (LocalMcpServer)

| 파일 | 변경 유형 | 설명 |
|------|-----------|------|
| `Configuration/ServerConfig.cs` | 수정 | `ChatSection` 추가 (IntentModel, ConversationTimeoutMinutes, MaxConversationHistory) |
| `appsettings.json` | 수정 | "Chat" 설정 섹션 추가 |
| `McpServer/IntentResolver.cs` | 신규 | 사용자 메시지 → 도구 매핑 (LLM 기반 의도 분석) |
| `McpServer/ConversationStore.cs` | 신규 | IConversationStore 인터페이스 + InMemoryConversationStore 구현 |
| `McpServer/McpEndpoints.cs` | 수정 | `POST /api/chat`, `POST /api/chat/approve` 엔드포인트 추가 |
| `Program.cs` | 수정 | DI에 IConversationStore, IntentResolver 등록 |
| `prompts/intent_analysis.prompt.md` | 신규 | 의도 분석 프롬프트 |
| `prompts/general_chat.prompt.md` | 신규 | 일반 대화 프롬프트 |

### VSIX (LocalMcpVsExtension)

| 파일 | 변경 유형 | 설명 |
|------|-----------|------|
| `ToolWindows/SummaryToolWindowControl.cs` | 전면 재작성 | 채팅 UI (메시지 버블, side-by-side diff, 승인/거부 버튼) |
| `Services/ChatMessageViewModel.cs` | 신규 | ChatMessageRole, ApprovalState, ChatMessageViewModel, CodeChangeInfo |
| `Services/McpRestClient.cs` | 수정 | SendChatAsync, SendApprovalAsync 메서드 + Chat DTO 추가 |

### 문서

| 파일 | 설명 |
|------|------|
| `.agents/contracts.md` | §9 Chat API, §10 Chat Approval API 계약 추가 |
| `.agents/modules.md` | MCP Server·VS Extension 모듈 책임 갱신 |
| `.agents/pipeline.md` | Chat Pipeline 섹션 추가 |
| `README.md` | VSIX v2.0 설명, Chat API 레퍼런스 추가 |

## 설계 결정

1. **의도 분석**: LLM이 사용자 메시지를 분석하여 JSON `{toolName, confidence, description}` 반환
2. **대화 상태**: 인메모리 ConcurrentDictionary, 30분 TTL, 최대 20개 이력
3. **승인 흐름**: 코드 수정 도구(add_comments, refactor, fix) 결과는 `requiresApproval=true`로 반환, 사용자 확인 후 에디터 적용
4. **일반 대화**: 의도 분석에서 도구가 매칭되지 않으면 general_chat 프롬프트로 자유 대화

## 빌드 결과

- MCP Server: ✅ 0 errors, 0 warnings
- VSIX: ✅ 0 C# errors (nullable 경고 7개), VSIX 패키징은 VS MSBuild 필요
