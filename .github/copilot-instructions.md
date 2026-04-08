# Copilot Instructions

## Source of Truth

- `.agents/` 디렉터리가 이 프로젝트의 단일 진실 공급원(Single Source of Truth)이다.
- 모든 수정은 `.agents/` 내 정의된 system, modules, contracts, pipeline, rules를 기반으로 한다.

## Scope Restrictions

- 변경 요청 시 해당 요청에 명시된 모듈과 파일만 수정한다.
- 전체 저장소를 스캔하거나 탐색하지 않는다. 필요한 컨텍스트만 참조한다.
- 요청 범위를 벗어난 리팩터링, 구조 변경, 기능 추가를 수행하지 않는다.
- `.agents/modules.md`에 정의되지 않은 모듈을 생성하지 않는다.

## Modification Protocol

변경 수행 시 아래 순서를 따른다:

1. `.agents/contracts.md`에서 영향받는 계약을 확인한다.
2. `.agents/modules.md`에서 대상 모듈의 책임과 의존 관계를 확인한다.
3. `.agents/rules.md`의 모든 규칙을 준수하며 변경한다.
4. 변경은 증분적(incremental)이고 독립 검증 가능(testable)해야 한다.

## Change Principles

- **최소 변경 원칙**: 요청을 충족하는 최소한의 변경만 수행한다. 관련 없는 코드를 수정하지 않는다.
- **계약 우선**: 모듈 인터페이스나 데이터 구조를 변경할 때 `contracts.md`를 먼저 갱신한다.
- **독립 진화**: 모듈 간 직접 참조를 금지하고 계약을 통해서만 통신한다.

## Definition of Done

코드 변경이 발생한 모든 작업은 종료 전에 반드시 아래를 수행한다:

1. `.agents/` 문서(system, modules, pipeline, contracts, rules) 중 영향 범위를 갱신한다.
2. README.md에 사용자 관점 영향(설치, 실행, 설정, 기능)이 있으면 반영한다.
3. 의존성 파일(있는 경우)을 갱신한다.
4. 최종 보고 시 문서 반영 여부를 함께 보고한다.

## Session Finalization

- 작업 세션 종료 시 결정 사항과 변경 내역을 `.agents/changes/`에 기록한다.
- 파일명 형식: `YYYY-MM-DD-<short-description>.md`
