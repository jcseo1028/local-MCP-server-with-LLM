# local-MCP-server-with-LLM

인터넷이 없는 현장에서 Visual Studio 2022 안에서 최소한의 Agent형 코딩 보조 기능을 제공하는 로컬 MCP 서버.

## 개요

- **목적**: 오프라인 환경에서 로컬 LLM + 로컬 MCP 서버 + 현장 자료 캐시로 코딩 보조 대응력 확보
- **클라이언트**: Visual Studio 2022 (17.14+) Agent mode / 오프라인 CLI (Direct REST)
- **LLM 런타임**: Ollama (`/api/chat` 엔드포인트)
- **코드 모델**: qwen2.5-coder:7b (코드 변환·수정용)
- **일반 모델**: gemma4 (의도 분석·계획·대화·요약용)
- **접속 방식**: SSE (VS 2022 Agent mode) 또는 Direct REST API (오프라인 CLI)
- **상태**: 6개 도구 구현 (summarize·add_comments·refactor·fix·search_project_code·suggest_fix) · VS 2022 연동 · CLI REST 검증 · VS 2022 확장(VSIX) v2.0 (채팅 UI·의도 분석·자동 도구 선택·승인 흐름·side-by-side diff) · **v2.1 구현 완료** (다단계 오케스트레이션·계획수립·문서검색·빌드/테스트·결과요약·단계별 UI) · Resource Cache 구현 완료
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
  ToolRegistry/FixCodeIssuesTool.cs         — fix_code_issues 구현
  ToolRegistry/SearchProjectCodeTool.cs     — search_project_code 구현 (Resource Cache)
  ToolRegistry/SuggestFixFromErrorLogTool.cs — suggest_fix_from_error_log 구현
  ToolRegistry/PromptTemplateLoader.cs — 프롬프트 템플릿 로더
  McpServer/McpEndpoints.cs         — MCP SSE + Direct REST + Chat + Run 엔드포인트
  McpServer/IntentResolver.cs       — 의도 분석 + 계획 수립 + 요약 생성
  McpServer/ConversationStore.cs    — 대화 + Run 상태 관리 (인메모리)
  McpServer/RunOrchestrator.cs      — 9단계 Run 상태 머신 (v2.1)
  McpServer/RunModels.cs            — Run 상태 모델 + API DTO (v2.1)
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
  Services/McpRestClient.cs         — MCP Server REST 클라이언트 (Chat + Run API)
  Services/LanguageDetector.cs      — 파일 확장자 → 언어 매핑
  Services/ChatMessageViewModel.cs  — 채팅/Run 뷰 모델 (ChatRunViewModel 등)
  Services/BuildTestRunner.cs       — 오프라인 빌드/테스트 실행기 (v2.1)
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

**v2.1 추가 기능:**
- **9단계 오케스트레이션**: 의도분석 → 계획수립 → 컨텍스트수집 → 문서검색 → 수정안생성 → 승인 → 적용 → 빌드/테스트 → 결과요약
- **단계별 타임라인 UI**: 각 단계의 진행 상태를 색상으로 구분하여 실시간 표시 (Completed=초록, InProgress=파랑, Failed=빨강)
- **계획/참조 섹션**: Run 카드에 포함된 계획 항목과 참조 문서를 섹션 카드로 표시
- **컨텍스트 검증**: 코드 32KB 초과 시 자동 절단, 적용 실패 시 build/test 자동 생략
- **오프라인 빌드/테스트**: --no-restore 빌드 + 네트워크 무관 단위 테스트 자동 실행
- **최종 요약**: 의도·계획·수정·빌드 결과를 LLM이 종합 요약
- **로컬 문서 검색**: 설정된 로컬 폴더에서 규칙/참조 문서 키워드 검색

**v2.1 추가 기능:**
- **9단계 오케스트레이션**: 의도분석 → 계획수립 → 컨텍스트수집 → 문서검색 → 수정안생성 → 승인 → 적용 → 빌드/테스트 → 결과요약
- **단계별 타임라인 UI**: 각 단계의 진행 상태를 색상으로 구분하여 실시간 표시 (Completed=초록, InProgress=파랑, Failed=빨강)
- **계획/참조 섹션**: Run 카드에 포함된 계획 항목과 참조 문서를 섹션 카드로 표시
- **컨텍스트 검증**: 코드 32KB 초과 시 자동 절단, 적용 실패 시 build/test 자동 생략
- **오프라인 빌드/테스트**: --no-restore 빌드 + 네트워크 무관 단위 테스트 자동 실행
- **최종 요약**: 의도·계획·수정·빌드 결과를 LLM이 종합 요약
- **로컬 문서 검색**: 설정된 로컬 폴더에서 규칙/참조 문서 키워드 검색 (기존 ask_local_docs 기능 대체)

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