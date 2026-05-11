# local-MCP-server-with-LLM

인터넷이 없는 현장에서 Visual Studio 2022 안에서 최소한의 Agent형 코딩 보조 기능을 제공하는 로컬 MCP 서버.

## 개요

- **목적**: 오프라인 환경에서 로컬 LLM + 로컬 MCP 서버 + 현장 자료 캐시로 코딩 보조 대응력 확보
- **클라이언트**: Visual Studio 2022 (17.14+) Agent mode / 오프라인 CLI (Direct REST)
- **LLM 런타임**: Ollama (`/api/chat` 엔드포인트)
- **코드 모델**: qwen2.5-coder:7b (코드 변환·수정용)
- **일반 모델**: gemma4 (의도 분석·계획·대화·요약용)
- **접속 방식**: SSE (VS 2022 Agent mode) 또는 Direct REST API (오프라인 CLI)
- **상태**: 7개 도구 구현 (summarize·add_comments·refactor·organize_imports·fix·search_project_code·suggest_fix) · VS 2022 연동 · CLI REST 검증 · VS 2022 확장(VSIX) v2.0 (채팅 UI·의도 분석·자동 도구 선택·승인 흐름·side-by-side diff) · **v2.1 구현 완료** (다단계 오케스트레이션·계획수립·문서검색·빌드/테스트·결과요약·단계별 UI) · Resource Cache 구현 완료 · **v2.2 구현 완료** (멀티파일 컨텍스트 전송·[FILE:] 프롬프트 안내·ApplyResults 전송) · **v2.3 구현 완료** (파일별 승인/거부 UI·파일 선택 UI·atomic rollback·[FILE:] 폴백 파싱) · **v2.4 구현 완료** (per-hunk accept/reject UI·unified diff 컬러 하이라이트·서버 측 hunks 사전 계산·대용량 파일 토큰 초과 대응) · **v2.5 구현 완료** (organize_imports 도구 추가·멀티파일 출력 엄격 모드·using/import 전용 검증 및 자동 보정) · **v2.6 구현 완료** (다중 도구 실행 계획 + PendingPatch 확정/되돌리기 API)
- **비목표**: GitHub Copilot 대체

## 구성

| 구성요소 | 역할 |
|----------|------|
| MCP Server | VS 2022 SSE + CLI Direct REST 요청을 수신·응답 |
| Tool Registry | MCP 도구 등록·조회·실행 |
| LLM Connector | 로컬 LLM과의 통신 추상화 |
| Resource Cache | 현장 필수 자료(문서, 표준, 참조)의 로컬 조회 |
| Configuration | 서버·모델·도구·캐시 설정 중앙 관리 |
| VS Extension (VSIX) | VS 2022 Tool Window에서 채팅 기반 코딩 보조 UI 제공 |

## 요구사항

- Visual Studio 2022 **17.14 이상**
- .NET 9.0 SDK
- 로컬 LLM 엔드포인트 (권장: Ollama)
  - 코드 모델: `qwen2.5-coder:7b` (4.7GB)
  - 일반 모델: `gemma4` (9.6GB) — 16GB+ RAM 권장
- 현장 자료 캐시 (사전 준비 필요)

## 빠른 시작

### 1. Ollama 실행

```bash
ollama serve
ollama pull qwen2.5-coder:7b   # 코드 변환용
ollama pull gemma4              # 일반 태스크용 (의도 분석·계획·대화·요약)
```

### 2. MCP 서버 실행

```bash
cd src/LocalMcpServer
dotnet run
```

서버가 `http://localhost:5100` 에서 시작된다.

### 3. VS 2022 연결 (온라인 환경)

솔루션 루트의 `.vs/mcp.json` 이 이미 설정되어 있다. VS 2022에서 솔루션을 열고 Agent mode 채팅에서 MCP 도구를 사용할 수 있다.

### 4. CLI 직접 호출 (오프라인 환경)

인터넷이 없는 환경에서는 VS 2022 Agent mode가 MCP 도구를 호출할 수 없다. 이 경우 Direct REST API를 통해 CLI에서 직접 호출한다.

**도구 목록 조회:**

```bash
curl http://localhost:5100/api/tools/list
```

**도구 호출 (코드 요약 예시):**

```bash
curl -X POST http://localhost:5100/api/tools/call \
  -H "Content-Type: application/json" \
  -d '{
    "name": "summarize_current_code",
    "arguments": {
      "code": "public class Foo { public int Bar() { return 42; } }",
      "language": "csharp"
    }
  }'
```

**PowerShell 예시:**

```powershell
# 도구 목록
Invoke-RestMethod http://localhost:5100/api/tools/list

# 코드 요약
$body = @{
  name = "summarize_current_code"
  arguments = @{ code = (Get-Content D:\_Github_LLM\local-MCP-server-with-LLM\src\LocalMcpServer\LlmConnector\LlmModels.cs -Raw); language = "csharp" }
} | ConvertTo-Json
Invoke-RestMethod http://localhost:5100/api/tools/call -Method POST `
  -ContentType "application/json" -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
```

### 현재 지원 도구

| 도구 | 설명 | 상태 |
|------|------|------|
| `summarize_current_code` | 코드 텍스트를 받아 한국어로 구조화된 요약 | ✅ 구현 완료 |
| `add_comments` | 코드에 문서 주석(XML doc, JSDoc 등) + 인라인 주석 자동 추가 | ✅ 구현 완료 |
| `refactor_current_code` | 가독성·구조·현대적 표현 기반 코드 리팩터링 | ✅ 구현 완료 |
| `organize_imports` | using/import 구문만 정리 (코드 본문 변경 금지) | ✅ 구현 완료 |
| `fix_code_issues` | 버그·안티패턴·보안 취약점 탐지 및 수정 | ✅ 구현 완료 |
| `search_project_code` | 프로젝트 내 코드 검색 | ✅ 구현 완료 (Resource Cache 필요) |
| `suggest_fix_from_error_log` | 에러 로그 기반 수정 제안 | ✅ 구현 완료 |

#### v2.1 오케스트레이션 내부 서비스

| 도구/서비스 | 설명 | 소유 모듈 | 상태 |
|------------|------|-----------|------|
| `RunOrchestrator` | Run 단위 9단계 상태 머신 관리 + 컨텍스트 검증(32KB) | MCP Server | ✅ 구현 완료 |
| `IntentResolver.GeneratePlan()` | 의도 분석 후 2~5개 작업 계획 수립 | MCP Server | ✅ 구현 완료 |
| `DocumentSearcher` | 로컬 문서 폴더 내 규칙/참조 문서 검색 | MCP Server | ✅ 구현 완료 |
| `BuildTestRunner` | 오프라인 빌드(--no-restore) + 단위 테스트 실행 | VS Extension | ✅ 구현 완료 |
| `SummaryGenerator` | 전체 run 결과를 종합한 최종 요약 생성 | MCP Server | ✅ 구현 완료 |

#### 미구현 모듈

| 모듈 | 설명 | 상태 |
|------|------|------|
| Resource Cache | 현장 필수 자료 로컬 저장·조회 + 프로젝트 코드 인덱스 | ✅ 구현 완료 |

## VS 2022 MCP 연결 설정 (SSE)

> 인터넷이 필요한 온라인 환경에서 VS 2022 Agent mode를 통해 MCP 도구를 사용하는 방법이다.
> 오프라인 환경에서는 위의 "CLI 직접 호출" 섹션을 참고한다.

### 방법 1: 솔루션별 설정 (권장)

솔루션 루트에 `.vs/mcp.json` 파일을 생성한다:

```json
{
  "servers": {
    "local-mcp": {
      "type": "sse",
      "url": "http://localhost:5100/sse"
    }
  }
}
```

- `type`: 전송 방식 (`"sse"` 또는 `"stdio"`)
- `url`: MCP 서버 엔드포인트 URL (SSE 방식인 경우)
- `.vs/mcp.json`은 솔루션을 여는 모든 사용자에게 적용된다. Git에 포함하면 팀 공유 가능.

### 방법 2: VS 사용자 설정

1. **Tools → Options → GitHub Copilot → MCP Servers** 로 이동
2. **Add Server** 클릭
3. 서버 이름, 전송 방식, URL을 입력
4. 이 설정은 해당 VS 인스턴스의 모든 솔루션에 적용된다.

> **참고**: Agent mode에서 MCP 도구를 사용하려면 채팅 창의 모드를 "Agent"로 전환해야 한다.

## 프로젝트 구조

```
src/LocalMcpServer/
  .gitignore                        — Git 제외 설정 (bin/, obj/ 등)
  Program.cs                       — 진입점, DI 구성, 서버 시작
  Configuration/ServerConfig.cs    — 설정 모델 (contracts.md §5)
  LlmConnector/LlmModels.cs        — LLM 요청/응답 모델 (contracts.md §3)
  LlmConnector/OllamaConnector.cs  — Ollama /api/chat 클라이언트
  ToolRegistry/IMcpTool.cs          — 도구 인터페이스
  ToolRegistry/ToolRegistryService.cs — 도구 등록·조회
  ToolRegistry/SummarizeCurrentCodeTool.cs — summarize_current_code 구현
  ToolRegistry/CodeToolBase.cs              — 코드 수정 도구 공통 추상 클래스
  ToolRegistry/AddCommentsTool.cs           — add_comments 구현
  ToolRegistry/RefactorCurrentCodeTool.cs   — refactor_current_code 구현
  ToolRegistry/OrganizeImportsTool.cs       — organize_imports 구현
  ToolRegistry/FixCodeIssuesTool.cs         — fix_code_issues 구현
  ToolRegistry/SearchProjectCodeTool.cs     — search_project_code 구현 (Resource Cache)
  ToolRegistry/SuggestFixFromErrorLogTool.cs — suggest_fix_from_error_log 구현
  ToolRegistry/PromptTemplateLoader.cs — 프롬프트 템플릿 로더
  McpServer/McpEndpoints.cs         — MCP SSE + Direct REST + Chat + Run 엔드포인트
  McpServer/IntentResolver.cs       — 의도 분석 + 계획 수립 + 요약 생성
  McpServer/ConversationStore.cs    — 대화 + Run 상태 관리 (인메모리)
  McpServer/RunOrchestrator.cs      — 9단계 Run 상태 머신 (v2.1) + 서버 측 diff 사전 계산 (A-2) + B-5 토큰 절단
  McpServer/RunModels.cs            — Run 상태 모델 + API DTO + DiffHunkInfo (v2.1/A-2)
  McpServer/DiffEngine.cs           — 서버 측 LCS diff 엔진 (A-2 사전 계산용)
  McpServer/DocumentSearcher.cs     — 로컬 문서 검색 + Resource Cache 통합 (v2.1)
  ResourceCache/IResourceCache.cs   — Resource Cache 인터페이스
  ResourceCache/CacheModels.cs      — 캐시 요청/응답 모델 (contracts.md §4)
  ResourceCache/ResourceCacheService.cs — 문서 캐시 + 코드 인덱스 (심볼 추출 + 텍스트 역인덱스)
  prompts/                          — 프롬프트 템플릿 파일 (코드 수정 없이 튜닝 가능)
  appsettings.json                  — 서버 설정

src/LocalMcpVsExtension/
  LocalMcpVsExtension.csproj        — SDK-style VSIX 프로젝트 (\u0046ramework 4.8)
  source.extension.vsixmanifest     — VSIX 매니페스트 (VS 2022 17.14+)
  VSCommandTable.vsct               — 메뉴 커맨드 테이블
  VSCommandTable.cs                 — 커맨드 GUID/ID 상수
  LocalMcpVsExtensionPackage.cs     — VS 패키지 진입점
  Commands/ShowSummaryWindowCommand.cs — Tool Window 열기 커맨드
  ToolWindows/SummaryToolWindow.cs  — Tool Window 정의
  ToolWindows/SummaryToolWindowControl.cs — 채팅 UI + Run 타임라인 + 빌드/테스트 실행
  Services/McpRestClient.cs         — MCP Server REST 클라이언트 (Chat + Run API + DiffHunkDto)
  Services/LanguageDetector.cs      — 파일 확장자 → 언어 매핑
  Services/ChatMessageViewModel.cs  — 채팅/Run 뷰 모델 (HunkSelection, FileChangeInfo 포함)
  Services/BuildTestRunner.cs       — 오프라인 빌드/테스트 실행기 (v2.1)
  Services/LineDiffEngine.cs        — Myers LCS diff 엔진 (DiffHunk)
.vs/mcp.json                        — VS 2022 MCP 연결 설정
```

## 프롬프트 튜닝

`src/LocalMcpServer/prompts/` 디렉터리의 `.prompt.md` 파일을 수정하면 서버 재시작 없이 프롬프트가 반영된다. `{{variable}}` 형식의 변수 치환을 지원한다.

## 접속 방식 비교

| 환경 | 접속 방법 | 엔드포인트 | 비고 |
|------|-----------|-----------|------|
| 온라인 | VS 2022 Agent mode (SSE) | `GET /sse` + `POST /message` | Copilot 확장 필요 |
| 오프라인 | VS 2022 확장 (VSIX) | `POST /api/chat/runs` | IDE 통합 채팅 UI (v2.1 Run) |
| 오프라인 | CLI / 스크립트 (REST) | `GET /api/tools/list` + `POST /api/tools/call` | IDE 무관 |

두 방식 모두 동일한 MCP 서버 프로세스를 공유하며, 같은 도구와 LLM 커넥터를 사용한다.

## VS 2022 확장 (VSIX) — 오프라인 IDE 통합

인터넷이 없는 환경에서 VS 2022 안에서 채팅 형태로 코딩 보조를 사용할 수 있는 Tool Window 확장이다.

**v2.0 주요 기능:**
- **채팅 UI**: 자연어로 요청하면 서버가 의도를 분석하고 적절한 도구를 자동 선택·실행
- **의도 분석**: LLM이 사용자 메시지를 분석하여 summarize/add_comments/refactor/fix 중 적절한 도구를 선택
- **승인 흐름**: 코드 수정 결과를 side-by-side diff로 표시, 사용자 확인 후에만 에디터에 반영
- **선택 영역 지원**: 선택 영역만 보냈을 때 해당 부분만 교체, 전체 파일일 때 전체 교체
- **대화 이력**: 같은 대화 안에서 컨텍스트를 유지하며 후속 요청 가능
- **대화 세션 백업**: 새 대화 시작 시 현재 대화를 자동 저장, 필요 시 이전 대화를 복원하여 참고 가능 (최대 20개)
- VS Dark/Light/Blue 테마 자동 대응 (VsBrushes + VSColorTheme)
- LLM 응답을 Markdown으로 렌더링 (헤딩, 리스트, 코드블록, 볼드 등)

**v2.3 추가 기능:**
- **파일별 개별 승인/거부**: 멀티 파일 수정 시 각 파일 Expander 헤더에 체크박스 표시 — 체크 해제된 파일은 적용 제외
- **파일 선택 UI**: 📁 버튼으로 현재 열린 파일 체크리스트 패널 토글 — 서버에 전송할 파일 직접 선택
- **Atomic Rollback**: 멀티 파일 적용 시 어느 파일이든 실패하면 이미 적용된 전체 파일 원복 (all-or-nothing)
- **[FILE:] 폴백 파싱**: LLM이 `### path.ext` / `**path.ext**` / `// File: path` 형식으로 출력했을 때도 파싱 가능
- **ApplyResults 전송**: 파일별 적용 결과를 `ClientResultRequest.ApplyResults`에 담아 서버에 전송
- **멀티 파일 컨텍스트 수집**: `IVsRunningDocumentTable`로 VS에서 열린 코드 파일 자동 수집 후 서버 전달

**v2.4 추가 기능:**
- **Per-Hunk Accept/Reject UI**: 각 diff hunk 헤더에 체크박스 표시 — 체크 해제 시 해당 hunk만 건너뜀
- **Unified Diff 컬러 하이라이트**: 삭제 라인=빨간 배경(`-`), 추가 라인=초록 배경(`+`), ±3 컨텍스트 라인, `@@ -x,y +x,z @@` 헤더
- **서버 측 Hunks 사전 계산**: MCP 서버가 proposal 생성 시 `DiffEngine.Compute()`로 hunks 사전 계산 → VSIX 재계산 중복 제거
- **대용량 파일 토큰 초과 대응**: 파일당 8,000자 / 전체 32,000자 제한, 초과 시 비율 절단 + 프롬프트에 생략 표시

**v2.5 추가 기능:**
- **organize_imports 도구 추가**: using/import 구문 정리 전용 도구 분리 (`organize_imports`)
- **멀티파일 출력 엄격 모드**: `[FILE: 경로]...[/FILE]` 파싱 실패 시 단건 조용한 폴백 없이 1회 재시도 후 명시적 실패
- **using/import 전용 안전장치**: 본문 변경 감지 시 검증 수행, 필요 시 import 블록만 원본 본문에 자동 보정 후 적용

**v2.6 추가 기능:**
- **다중 도구 실행 계획**: `allowMultiToolPlan=true` 요청 시 단일 프롬프트를 최대 `maxPlanSteps` 단계의 도구 실행 계획으로 확장
- **Run 계획 상태 노출**: Run snapshot에 `executionMode`, `planSteps`, `currentStepIndex` 포함
- **PendingPatch 추적**: 코드 변경 단계에서 임시 적용 대상 패치를 서버가 추적
- **확정/되돌리기 API**: `POST /api/chat/runs/{runId}/confirm`, `POST /api/chat/runs/{runId}/revert`
- **VSIX 버튼 연동**: PendingPatch가 있으면 UI 버튼이 `확정/되돌리기`로 동작하고, 없으면 기존 `승인/거부`를 사용
- **VSIX 실행 단계 표시**: Run 카드에 도구 실행 단계(`planSteps`)와 현재 인덱스를 표시
- **리팩토링 키워드 보강**: 계획 생성 시 `리팩토링` 표현도 `refactor_current_code` 단계로 인식
- **빈 계획 항목 정리**: 계획 수립 LLM의 빈 응답은 공백 항목으로 노출하지 않음
- **재시도 예산 가드**: 멀티파일 포맷 재시도 전 남은 실행 시간(최소 90초)을 확인해 예산 부족 시 재시도 생략
- **취소 로그 분리**: 타임아웃 취소와 외부 취소(서버 중단/요청 취소) 로그 및 에러 메시지를 분리
- **모델 역할 분리(v2.6.2)**: 의도/계획은 `Chat.IntentModel`(qwen) 우선, 일반 대화는 `Chat.ChatModel`(gemma4), 결과 요약은 `Llm.SummaryModel`(gemma4) 우선 사용
- **도구 모델 강제(v2.6.3)**: 코드 수정 도구 실행 시 `LlmRequest.Model`을 명시해 `Llm.DefaultModel`(코드 모델)로 고정
- **예산 분리(v2.6.3)**: Run 전체 예산(10분)과 step 실행 예산(기본 4분) 분리 적용
- **대기 시간 제외(v2.6.3)**: 승인 대기 및 클라이언트 빌드/테스트 대기 시간은 Run 예산 계산에서 제외
- **파일별 단건 조합(v2.6.4)**: 멀티파일 편집 요청은 파일별 단건 호출로 처리하고 서버가 결과를 `FileChange` 목록으로 조합
- **보조 컨텍스트 하이브리드(v2.6.4)**: 현재 파일 외 선택 파일은 요약 정보만 참고 컨텍스트로 전달하여 모델의 멀티파일 출력 의존도를 줄임
- **실패 즉시 중단(v2.6.5)**: 클라이언트 적용/빌드/테스트 실패가 보고되면 다음 step으로 진행하지 않고 Run을 즉시 `Failed`로 종료

**v2.1 추가 기능:**
- **9단계 오케스트레이션**: 의도분석 → 계획수립 → 컨텍스트수집 → 문서검색 → 수정안생성 → 승인 → 적용 → 빌드/테스트 → 결과요약
- **의도/계획 검증 모드**: `POST /api/chat/runs`에 `intentAndPlanOnly=true`를 주면 의도 분석과 계획 수립까지만 실행하고 즉시 완료
- **단계별 타임라인 UI**: 각 단계의 진행 상태를 색상으로 구분하여 실시간 표시 (Completed=초록, InProgress=파랑, Failed=빨강)
- **계획/참조 섹션**: Run 카드에 포함된 계획 항목과 참조 문서를 섹션 카드로 표시
- **컨텍스트 검증**: 코드 32KB 초과 시 자동 절단, 적용 실패 시 build/test 자동 생략
- **오프라인 빌드/테스트**: --no-restore 빌드 + 네트워크 무관 단위 테스트 자동 실행
- **최종 요약**: 의도·계획·수정·빌드 결과를 LLM이 종합 요약
- **로컬 문서 검색**: 설정된 로컬 폴더에서 규칙/참조 문서 키워드 검색

### 빌드

Visual Studio 2022 MSBuild를 사용한다:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" `
  src\LocalMcpVsExtension\LocalMcpVsExtension.csproj /t:Rebuild /p:Configuration=Release
```

빌드 결과: `src/LocalMcpVsExtension/bin/Release/net48/LocalMcpVsExtension.vsix`

### 설치

1. VS 2022를 닫는다
2. `LocalMcpVsExtension.vsix` 파일을 더블클릭하여 설치한다
3. VS 2022를 다시 연다

### 사용

1. **MCP 서버 실행**: `cd src/LocalMcpServer && dotnet run`
2. **Tool Window 열기**: VS 메뉴 → **보기 → 다른 창 → Local MCP 코드 요약**
3. 채팅 입력란에 자연어로 요청을 입력한다. 예:
   - "이 코드를 요약해줘"
   - "주석을 추가해줘"
   - "리팩터링 해줘"
   - "버그가 있는지 확인해줘"
4. **"현재 코드 포함" 체크박스**: 체크하면 에디터의 현재 파일/선택 영역을 자동 첨부
5. 서버가 의도를 분석하고 적절한 도구를 **자동 선택**하여 실행한다
6. 결과가 **Markdown으로 렌더링**되어 채팅 버블에 표시된다
7. 코드 수정이 포함된 경우 **side-by-side diff**로 원본/변경 코드가 표시된다
8. **"✅ 확인" 버튼**을 클릭하면 변경이 에디터에 반영된다 (Ctrl+Z로 되돌리기 가능)
9. **"❌ 거부" 버튼**을 클릭하면 변경을 취소한다
10. **📋 버튼**으로 이전 대화 목록을 표시하고, 선택하여 복원할 수 있다

서버 주소는 ⚙ 버튼을 클릭하여 변경할 수 있다 (기본: `http://localhost:5100`).

## Chat API 레퍼런스

VSIX 채팅 UI가 사용하는 API이다. CLI에서도 직접 호출 가능하다.

### `POST /api/chat`

자연어 메시지를 보내면 서버가 의도를 분석하고 적절한 도구를 실행한다.

**요청 바디:**

```json
{
  "message": "이 코드에 주석을 추가해줘",
  "code": "public class Foo { ... }",
  "language": "csharp",
  "selectionOnly": false,
  "conversationId": null
}
```

**응답:**

```json
{
  "conversationId": "abc-123",
  "intent": {
    "toolName": "add_comments",
    "confidence": 0.95,
    "description": "코드에 문서 주석을 추가합니다"
  },
  "result": "주석이 추가된 코드입니다:\n```csharp\n...\n```",
  "codeChange": {
    "original": "public class Foo { ... }",
    "modified": "/// <summary>...</summary>\npublic class Foo { ... }",
    "toolName": "add_comments"
  },
  "requiresApproval": true
}
```

### `POST /api/chat/approve`

코드 변경을 승인하거나 거부한다.

**요청 바디:**

```json
{
  "conversationId": "abc-123",
  "approved": true
}
```

## Direct REST API 레퍼런스

### `GET /api/tools/list`

등록된 도구 목록을 반환한다.

**응답 예시:**

```json
{
  "tools": [
    {
      "name": "summarize_current_code",
      "description": "현재 파일 또는 선택 영역의 코드를 요약합니다.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "code": { "type": "string", "description": "요약 대상 코드 텍스트" },
          "language": { "type": "string", "description": "프로그래밍 언어 (선택)" }
        },
        "required": ["code"]
      }
    }
  ]
}
```

### `POST /api/tools/call`

도구를 실행하고 결과를 반환한다.

**요청 바디:**

```json
{
  "name": "summarize_current_code",
  "arguments": {
    "code": "public class Foo { ... }",
    "language": "csharp"
  }
}
```

**성공 응답:**

```json
{
  "content": [
    { "type": "text", "text": "### 1. 전체 목적\n..." }
  ]
}
```

**에러 응답 (도구 없음):**

```json
{
  "error": "Tool not found: unknown_tool"
}
```

## 알려진 제한사항

- VS 2022 Agent mode는 **GitHub Copilot 확장이 설치**되어 있어야 MCP 도구를 사용할 수 있다
- 현재 Copilot의 클라우드 모델이 MCP 도구 호출 여부를 판단하므로, **완전 오프라인 환경에서는 VSIX 확장 또는 CLI REST를 사용**해야 한다
- VSIX 확장은 VS 2022 MSBuild로 빌드해야 한다 (`dotnet build`만으로는 `.vsix` 파일이 생성되지 않음)
- 두 모델을 동시에 사용할 경우 ~15GB 메모리가 필요하다. 메모리 부족 시 `gemma4:e2b` (7.2GB)로 대체 가능
- Ollama 모델 전환 시 첫 호출에 10-30초 로딩 지연이 발생할 수 있다
- `appsettings.json`에서 `Llm.GeneralModel`을 null로 설정하면 모든 태스크가 `DefaultModel` 단일 모델로 동작한다

## 문서

프로젝트 설계와 규칙은 `.agents/` 디렉터리에서 관리한다:

- `.agents/system.md` — 시스템 정의·경계·원칙
- `.agents/modules.md` — 모듈 구성·책임·의존
- `.agents/contracts.md` — 모듈 간 데이터 계약
- `.agents/pipeline.md` — 런타임 데이터 흐름
- `.agents/rules.md` — 변경 규칙