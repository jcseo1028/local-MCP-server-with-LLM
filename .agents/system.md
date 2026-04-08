# System Definition

## Purpose

인터넷이 없는 현장에서 Visual Studio 2022 Agent mode에 로컬 LLM 기반 MCP 서버를 연결하여 최소한의 에이전트형 코딩 보조 기능을 제공한다.

## Context

- Visual Studio 2022 **17.14 이상**은 Agent mode와 MCP 서버 연결을 지원한다.
- MCP 도구는 Agent mode에서 사용한다.
- 이 시스템은 GitHub Copilot을 대체하지 않는다. 오프라인 환경에서의 **대응력 확보**가 목적이다.

## Boundaries

- 이 시스템은 로컬 MCP 서버의 설계, 구성, 실행을 다룬다.
- **오프라인 전용**으로 설계한다. 인터넷 연결을 전제하지 않는다.
- 모든 자원(LLM, 자료 캐시, 도구)은 로컬에서 동작한다.
- MCP 클라이언트는 Visual Studio 2022 Agent mode이다.
- 특정 프로그래밍 언어, LLM 구현체, 프레임워크에 종속되지 않는다.

## Core Responsibilities

| 모듈 | 설명 |
|------|------|
| MCP Server | MCP 프로토콜을 통해 VS 2022 Agent mode의 요청을 수신하고 응답한다 |
| Tool Registry | MCP 도구의 등록, 조회, 실행을 처리한다 |
| LLM Connector | 로컬 LLM과의 통신을 추상화하고 요청/응답을 변환한다 |
| Resource Cache | 현장 필수 자료(문서, 표준, 참조 데이터)를 로컬에서 조회 가능하게 관리한다 |
| Configuration | 서버, 모델, 도구, 캐시 설정을 중앙 관리한다 |

## Non-Goals

- GitHub Copilot 대체
- 온라인/클라우드 LLM 연동
- 프론트엔드 UI 제공
- 사용자 인증/인가 시스템
- 프로덕션 레벨 배포 인프라
- `modules.md`에 정의되지 않은 기능의 선제적 구현

## Design Principles

- **오프라인 우선**: 인터넷 없이 모든 기능이 동작해야 한다.
- **구현 비종속**: 모듈 계약은 특정 기술에 종속하지 않는다. 권장 스택은 아래에 별도 명시한다.
- **모듈성**: 각 모듈은 `contracts.md`에 정의된 인터페이스를 통해서만 통신하며, 독립적으로 교체·확장 가능하다.
- **단순성**: 최소한의 구성으로 동작 가능한 상태를 우선한다.

## Recommended Stack

모듈 계약은 기술 중립이지만, 초기 구현에는 아래 스택을 권장한다.

| 구성요소 | 권장 기술 | 이유 |
|----------|----------|------|
| MCP Server | C# | VS 2022 생태계와 일치, 얇은 프로토콜 계층으로 구현 |
| LLM 런타임 | Ollama | 로컬 모델 실행기, 오프라인 동작, REST API 제공 |
| 기본 모델 | Qwen 계열 | 코드 추론용 기본 모델 |
| 보조 모델 | Gemma 계열 (선택) | 요약 전용 보조 모델 |
