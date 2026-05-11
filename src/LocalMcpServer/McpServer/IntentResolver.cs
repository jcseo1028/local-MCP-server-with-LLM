using System.Text.Json;
using LocalMcpServer.Configuration;
using LocalMcpServer.LlmConnector;
using LocalMcpServer.ToolRegistry;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.McpServer;

/// <summary>
/// 사용자 메시지를 분석하여 적절한 도구를 선택한다.
/// pipeline.md Chat Pipeline §4 구현.
/// MCP Server에서 LLM Connector를 직접 호출 (라우팅 결정 목적).
/// </summary>
public sealed class IntentResolver
{
    private readonly OllamaConnector _llm;
    private readonly ToolRegistryService _registry;
    private readonly PromptTemplateLoader _promptLoader;
    private readonly ServerConfig _config;
    private readonly ILogger<IntentResolver> _logger;

    // 코드 수정 도구 (결과를 에디터에 적용하기 위해 승인이 필요한 도구)
    private static readonly HashSet<string> EditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "add_comments", "refactor_current_code", "fix_code_issues", "organize_imports"
    };

    public IntentResolver(
        OllamaConnector llm,
        ToolRegistryService registry,
        PromptTemplateLoader promptLoader,
        IOptions<ServerConfig> config,
        ILogger<IntentResolver> logger)
    {
        _llm = llm;
        _registry = registry;
        _promptLoader = promptLoader;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// 사용자 메시지를 분석하여 의도를 파악한다.
    /// </summary>
    public async Task<IntentResult> AnalyzeIntentAsync(string message, string? language, CancellationToken ct)
    {
        try
        {
            // 도구 목록을 JSON으로 구성
            var tools = _registry.ListTools();
            var toolsJson = JsonSerializer.Serialize(tools.Select(t => new
            {
                name = t.Name,
                description = t.Description
            }));

            // 프롬프트 로드 및 변수 치환 — system instruction으로 사용
            var systemPrompt = await _promptLoader.LoadAndRenderAsync("intent_analysis", new Dictionary<string, string>
            {
                ["message"] = message,
                ["tools"] = toolsJson,
                ["language"] = language ?? "unknown"
            }, ct);

            var intentModel = ResolveIntentPlanModel();

            var llmRequest = new LlmRequest
            {
                SystemPrompt = systemPrompt,
                Prompt = $"사용자 메시지: \"{message}\"\n위 지시사항에 따라 JSON만 출력하세요.",
                Model = intentModel,
                Options = new LlmOptions
                {
                    Temperature = 0.1,
                    MaxTokens = 256,
                    NumCtx = 4096,
                    ResponseFormat = "json"
                }
            };

            var llmResponse = await _llm.GenerateWithRetryAsync(llmRequest, maxRetries: 2, ct);

            // LLM 응답에서 JSON 파싱 — 빈 응답이면 키워드 fallback
            if (string.IsNullOrWhiteSpace(llmResponse.Text))
            {
                _logger.LogWarning("의도 분석 LLM 빈 응답, 키워드 fallback 사용");
                var fb = FallbackIntentByKeyword(message);
                fb.RawLlmResponse = "(빈 응답)";
                fb.FallbackUsed = true;
                return fb;
            }

            var result = ParseIntentResponse(llmResponse.Text);
            result.RawLlmResponse = llmResponse.Text;

            // LLM이 응답했지만 파싱 실패(ToolName=null, Confidence=0)이면 키워드 fallback
            if (result.ToolName is null && result.Confidence == 0.0)
            {
                var fallback = FallbackIntentByKeyword(message);
                if (fallback.ToolName is not null)
                {
                    _logger.LogInformation("LLM 파싱 실패 → 키워드 fallback 적용: {Tool}", fallback.ToolName);
                    fallback.RawLlmResponse = llmResponse.Text;
                    fallback.FallbackUsed = true;
                    return fallback;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "의도 분석 실패, 키워드 fallback 사용");
            var fb = FallbackIntentByKeyword(message);
            fb.RawLlmResponse = ex.Message;
            fb.FallbackUsed = true;
            return fb;
        }
    }

    /// <summary>
    /// LLM 응답이 비어 있거나 실패할 때 메시지 키워드로 의도를 추론한다.
    /// </summary>
    private IntentResult FallbackIntentByKeyword(string message)
    {
        var msg = message.ToLowerInvariant();

        // 복합 조건 매핑 — (필수 키워드 조합, toolName, description)
        // AllOf: 모든 키워드가 포함되어야 매칭 (AND 조건, 더 구체적)
        // AnyOf: 하나라도 포함되면 매칭 (OR 조건)
        var mappings = new (string[]? allOf, string[] anyOf, string toolName, string description, double confidence)[]
        {
            // 프로젝트 구조 분석 — "프로젝트" + ("구조"|"아키텍처"|"모듈"|"디렉터리") 조합 필요
            (["프로젝트"], ["구조", "아키텍처", "모듈", "디렉터리 구성"], "analyze_project_structure", "프로젝트 구조 분석 요청", 0.6),

            // 코드 분석/요약 — "코드" + ("분석"|"설명"|"요약") 또는 단독 "요약"/"설명해"
            (["코드"], ["분석", "설명", "요약", "리뷰"], "summarize_current_code", "코드 분석/요약 요청", 0.6),
            (null, ["요약", "설명해", "알려줘"], "summarize_current_code", "현재 코드 요약 요청", 0.5),

            // 나머지는 기존 단일 키워드 매칭
            (null, ["주석", "코멘트", "comment", "문서화", "doc"], "add_comments", "코드 주석 추가 요청", 0.5),            // using/import 전용 정리 — refactor_current_code보다 우선 매핑 (SC-5)
            (["using"], ["정리", "추가", "삭제", "제거", "organize"], "organize_imports", "using 정리 요청", 0.8),
            (["import"], ["정리", "추가", "삭제", "제거", "organize"], "organize_imports", "import 정리 요청", 0.8),
            (null, ["네임스페이스 정리", "import 정리", "using 정리", "organize import"], "organize_imports", "import/using 정리 요청", 0.7),            (null, ["리팩터", "리팩토링", "refactor", "개선", "정리"], "refactor_current_code", "코드 리팩터링 요청", 0.5),
            (null, ["버그", "오류", "fix", "수정", "고쳐"], "fix_code_issues", "코드 수정 요청", 0.5),
            (null, ["검색", "찾아", "search", "어디"], "search_project_code", "코드 검색 요청", 0.5),
            (null, ["에러", "로그", "error", "스택", "exception"], "suggest_fix_from_error_log", "에러 로그 기반 수정 요청", 0.5),
        };

        foreach (var (allOf, anyOf, toolName, description, confidence) in mappings)
        {
            // allOf가 있으면 모든 키워드가 포함되어야 함
            if (allOf is not null && !allOf.All(k => msg.Contains(k)))
                continue;

            // anyOf 중 하나라도 매칭되어야 함
            if (!anyOf.Any(k => msg.Contains(k)))
                continue;

            _logger.LogInformation("키워드 fallback 매칭: tool={Tool}, message=\"{Message}\"", toolName, message);
            return new IntentResult
            {
                ToolName = toolName,
                Confidence = confidence,
                Description = description
            };
        }

        return new IntentResult
        {
            ToolName = null,
            Confidence = 0.1,
            Description = "일반 대화 (키워드 fallback)"
        };
    }

    /// <summary>
    /// 일반 대화 응답을 생성한다 (도구 매핑이 안 된 경우).
    /// </summary>
    public async Task<string> GenerateChatResponseAsync(
        string message, string? code, string? language, string history, CancellationToken ct)
    {
        var prompt = await _promptLoader.LoadAndRenderAsync("general_chat", new Dictionary<string, string>
        {
            ["message"] = message,
            ["code"] = code ?? "(코드 없음)",
            ["language"] = language ?? "unknown",
            ["history"] = history
        }, ct);

        var generalModel = ResolveChatModel();

        var llmRequest = new LlmRequest
        {
            Prompt = prompt,
            Model = generalModel,
            Options = new LlmOptions
            {
                Temperature = 0.5,
                MaxTokens = 4096,
                NumCtx = 16384
            }
        };

        var response = await _llm.GenerateAsync(llmRequest, ct);
        return response.Text;
    }

    /// <summary>도구가 코드 수정 도구인지 확인한다.</summary>
    public static bool IsEditTool(string toolName) => EditTools.Contains(toolName);

    /// <summary>
    /// 의도와 메시지를 기반으로 실행 계획(2-5 단계)을 생성한다.
    /// </summary>
    public async Task<(List<string> Items, string? RawResponse)> GeneratePlanAsync(
        IntentResult intent, string message, string? code, string? language, CancellationToken ct)
    {
        try
        {
            var systemPrompt = await _promptLoader.LoadAndRenderAsync("planning", new Dictionary<string, string>
            {
                ["message"] = message,
                ["intent_tool"] = intent.ToolName ?? "general_chat",
                ["intent_description"] = intent.Description,
                ["language"] = language ?? "unknown"
            }, ct);

            var llmRequest = new LlmRequest
            {
                SystemPrompt = systemPrompt,
                Prompt = $"사용자 요청: \"{message}\"\n의도: {intent.Description}\n위 규칙에 따라 번호 목록만 출력하세요.",
                Model = ResolveIntentPlanModel(),
                Options = new LlmOptions
                {
                    Temperature = 0.2,
                    MaxTokens = 512,
                    NumCtx = 4096
                }
            };

            var response = await _llm.GenerateWithRetryAsync(llmRequest, maxRetries: 2, ct);
            return (ParsePlanResponse(response.Text), response.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "계획 생성 실패");
            return ([intent.Description.Length > 0 ? intent.Description : message], null);
        }
    }

    /// <summary>
    /// Run 결과를 요약한다.
    /// </summary>
    public async Task<string> GenerateSummaryAsync(
        string message, IntentResult intent, List<string> planItems,
        string? toolResult, bool? approved, CancellationToken ct)
    {
        try
        {
            var prompt = await _promptLoader.LoadAndRenderAsync("run_summary", new Dictionary<string, string>
            {
                ["message"] = message,
                ["intent_tool"] = intent.ToolName ?? "general_chat",
                ["plan_items"] = string.Join("\n", planItems.Select((p, i) => $"{i + 1}. {p}")),
                ["tool_result"] = TruncateForSummary(toolResult, 6000),
                ["approved"] = approved?.ToString() ?? "N/A"
            }, ct);

            var llmRequest = new LlmRequest
            {
                Prompt = prompt,
                Model = ResolveSummaryModel(),
                Options = new LlmOptions
                {
                    Temperature = 0.3,
                    MaxTokens = 2048,
                    NumCtx = 16384
                }
            };

            var response = await _llm.GenerateAsync(llmRequest, ct);
            return response.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "요약 생성 실패");
            return approved == true ? "작업이 완료되었습니다." : "작업이 취소 또는 실패했습니다.";
        }
    }

    private string ResolveIntentPlanModel()
    {
        if (!string.IsNullOrWhiteSpace(_config.Chat.IntentModel))
            return _config.Chat.IntentModel!;
        if (!string.IsNullOrWhiteSpace(_config.Llm.GeneralModel))
            return _config.Llm.GeneralModel!;
        return _config.Llm.DefaultModel;
    }

    private string ResolveChatModel()
    {
        if (!string.IsNullOrWhiteSpace(_config.Chat.ChatModel))
            return _config.Chat.ChatModel!;
        if (!string.IsNullOrWhiteSpace(_config.Llm.GeneralModel))
            return _config.Llm.GeneralModel!;
        return _config.Llm.DefaultModel;
    }

    private string ResolveSummaryModel()
    {
        if (!string.IsNullOrWhiteSpace(_config.Llm.SummaryModel))
            return _config.Llm.SummaryModel!;
        return ResolveChatModel();
    }

    private List<string> ParsePlanResponse(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = new List<string>();

        foreach (var line in lines)
        {
            // "1. xxx" 또는 "- xxx" 형식 파싱
            var trimmed = line.TrimStart('-', '*', ' ');
            if (trimmed.Length > 0 && char.IsDigit(line[0]))
            {
                var dotIdx = trimmed.IndexOf('.');
                if (dotIdx >= 0 && dotIdx < 4)
                    trimmed = trimmed[(dotIdx + 1)..].TrimStart();
            }

            if (trimmed.Length > 0)
                items.Add(trimmed);
        }

        // 최대 5개로 제한
        if (items.Count > 5)
            return items.Take(5).ToList();

        if (items.Count > 0)
            return items;

        // LLM이 빈 문자열 또는 공백만 반환한 경우 빈 계획으로 처리
        var fallback = text.Trim();
        return string.IsNullOrWhiteSpace(fallback) ? [] : [fallback];
    }

    private IntentResult ParseIntentResponse(string text)
    {
        try
        {
            // LLM 응답에서 JSON 블록 추출
            var jsonText = text;

            // 코드 펜스 안에 있는 경우
            var fenceStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (fenceStart >= 0)
            {
                var contentStart = text.IndexOf('\n', fenceStart) + 1;
                var fenceEnd = text.IndexOf("```", contentStart, StringComparison.Ordinal);
                if (fenceEnd > contentStart)
                {
                    jsonText = text[contentStart..fenceEnd].Trim();
                }
                else
                {
                    // 펜스가 닫히지 않은 경우 — 중괄호 추출로 fallback
                    var braceStart = text.IndexOf('{');
                    var braceEnd = text.LastIndexOf('}');
                    if (braceStart >= 0 && braceEnd > braceStart)
                        jsonText = text[braceStart..(braceEnd + 1)];
                }
            }
            else
            {
                // 중괄호만 추출
                var braceStart = text.IndexOf('{');
                var braceEnd = text.LastIndexOf('}');
                if (braceStart >= 0 && braceEnd > braceStart)
                    jsonText = text[braceStart..(braceEnd + 1)];
            }

            var parsed = JsonSerializer.Deserialize<JsonElement>(jsonText);

            var toolName = parsed.TryGetProperty("toolName", out var tn) && tn.ValueKind != JsonValueKind.Null
                ? tn.GetString()
                : null;

            var confidence = parsed.TryGetProperty("confidence", out var cf)
                ? cf.GetDouble()
                : 0.0;

            var description = parsed.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? ""
                : "";

            _logger.LogInformation("의도 분석 결과: tool={Tool}, confidence={Confidence}, desc={Desc}",
                toolName ?? "null", confidence, description);

            return new IntentResult
            {
                ToolName = toolName,
                Confidence = confidence,
                Description = description
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "의도 분석 JSON 파싱 실패: {Text}", text);
            return new IntentResult
            {
                ToolName = null,
                Confidence = 0.0,
                Description = "파싱 실패"
            };
        }
    }

    /// <summary>
    /// 요약 프롬프트에 넣기 전에 tool_result를 적절한 길이로 절단한다.
    /// 코드 수정 결과가 매우 길 수 있으므로 컨텍스트 윈도우를 보호한다.
    /// </summary>
    private static string TruncateForSummary(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "(결과 없음)";

        if (text.Length <= maxChars)
            return text;

        return text[..maxChars] + $"\n\n... (총 {text.Length}자 중 {maxChars}자까지 표시)";
    }
}

public sealed class IntentResult
{
    public string? ToolName { get; set; }
    public double Confidence { get; set; }
    public string Description { get; set; } = "";
    public string? RawLlmResponse { get; set; }
    public bool FallbackUsed { get; set; }
}
