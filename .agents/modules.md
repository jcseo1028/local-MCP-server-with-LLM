# Modules

## Overview

시스템은 아래 4개 모듈로 구성된다. 각 모듈은 명확한 책임을 가지며 독립적으로 구현·교체할 수 있다.

---

## 1. MCP Server

- **책임**: MCP 프로토콜 준수, 클라이언트 요청 수신, 메서드 라우팅, 응답 반환
- **입력**: MCP 클라이언트 요청 (JSON-RPC)
- **출력**: MCP 프로토콜 응답 (JSON-RPC)
- **의존**: Tool Registry, Configuration
- **비의존**: LLM Connector (직접 호출하지 않음)

## 2. Tool Registry

- **책임**: 도구 정의 등록, 도구 목록 조회, 도구 실행
- **입력**: ToolListRequest, ToolCallRequest (`contracts.md` 참조)
- **출력**: ToolListResponse, ToolCallResponse (`contracts.md` 참조)
- **의존**: LLM Connector (도구 실행 시 필요한 경우에만), Configuration
- **비의존**: MCP Server

## 3. LLM Connector

- **책임**: LLM 엔드포인트와의 통신 추상화, 요청/응답 변환
- **입력**: LLMRequest (`contracts.md` 참조)
- **출력**: LLMResponse (`contracts.md` 참조)
- **의존**: Configuration
- **외부 의존**: LLM 엔드포인트 (로컬 또는 원격)

## 4. Configuration

- **책임**: 서버, 모델, 도구 설정의 중앙 관리
- **입력**: 설정 파일 또는 환경 변수
- **출력**: Config 객체 (`contracts.md` 참조)
- **의존**: 없음

---

## Module Interaction Rules

- 모듈 간 통신은 `contracts.md`에 정의된 계약을 통해서만 수행한다.
- 다른 모듈의 내부 구현을 직접 참조하지 않는다.
- 의존 관계는 위에 명시된 것만 허용한다. 새 의존 추가 시 이 문서를 먼저 갱신한다.
- 새 모듈 추가 시 이 문서와 `contracts.md`를 먼저 갱신한다.
