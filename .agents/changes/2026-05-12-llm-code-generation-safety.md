# Specification: LLM 코드 생성 안전성 및 품질 보증 (v2.6.6)

**결정 날짜**: 2026-05-12  
**담당**: RunOrchestrator, Tool Registry, Prompts  
**상태**: Phase 1/2/3 구현 완료 (32b 강제 모델 선택 + timeout 조정 + 프롬프트/검증/대용량 분할 반영)  

---

## 1. 문제 배경

### 1.1 발견된 이슈

대용량 파일(50KB+, 1,000줄 이상) 리팩토링 시 심각한 기능 손실 발생:
- Main_Camera.cs: 92.4% 코드 삭제 (51.8KB → 3.9KB)
- 필수 메서드 1,050줄 이상 누락 (GetCamSetValue, ImageGrab, Vision_Initial)
- 프로젝트 컴파일 불가능

### 1.2 근본 원인

1. **파일 크기 제한**: 8KB 제한으로 파일 일부만 처리됨
2. **모델 한계**: 7B 파라미터 모델의 장문 이해도 한계
3. **검증 부재**: 메서드 손실 감지 기능 없음
4. **프롬프트 모호성**: 리팩토링 범위 명확하지 않음

---

## 2. 설계 결정

### 2.1 파일 크기 적응형 처리

**원칙**: 파일 크기에 따라 동적으로 처리 전략 선택

```
파일 크기/줄수          전략                 모델              MaxPerFileChars
────────────────────────────────────────────────────────────────────────
< 300줄                 Standard             qwen2.5-coder:7b  8KB
300-799줄               Enhanced             qwen2.5-coder:7b  16KB
800-2000줄              Large-model          qwen2.5-coder:32b 24KB
> 2000줄                Chunked split        qwen2.5-coder:32b 12KB (per chunk)
```

**구현**:
- `RunOrchestrator.GeneratePerFileProposalAsync()`에서 파일 라인 수 확인
- `GetOptimalMaxPerFileChars(int lineCount)` 메서드로 동적 제한값 결정
- `GetOptimalModelForFile(int lineCount)` 메서드로 모델 선택 (800줄 이상은 32b 강제)
- `RequireLargeFileModelOrThrow()` 메서드로 32b 사용 가능 여부 검증
- 32b 미설치/미설정 시 폴백 없이 명시적 실패 반환 (품질 보증 우선)

### 2.2 리팩토링 범위 명확화

**금지 사항** (메서드/함수/클래스 정의 변경 금지):
- ❌ public/private 메서드 삭제
- ❌ 클래스/구조체/인터페이스 필드 삭제 (상수/enum 제외)
- ❌ public API 시그니처 변경
- ❌ 클래스 상속/인터페이스 구현 변경

**허용 사항**:
- ✅ using 지시문 정리 (불필요한 것만)
- ✅ 메서드 내부 로직 최적화 (기능 유지)
- ✅ 변수명/상수명 개선
- ✅ 주석 정리
- ✅ 코드 스타일 정규화
- ✅ 중복 코드 통합 (메서드 수 유지)

### 2.3 완성도 검증

**메서드 보존율 검사**:
```
변경 후 메서드 개수 >= 변경 전 메서드 개수 × 80%
```

- 성공: 변경 후 메서드 수가 기준 이상
- 실패: 메서드 손실 >= 20% → 작업 거부 및 롤백

**구문 검증**:
- C# 컴파일러로 생성 코드 파싱 (구문 오류 0개)
- 실패 시 자동 재시도 또는 사용자 알림

---

## 3. 구현 변경사항

### 3.1 RunOrchestrator.cs

#### 추가 메서드: 파일 크기별 전략 결정

```csharp
private int GetOptimalMaxPerFileChars(int fileLineCount, bool selectionOnly = false)
{
    // 선택 범위 모드일 때는 기본 제한값 사용
    if (selectionOnly)
        return 4_000;

    // 라인 수 기반 동적 결정
    return fileLineCount switch
    {
      > 2000 => 12_000,   // 청크 단위에서도 맥락 확보
      >= 800 => 24_000,   // 대형 파일은 더 큰 입력 보장
        > 500 => 16_000,    // 중형 파일
        > 300 => 10_000,    // 소형 파일
        _ => 8_000          // 기본
    };
}

private string GetOptimalModelForFile(int fileLineCount)
{
    if (fileLineCount >= 800)
      return RequireLargeFileModelOrThrow();

    return DefaultModel;  // 기본 모델 (7b)
}

  private string RequireLargeFileModelOrThrow()
  {
    var model = _config.Llm.LargeFileModel;
    if (string.IsNullOrWhiteSpace(model))
      throw new ValidationException("대용량 파일(800줄 이상)은 32b 모델이 필요합니다. appsettings.json의 Llm.LargeFileModel을 설정하세요.");

    // 실제 모델 존재 확인은 LLM Connector에서 수행
    return model;
  }
```

#### 수정: GeneratePerFileProposalAsync() 에서 호출

```csharp
// 기존: MaxPerFileChars = 8_000;
// 변경:
var fileLineCount = currentFile.Count(c => c == '\n') + 1;
var optimalMaxChars = GetOptimalMaxPerFileChars(fileLineCount, selectionOnly: isSelectionOnly);
var optimalModel = GetOptimalModelForFile(fileLineCount);

// LlmRequest 생성 시 모델 명시
llmRequest.Model = optimalModel;
```

#### 설정 추가: appsettings.json

```json
"Llm": {
  "DefaultModel": "qwen2.5-coder:7b",
  "GeneralModel": "qwen2.5-coder:7b",
  "SummaryModel": "gemma4",
  "LargeFileModel": "qwen2.5-coder:32b"
}
```

#### 추가 검증: 메서드 개수 비율 확인

```csharp
private bool ValidateMethodPreservationRate(string beforeCode, string afterCode, double minRate = 0.8)
{
    var beforeMethods = CountPublicMethods(beforeCode);
    var afterMethods = CountPublicMethods(afterCode);

    if (afterMethods < beforeMethods * minRate)
    {
        _logger.LogWarning(
            "메서드 손실율 과도함: {Before} → {After} (최소 {Min:P0})",
            beforeMethods, afterMethods, minRate);
        return false;
    }

    return true;
}

private int CountPublicMethods(string code)
{
    // 정규식: public/private 메서드 정의 개수
    var pattern = @"(?:public|private)\s+(?:static\s+)?[\w<>[\],\s]+\s+\w+\s*\([^)]*\)\s*(?:=>|{)";
    return Regex.Matches(code, pattern, RegexOptions.Multiline).Count;
}
```

### 3.2 규칙 업데이트: rules.md에 추가

#### 신규 섹션: 코드 생성 품질 보증 (Rule 9)

```markdown
## 9. 코드 생성 품질 보증 (LLM Code Generation Quality Assurance)

### 9.1 리팩토링 범위 명확화
- 메서드/함수 정의는 삭제하지 않음
- public API는 변경하지 않음
- 클래스/구조체 필드는 보존
- 구체적 금지 사항은 프롬프트에 명시

### 9.2 대용량 파일 처리 (> 500줄)
- 동적 모델 선택: 800줄 이상은 32b 모델 우선
- 동적 크기 제한: 파일 크기에 비례하여 MaxPerFileChars 증대
- 청크 분할 준비: 2000줄 이상은 분할 처리 고려

### 9.3 완성도 검증 (Post-Generation)
- 메서드 보존율: 변경 후 메서드 수 >= 원본 × 80%
- 구문 검증: C# 컴파일러 오류 0개
- 실패 시 자동 거부 및 사용자 알림

### 9.4 의도 명확화 (Prompt Design)
- "using 정리만" 이라면 명시적으로 기술
- "메서드 삭제 금지" 같은 제약은 금지사항 섹션에 기록
- 리팩토링 도구는 단독 실행, 다른 기능과 혼합 금지
```

### 3.3 모듈 문서 업데이트: modules.md

#### RunOrchestrator 항목 업데이트 (v2.6.6)

**현재 내용**:
```
- MaxPerFileChars: 8KB for current file code (or 4KB if SelectionOnly)
```

**변경 내용**:
```
- **대용량 파일 적응형 처리 (v2.6.6)**:
  - MaxPerFileChars: 동적 결정 (8KB~20KB, 파일 크기 기준)
  - 300줄 미만: 8KB, 300-800줄: 10-16KB, 800줄 이상: 16-20KB
  - 모델 선택: 800줄 이상은 qwen2.5-coder:32b 우선
  - SelectionOnly 모드: 4KB 유지
  
- **완성도 검증**:
  - 메서드 보존율: 원본 대비 80% 이상 (공개 메서드 기준)
  - 구문 검증: C# 컴파일 오류 0개
  - 실패 시 자동 거부

- **RunOrchestrator 메서드**:
  - `GetOptimalMaxPerFileChars(lineCount)`: 파일 크기별 제한값
  - `GetOptimalModelForFile(lineCount)`: 파일 크기별 모델 선택
  - `ValidateMethodPreservationRate(before, after, minRate)`: 메서드 손실 검증
  - `CountPublicMethods(code)`: 공개 메서드 개수 계산
```

---

## 4. 프롬프트 변경사항

### 4.1 refactor_current_code.prompt.md

**추가할 섹션**: 상단에 "금지사항" 섹션 삽입

```markdown
## ⚠️ 중요 제약사항 (FORBIDDEN & CONSTRAINTS)

이 작업은 **리팩토링 (Refactoring) - 코드 정리 및 최적화** 입니다.
다음은 **절대 수행하면 안 됩니다**:

### 금지 사항
- ❌ **메서드/함수 삭제**: public/private 메서드 정의 제거 금지
- ❌ **클래스/구조체 필드 삭제**: 멤버 변수, 프로퍼티 제거 금지  
- ❌ **public API 변경**: 메서드 시그니처 변경 금지
- ❌ **클래스 구조 변경**: 상속/인터페이스 구현 변경 금지

### 허용 사항만 수행
- ✅ 불필요한 using 지시문 제거 (단, 실제로 미사용만)
- ✅ 메서드 내부 로직 최적화 (기능 유지)
- ✅ 변수명/함수명 개선
- ✅ 주석 정리
- ✅ 코드 스타일 정규화
- ✅ 중복 코드 통합 (메서드 수 유지)

### ⚠️ 주의
파일이 매우 크면, 전체 파일 구조를 놓치지 않도록 주의하세요.
특히 메서드 끝이 잘리지 않았는지 확인하세요.
```

### 4.2 organize_imports.prompt.md (신규 또는 기존 수정)

```markdown
## 목표
using/import 지시문을 정리하여 불필요한 의존성을 제거합니다.

## ⚠️ 제약사항
- ✅ 실제로 미사용하는 using만 제거
- ❌ 메서드/클래스 본문은 변경하지 말 것

## 작업 범위
1. using 분석: 각 using이 실제로 사용되는지 확인
2. 제거: 미사용 using 제거
3. 정렬: using을 System → 회사 → 타사 순서로 정렬
4. 본문 보존: 메서드와 클래스 정의는 원본 유지
```

---

## 5. 검증 프로세스

### 5.1 실행 흐름

```
[코드 수정 도구 호출]
  ↓
[파일 크기 확인 → 최적 MaxPerFileChars, Model 결정]
  ↓
[lineCount >= 800 ?]
  ├─ 예: [32b 모델 사용 가능 여부 검사]
  │      ├─ 불가: 즉시 실패 + 설치 가이드 반환
  │      └─ 가능: 32b 모델로 진행
  └─ 아니오: 기본 모델로 진행
  ↓
[LLM 호출 (선택된 모델 사용)]
  ↓
[결과 수신]
  ↓
[1차 검증: 구문 오류 검사]
  ├─ 실패: 사용자 알림 → 재시도/거부
  └─ 성공: 2차 검증
  ↓
[2차 검증: 메서드 보존율 검사]
  ├─ 실패 (손실 > 20%): 작업 거부, 사용자 알림
  └─ 성공: 제안 반환
  ↓
[사용자 승인 대기]
  ├─ 승인: 적용
  └─ 거부: 취소
```

### 5.2 오류 메시지

**메서드 손실 감지**:
```
[ERROR] 메서드 손실 과다: 100개 → 20개 (80% 감소)
리팩토링 결과가 코드 구조를 심각하게 손상합니다.
작업을 거부합니다. 원본 코드로 롤백되었습니다.

[권장사항]
- 파일이 매우 크면 부분 수정을 고려하세요
- 특정 메서드만 리팩토링하도록 선택 영역을 지정하세요
```

**구문 오류 감지**:
```
[ERROR] 생성된 코드에 C# 컴파일 오류 있음
[오류] Line 150: ')' 예상됨

작업을 거부합니다. 다시 시도하시겠습니까?
```

**32b 모델 미설치/미설정 감지**:
```
[ERROR] 대용량 파일 처리에는 qwen2.5-coder:32b 모델이 필요합니다.

[조치]
1. ollama pull qwen2.5-coder:32b
2. appsettings.json의 Llm.LargeFileModel 설정 확인
3. 서버 재시작 후 재시도
```

---

## 6. 구현 체크리스트

### 6.1 즉시 필요 (Phase 1)

- [x] RunOrchestrator.cs
  - [x] `GetOptimalMaxPerFileChars()` 구현
  - [x] `GetOptimalModelForFile()` 구현
  - [x] `GeneratePerFileProposalAsync()` 수정 (모델 선택 추가)
  - [x] `ValidateMethodPreservationRate()` 구현
  - [x] `CountPublicMethods()` 구현

- [x] rules.md 업데이트
  - [x] Rule 9 (코드 생성 품질 보증) 추가

- [x] modules.md 업데이트
  - [x] RunOrchestrator v2.6.6 명시
  - [x] 대용량 파일 처리 전략 기록

### 6.2 단기 필요 (Phase 2)

- [x] prompts/refactor_current_code.prompt.md
  - [x] "금지사항" 섹션 추가

- [x] prompts/organize_imports.prompt.md
  - [x] 신규 생성 또는 기존 수정

- [x] 테스트
  - [x] 대용량 파일(800줄 이상) 리팩토링 테스트
  - [x] 메서드 손실 검증 로직 테스트
  - [x] 800줄 이상 파일에서 32b 강제 선택 확인
  - [x] 32b 미설치 시 폴백 없이 실패 확인
  - [x] 실패 메시지에 설치 가이드 노출 확인

### 6.3 장기 고려 (Phase 3+)

- [x] 청크 분할 처리 (2000줄 이상)
- [x] 자동 구문 오류 복구 로직
- [x] 메서드별 개별 처리 모드
- [x] 트랜잭션 방식의 원자성 보증

---

## 7. 성공 기준

| 항목 | 기준 | 검증 방법 |
|------|------|----------|
| 파일 크기 적응 | 800줄 이상 파일은 32b 모델 강제 사용(폴백 금지) | 로그 확인 |
| 메서드 보존 | 메서드 손실 < 20% | 자동 검증 |
| 구문 검증 | 생성 코드 컴파일 오류 0개 | 파서 검증 |
| 금지사항 준수 | public 메서드 100% 보존 | 메서드 카운팅 |
| 프롬프트 명확성 | 리팩토링 범위 명시 | 문서 확인 |

---

## 8. 문서 동기화

이 결정 사항이 구현되면 아래 문서도 동일 세션에서 갱신:

- `.agents/rules.md` — Rule 9 추가
- `.agents/modules.md` — RunOrchestrator v2.6.6 업데이트
- `src/LocalMcpServer/prompts/*.prompt.md` — 금지사항 섹션 추가
