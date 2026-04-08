# System Definition

## Purpose

로컬 환경에서 LLM을 활용한 MCP(Model Context Protocol) 서버를 구축하고 운영한다.

## Boundaries

- 이 시스템은 로컬 MCP 서버의 설계, 구성, 실행을 다룬다.
- 로컬 자원 중심으로 동작한다. 외부 서비스 연동은 LLM Connector 모듈을 통해서만 수행한다.
- 특정 프로그래밍 언어, LLM 구현체, 프레임워크에 종속되지 않는다.

## Core Responsibilities

| 모듈 | 설명 |
|------|------|
| MCP Server | MCP 프로토콜을 통해 클라이언트 요청을 수신하고 응답한다 |
| Tool Registry | MCP 도구의 등록, 조회, 실행 위임을 처리한다 |
| LLM Connector | LLM과의 통신을 추상화하고 요청/응답을 변환한다 |
| Configuration | 서버, 모델, 도구 설정을 중앙 관리한다 |

## Non-Goals

- 프론트엔드 UI 제공
- 사용자 인증/인가 시스템
- 프로덕션 레벨 배포 인프라
- `modules.md`에 정의되지 않은 기능의 선제적 구현

## Design Principles

- **구현 비종속**: 특정 언어, 프레임워크, LLM에 종속하지 않는다.
- **모듈성**: 각 모듈은 `contracts.md`에 정의된 인터페이스를 통해서만 통신하며, 독립적으로 교체·확장 가능하다.
- **단순성**: 최소한의 구성으로 동작 가능한 상태를 우선한다.
