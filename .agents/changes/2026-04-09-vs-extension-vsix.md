# 2026-04-09 — VS 2022 확장 (VSIX) 구현

## 결정 사항

- 오프라인 환경에서 VS 2022 안에서 코드 요약을 사용할 수 있도록 **VSIX Tool Window 확장**을 구현
- 사용자가 4가지 옵션(PowerShell 스크립트, C# CLI, Web UI, VSIX) 중 **VSIX**를 선택
- SDK-style csproj + Community.VisualStudio.Toolkit.17 기반
- WPF UI는 XAML 대신 **프로그래밍 방식**으로 구현 (SDK-style 프로젝트에서 XAML Page 컴파일 문제 회피)

## 변경 내역

### 신규 파일 (`src/LocalMcpVsExtension/`)

| 파일 | 역할 |
|------|------|
| `LocalMcpVsExtension.csproj` | SDK-style VSIX 프로젝트 (.NET Framework 4.8) |
| `source.extension.vsixmanifest` | VSIX 매니페스트 (VS 2022 17.14+, amd64) |
| `VSCommandTable.vsct` | 메뉴 커맨드 (보기 → 다른 창 → Local MCP 코드 요약) |
| `VSCommandTable.cs` | 커맨드 GUID/ID 상수 |
| `LocalMcpVsExtensionPackage.cs` | ToolkitPackage 진입점 |
| `Commands/ShowSummaryWindowCommand.cs` | Tool Window 열기 커맨드 |
| `ToolWindows/SummaryToolWindow.cs` | BaseToolWindow 정의 |
| `ToolWindows/SummaryToolWindowControl.cs` | 프로그래밍 WPF UI |
| `Services/McpRestClient.cs` | REST 클라이언트 (contracts §8) |
| `Services/LanguageDetector.cs` | 파일 확장자 → 언어 매핑 |
| `nuget.config` | PackageSourceMapping 로컬 오버라이드 |
| `.gitignore` | 빌드 산출물 제외 |

### 수정 파일

| 파일 | 변경 내용 |
|------|-----------|
| `.agents/modules.md` | §6 VS Extension (VSIX) 모듈 추가 + 빌드 주의사항 |
| `.agents/pipeline.md` | VS Extension Pipeline 섹션 추가 |
| `README.md` | VSIX 빌드/설치/사용법, 구성 테이블, 접속 비교 테이블, 제한사항 갱신 |

## 해결한 기술 이슈

1. **XAML 컴파일 실패**: SDK-style .NET 4.8 프로젝트에서 `<Page>` XAML 자동 컴파일 불가 → 프로그래밍 방식 WPF UI로 전환
2. **NuGet PackageSourceMapping**: 글로벌 설정 제한 → 로컬 `nuget.config`로 해결
3. **VSIX 패키징 불가 (`CreateVsixContainer` 타겟 미발견)**: `<Project Sdk="...">` 자동 import에서 VsSDK.targets가 Sdk.targets 앞에 위치 → 명시적 `<Import Project="Sdk.props/targets" />` 분리로 해결
4. **VSSDK1202 (TemplateOutputDirectory 필요)**: `<TemplateOutputDirectory>` 속성 추가
5. **VSSDK1311 (ProductArchitecture 필요)**: vsixmanifest에 `<ProductArchitecture>amd64</ProductArchitecture>` 추가
6. **VSSDK1025 (pkgdef 미생성)**: VsSDK.targets가 Sdk.targets 뒤에 import되지 않아 `TargetPath` 미정의 → import 순서 수정으로 해결

## 빌드 결과

- `src/LocalMcpVsExtension/bin/Release/net48/LocalMcpVsExtension.vsix` (423 KB)
- 오류 0, 경고 0

## 문서 반영 여부

- [x] `.agents/modules.md` — §6 추가 및 빌드 주의사항 보충
- [x] `.agents/pipeline.md` — VS Extension Pipeline 섹션 추가
- [x] `README.md` — VSIX 빌드/설치/사용, 구성 테이블, 비교 테이블, 제한사항 갱신
- [x] `.agents/contracts.md` — 변경 없음 (기존 §8 Direct REST API 그대로 사용)
