# 2026-04-09 — 코드 수정 도구 3종 추가

## 결정 사항

- 코드 수정 도구 3종(add_comments, refactor_current_code, fix_code_issues) 구현
- `CodeToolBase` 추상 클래스로 공통 패턴(code+language 입력, LLM 호출, 옵션 오버라이드) 추출
- VSIX에 "📋 적용" 버튼 추가 — 코드 수정 결과를 에디터에 직접 반영

## 변경 내역

### MCP Server (`src/LocalMcpServer/`)

| 파일 | 변경 |
|------|------|
| `ToolRegistry/CodeToolBase.cs` | **신규** — 코드 수정 도구 추상 베이스 클래스 |
| `ToolRegistry/AddCommentsTool.cs` | **신규** — add_comments 도구 (Temperature=0.2, MaxTokens=2048) |
| `ToolRegistry/RefactorCurrentCodeTool.cs` | **신규** — refactor_current_code 도구 (Temperature=0.3, MaxTokens=2048) |
| `ToolRegistry/FixCodeIssuesTool.cs` | **신규** — fix_code_issues 도구 (Temperature=0.2, MaxTokens=2048) |
| `prompts/add_comments.prompt.md` | **신규** — 주석 추가 프롬프트 템플릿 |
| `prompts/refactor_current_code.prompt.md` | **신규** — 리팩터링 프롬프트 템플릿 |
| `prompts/fix_code_issues.prompt.md` | **신규** — 이슈 수정 프롬프트 템플릿 |
| `Program.cs` | 3개 도구 DI 등록 및 ToolRegistryService에 추가 |

### VSIX Extension (`src/LocalMcpVsExtension/`)

| 파일 | 변경 |
|------|------|
| `ToolWindows/SummaryToolWindowControl.cs` | "📋 적용" 버튼, `EditTools` HashSet, `ApplyResultToEditorAsync()`, `ExtractCodeFromResult()` 추가 |

### `.agents/` 문서

| 파일 | 변경 |
|------|------|
| `contracts.md` | §7c(add_comments), §7d(refactor_current_code), §7e(fix_code_issues) 추가, 기존 §7c→§7f, §7d→§7g 재번호 |
| `modules.md` | Tool Registry에 CodeToolBase·3개 도구 추가, VS Extension에 코드 적용 기능 기술 |
| `README.md` | 도구 테이블 갱신, VSIX 기능 목록 갱신, 프로젝트 구조 갱신, 사용법에 적용 버튼 설명 추가 |

## 아키텍처 패턴

```
CodeToolBase (abstract)
  ├── AddCommentsTool        — 주석 자동 추가
  ├── RefactorCurrentCodeTool — 코드 리팩터링  
  └── FixCodeIssuesTool      — 버그/이슈 수정

각 도구는 ToolName, Description, PromptFileName, GetLlmOptions()만 오버라이드.
ExecuteAsync, Validate, InputSchema 등은 CodeToolBase가 제공.
```

## 빌드 결과

- MCP Server: `dotnet build -c Release` — 0 errors, 0 warnings
- VSIX: MSBuild Rebuild Release — 0 errors, 0 warnings
