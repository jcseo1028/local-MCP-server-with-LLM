# Dual-Model Routing: gemma4 + qwen2.5-coder:7b

**날짜**: 2026-04-16  
**유형**: 기능 추가 (Feature)  
**상태**: 구현 완료 ✅

---

## 1. 요약

Ollama에 gemma4 모델을 추가하여 **일반 태스크**(의도 분석, 계획 생성, 일반 대화, 요약)에는 gemma4를 사용하고, **코드 태스크**(코드 주석 추가, 리팩토링, 버그 수정, 코드 요약)에는 기존 qwen2.5-coder:7b를 유지한다.

## 2. gemma4 모델 다운로드

### 사전 요구사항
- Ollama 최신 버전 설치 완료 (`ollama --version`으로 확인)
- 최소 10GB 디스크 여유 공간

### 다운로드 명령

```powershell
# 기본 모델 (e4b, 9.6GB, 128K context) — 권장
ollama pull gemma4

# 경량 모델 (e2b, 7.2GB, 128K context) — 메모리 부족 시
ollama pull gemma4:e2b

# 대형 모델 (26b MoE, 18GB, 256K context) — 고성능 GPU 보유 시
ollama pull gemma4:26b
```

### 다운로드 확인

```powershell
# 설치된 모델 목록 확인
ollama list

# gemma4 동작 테스트
ollama run gemma4 "안녕하세요, 간단히 자기소개 해주세요."
```

### 모델 선택 가이드

| 모델 | 크기 | 컨텍스트 | 권장 환경 |
|------|------|----------|-----------|
| `gemma4` (e4b) | 9.6GB | 128K | 16GB+ RAM, 일반 개발 PC (**권장**) |
| `gemma4:e2b` | 7.2GB | 128K | 8GB RAM, 저사양 환경 |
| `gemma4:26b` | 18GB | 256K | 32GB+ RAM, 고성능 GPU |

---

## 3. 모델 라우팅 설계

### 3.1 현행 구조

```
OllamaConnector 모델 결정 순서:
  request.Model → SummaryModel → DefaultModel → "qwen2.5-coder:7b" (하드코딩)
```

- `LlmSection`: `DefaultModel = "qwen2.5-coder:7b"`, `SummaryModel = null`
- `ChatSection`: `IntentModel = null`
- 모든 LLM 호출이 결과적으로 qwen2.5-coder:7b 단일 모델로 수렴

### 3.2 변경 후 구조

`LlmSection`에 `GeneralModel` 속성을 추가한다.

```csharp
public sealed class LlmSection
{
    public string Provider { get; set; } = "ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string DefaultModel { get; set; } = "qwen2.5-coder:7b";   // 코드 태스크 전용
    public string? GeneralModel { get; set; }                         // 신규: 일반 태스크 전용
    public string? SummaryModel { get; set; }                         // 유지: 요약 전용 오버라이드
}
```

### 3.3 모델 라우팅 테이블

| 컴포넌트 | 메서드 | 태스크 유형 | 사용 모델 |
|----------|--------|------------|-----------|
| IntentResolver | `AnalyzeIntentAsync` | 의도 분석 | **GeneralModel** (gemma4) |
| IntentResolver | `GeneratePlanAsync` | 계획 생성 | **GeneralModel** (gemma4) |
| IntentResolver | `GenerateChatResponseAsync` | 일반 대화 | **GeneralModel** (gemma4) |
| IntentResolver | `GenerateSummaryAsync` | 실행 요약 | **GeneralModel** (gemma4) |
| CodeToolBase | `ExecuteAsync` | 코드 변환 | **DefaultModel** (qwen2.5-coder:7b) |
| AddCommentsTool | (CodeToolBase) | 주석 추가 | **DefaultModel** (qwen2.5-coder:7b) |
| RefactorCurrentCodeTool | (CodeToolBase) | 리팩토링 | **DefaultModel** (qwen2.5-coder:7b) |
| FixCodeIssuesTool | (CodeToolBase) | 버그 수정 | **DefaultModel** (qwen2.5-coder:7b) |
| SummarizeCurrentCodeTool | `ExecuteAsync` | 코드 요약 | **DefaultModel** (qwen2.5-coder:7b) |

### 3.4 모델 결정 로직 (OllamaConnector 변경 없음)

OllamaConnector의 기존 모델 결정 체인은 변경하지 않는다.  
**호출자가 `LlmRequest.Model`에 적절한 모델을 명시**하는 방식으로 라우팅한다.

```
호출자 모델 결정:
  IntentResolver → request.Model = config.Llm.GeneralModel ?? config.Llm.DefaultModel
  CodeToolBase   → request.Model = null (→ OllamaConnector가 DefaultModel 사용)
```

---

## 4. 설정 파일 변경

### appsettings.json

```json
{
  "Llm": {
    "Provider": "ollama",
    "Endpoint": "http://localhost:11434",
    "DefaultModel": "qwen2.5-coder:7b",
    "GeneralModel": "gemma4",
    "SummaryModel": null
  },
  "Chat": {
    "IntentModel": null,
    "ConversationTimeoutMinutes": 30,
    "MaxConversationHistory": 20
  }
}
```

- `GeneralModel = "gemma4"`: 일반 태스크에 사용
- `DefaultModel = "qwen2.5-coder:7b"`: 코드 태스크에 사용 (기존 유지)
- `Chat.IntentModel`: deprecated 예정 — `GeneralModel`로 통합. 값이 설정되면 의도 분석에 우선 사용

---

## 5. 코드 변경 대상

### 5.1 Configuration/ServerConfig.cs
- `LlmSection`에 `GeneralModel` 속성 추가

### 5.2 McpServer/IntentResolver.cs
- `AnalyzeIntentAsync`: `LlmRequest.Model`에 `GeneralModel` 설정 (IntentModel 우선)
- `GenerateChatResponseAsync`: `LlmRequest.Model`에 `GeneralModel` 설정
- `GeneratePlanAsync`: `LlmRequest.Model`에 `GeneralModel` 설정
- `GenerateSummaryAsync`: `LlmRequest.Model`에 `GeneralModel` 설정

### 5.3 appsettings.json / appsettings.Development.json
- `Llm.GeneralModel` 항목 추가

### 5.4 영향 없는 파일 (변경 불필요)
- `OllamaConnector.cs` — 기존 로직 유지
- `CodeToolBase.cs` — Model=null → DefaultModel 자동 사용
- 개별 Code Tool 클래스 — CodeToolBase 상속, 변경 없음
- VSIX — 서버 내부 라우팅이므로 클라이언트 변경 없음

---

## 6. 메모리 고려사항

두 모델을 동시에 Ollama에 로드하면 VRAM/RAM 사용량이 증가한다.

| 시나리오 | 예상 메모리 |
|----------|------------|
| gemma4 (e4b) + qwen2.5-coder:7b | ~15GB |
| gemma4:e2b + qwen2.5-coder:7b | ~12GB |
| gemma4:26b + qwen2.5-coder:7b | ~23GB |

Ollama는 자동으로 최근 사용 모델을 메모리에 유지하므로, 모델 전환 시 첫 호출에 로딩 지연(10-30초)이 발생할 수 있다. `OLLAMA_KEEP_ALIVE` 환경변수로 모델 메모리 유지 시간을 조정할 수 있다.

```powershell
# 모델을 5분간 메모리에 유지 (기본값)
$env:OLLAMA_KEEP_ALIVE = "5m"

# 모델을 항상 메모리에 유지 (VRAM 충분 시)
$env:OLLAMA_KEEP_ALIVE = "-1"
```

---

## 7. 테스트 계획

1. **단일 모델 폴백**: `GeneralModel = null` 시 기존 동작 유지 확인
2. **의도 분석**: gemma4로 의도 분석 정확도 확인
3. **일반 대화**: gemma4로 한국어 대화 품질 확인
4. **코드 도구**: qwen2.5-coder:7b로 코드 변환 품질 유지 확인
5. **모델 전환 지연**: 연속 요청 시 모델 전환 시간 측정

---

## 8. 계약 영향 (contracts.md)

- §5 Config 스키마: `LlmSection`에 `GeneralModel` 필드 추가
- 기존 계약에는 모델 라우팅 세부사항이 없으므로 새 계약 불필요
