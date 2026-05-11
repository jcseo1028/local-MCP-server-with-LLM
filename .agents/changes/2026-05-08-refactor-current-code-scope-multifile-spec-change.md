# 스펙 변경 제안: refactor_current_code 멀티파일 수용성과 변경 범위 제어 강화

## 1) 배경

현상:
- 파일 2개를 선택해 요청했지만 결과가 1개 파일 중심으로 생성됨
- using 정리 수준의 요청 의도와 달리 소스 전체 리팩터링이 제안됨

요청:
- 원인 분석
- 재발 방지를 위한 스펙 변경안 정의

---

## 2) 원인 분석 (코드 근거)

### 원인 A. 의도 라우팅이 "부분 정리"를 "전체 리팩터링"으로 해석

- 위치: src/LocalMcpServer/McpServer/IntentResolver.cs
- 키워드 fallback 매핑에서 "정리"가 refactor_current_code로 직접 매핑됨
- 즉, "using만 정리" 같은 제한 의도가 있어도 도구 선택은 전체 리팩터링 도구로 고정될 수 있음

영향:
- 사용자의 변경 범위 의도(작은 수정)가 도구 단계에서 손실됨

### 원인 B. refactor 프롬프트가 본질적으로 광범위 리팩터링을 유도

- 위치: src/LocalMcpServer/prompts/refactor_current_code.prompt.md
- 지시문이 가독성, 구조 개선, 현대 문법 적용, 중복 제거를 동시에 강하게 요구
- 출력 형식이 "리팩터링된 전체 코드" 중심

영향:
- LLM이 최소 수정이 아닌 전면 개선을 수행하기 쉬움

### 원인 C. 멀티파일 입력은 전달되지만 refactor 계약/프롬프트가 멀티파일 출력을 강제하지 않음

- 위치:
  - src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs
  - src/LocalMcpServer/McpServer/RunOrchestrator.cs
  - src/LocalMcpServer/ToolRegistry/CodeToolBase.cs
  - .agents/contracts.md (7d refactor_current_code)
- VSIX는 Files 배열을 RunStartRequest로 전송함
- RunOrchestrator는 files_context를 도구 인자로 전달함
- 그러나 refactor 계약(7d)과 프롬프트는 files_context 기반 멀티파일 출력 규약이 명시되어 있지 않음

영향:
- 모델이 단일 코드 블록으로 응답할 가능성이 높고, 멀티파일 결과 안정성이 떨어짐

### 원인 D. 멀티파일 파싱 실패 시 단건 폴백으로 조용히 전환

- 위치: src/LocalMcpServer/McpServer/RunOrchestrator.cs
- ParseFileBlocks 결과가 0건이면 단건 Original/Modified 제안으로 폴백
- 이때 사용자 입장에서는 "2개 선택했는데 1개만 반영"처럼 보임

영향:
- 오류가 드러나지 않고 품질 저하가 정상 결과처럼 보임

---

## 3) 스펙 변경 목표

1. 멀티파일 선택 시 결과도 멀티파일로 안정적으로 회수한다.
2. "using만 정리" 같은 제한 의도를 계약 단계에서 보존한다.
3. 파싱 실패를 묵살하지 않고 재시도 또는 명시적 실패로 처리한다.

---

## 4) 변경안

## SC-1. 코드 수정 도구 공통 입력 계약에 변경 범위 필드 추가

대상: .agents/contracts.md §7c, §7d, §7e

신규 필드:
- request_scope: string | null
  - 값 예시: imports_only, formatting_only, targeted_patch, full_refactor
- request_constraints: string | null
  - 값 예시: "using만 정리", "구조 변경 금지", "메서드 시그니처 변경 금지"

규칙:
- request_scope가 imports_only 또는 targeted_patch이면 대규모 구조 변경 금지
- request_constraints는 프롬프트에 원문 그대로 전달

## SC-2. refactor_current_code 입력 계약에 files_context 공식 반영

대상: .agents/contracts.md §7d

변경:
- code: string | null
- language: string | null
- files_context: string | null
- required 규칙: code 또는 files_context 중 하나는 반드시 필요

효과:
- 서버 구현(CodeToolBase/RunOrchestrator)과 계약 불일치 제거

## SC-3. 멀티파일 모드 출력 계약 강제

대상: .agents/contracts.md §7c, §7d, §7e + 프롬프트 파일

규칙:
- files_context가 비어있지 않으면 반드시 파일 블록 형식으로만 출력
- 포맷:
  - [FILE: 경로]
  - 전체 수정 코드
  - [/FILE]
- 수정이 없는 파일은 출력 금지
- 코드 블록 외 설명 텍스트는 최소화(선택: 상단 한 줄 요약)

효과:
- RunOrchestrator 파서의 해석 모호성 제거

## SC-4. RunOrchestrator 멀티파일 엄격 모드 도입

대상: src/LocalMcpServer/McpServer/RunOrchestrator.cs

규칙:
- 입력 files_context가 존재하는데 ParseFileBlocks 결과가 0건이면 단건 폴백 금지
- 처리 순서:
  1) 동일 요청 재시도 1회 (출력 포맷 강제 문구 추가)
  2) 여전히 0건이면 실패 상태로 종료하고 원인 메시지 반환

효과:
- "조용한 단건 폴백" 제거, 문제를 명시적으로 관찰 가능

## SC-5. 의도 라우팅 세분화: import/usings 전용 경로

대상:
- src/LocalMcpServer/McpServer/IntentResolver.cs
- src/LocalMcpServer/prompts/intent_analysis.prompt.md
- .agents/contracts.md (신규 도구 정의)

변경:
- 신규 도구 organize_imports 추가
  - 목적: using/import 정리만 수행
  - 비목표: 변수명 변경, 구조 리팩터링, 로직 변경
- fallback 키워드에서 "using", "import", "네임스페이스 정리"는 organize_imports 우선 매핑

효과:
- 사용자 의도와 도구 능력의 정합성 향상

## SC-6. refactor 프롬프트에 최소 변경 우선 규칙 명문화

대상: src/LocalMcpServer/prompts/refactor_current_code.prompt.md

추가 규칙:
- 사용자 요청이 제한 범위를 명시하면 해당 범위 밖 변경 금지
- 요청에 "만" 또는 "only"가 포함되면 대상 외 변경 금지
- 불필요한 rename/분리/재배치 금지

효과:
- "using만" 요청에서 과도한 리팩터링 억제

---

## 5) 수용 기준 (Acceptance Criteria)

AC-1:
- 파일 2개 선택 + "using만 정리" 요청 시 proposal.changes가 2개 파일 단위로 반환되어야 한다.

AC-2:
- 변경 diff에서 using/import 구문 외 실질 코드(메서드 본문, 시그니처, 타입명) 변경이 없어야 한다.

AC-3:
- files_context가 있는 요청에서 파일 블록 파싱 실패 시 단건 폴백이 발생하지 않아야 한다.

AC-4:
- intent 분석 결과가 organize_imports로 라우팅되거나, refactor 선택 시 request_scope=imports_only가 보존되어야 한다.

---

## 6) 하위 호환성

- 기존 단건 code/language 호출은 유지
- files_context 미사용 요청은 기존 동작 유지
- 단, 멀티파일 모드에서의 단건 폴백만 제거(의도된 breaking change)

---

## 7) 구현 우선순위

1. SC-2, SC-3, SC-4 (멀티파일 안정성 우선)
2. SC-6 (과도한 리팩터링 억제)
3. SC-5 (도구 세분화)
4. SC-1 (범용 범위 계약 정착)

---

## 8) 변경 대상 문서/코드 목록

문서:
- .agents/contracts.md
- .agents/modules.md (신규 도구 추가 시)
- .agents/pipeline.md (멀티파일 파싱 실패 처리 경로 변경 시)

서버:
- src/LocalMcpServer/McpServer/RunOrchestrator.cs
- src/LocalMcpServer/McpServer/IntentResolver.cs
- src/LocalMcpServer/ToolRegistry/RefactorCurrentCodeTool.cs
- src/LocalMcpServer/ToolRegistry/CodeToolBase.cs
- src/LocalMcpServer/prompts/refactor_current_code.prompt.md
- src/LocalMcpServer/prompts/intent_analysis.prompt.md

VSIX:
- src/LocalMcpVsExtension/ToolWindows/SummaryToolWindowControl.cs (실패 메시지/UX 보강 시)

---

## 9) 결론

이번 이슈의 본질은 "멀티파일 입력 가능"과 "멀티파일 출력 보장" 사이의 계약 공백, 그리고 "부분 수정 의도"를 표현/보존하는 계약 부재다.

따라서 우선은 멀티파일 출력 엄격화(SC-3, SC-4)와 refactor 범위 제한 규칙(SC-6)을 적용하고,
중기적으로 import/usings 전용 도구 분리(SC-5)로 의도-도구 정합성을 확보하는 것이 바람직하다.
