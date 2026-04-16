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
        "add_comments", "refactor_current_code", "fix_code_issues"
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

            // 프롬프트 로드 및 변수 치환
            var prompt = await _promptLoader.LoadAndRenderAsync("intent_analysis", new Dictionary<string, string>
            {
                ["message"] = message,
                ["tools"] = toolsJson,
                ["language"] = language ?? "unknown"
            }, ct);

            var intentModel = !string.IsNullOrEmpty(_config.Chat.IntentModel)
                ? _config.Chat.IntentModel
                : null; // null이면 OllamaConnector가 defaultModel 사용

            var llmRequest = new LlmRequest
            {
                Prompt = prompt,
                Model = intentModel,
                Options = new LlmOptions
                {
                    Temperature = 0.1,
                    MaxTokens = 200,
                    NumCtx = 2048
                }
            };

            var llmResponse = await _llm.GenerateAsync(llmRequest, ct);

            // LLM 응답에서 JSON 파싱
            return ParseIntentResponse(llmResponse.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "의도 분석 실패, 일반 대화로 처리");
            return new IntentResult
            {
                ToolName = null,
                Confidence = 0.0,
                Description = "의도 분석 실패"
            };
        }
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

        var llmRequest = new LlmRequest
        {
            Prompt = prompt,
            Options = new LlmOptions
            {
                Temperature = 0.5,
                MaxTokens = 1024,
                NumCtx = 4096
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
    public async Task<List<string>> GeneratePlanAsync(
        IntentResult intent, string message, string? code, string? language, CancellationToken ct)
    {
        try
        {
            var prompt = await _promptLoader.LoadAndRenderAsync("planning", new Dictionary<string, string>
            {
                ["message"] = message,
                ["intent_tool"] = intent.ToolName ?? "general_chat",
                ["intent_description"] = intent.Description,
                ["code"] = code ?? "(코드 없음)",
                ["language"] = language ?? "unknown"
            }, ct);

            var llmRequest = new LlmRequest
            {
                Prompt = prompt,
                Options = new LlmOptions
                {
                    Temperature = 0.2,
                    MaxTokens = 512,
                    NumCtx = 2048
                }
            };

            var response = await _llm.GenerateAsync(llmRequest, ct);
            return ParsePlanResponse(response.Text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "계획 생성 실패");
            return [intent.Description.Length > 0 ? intent.Description : message];
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
                ["tool_result"] = toolResult ?? "(결과 없음)",
                ["approved"] = approved?.ToString() ?? "N/A"
            }, ct);

            var llmRequest = new LlmRequest
            {
                Prompt = prompt,
                Options = new LlmOptions
                {
                    Temperature = 0.3,
                    MaxTokens = 512,
                    NumCtx = 2048
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
        return items.Count > 5 ? items.Take(5).ToList() : items.Count > 0 ? items : [text.Trim()];
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
                    jsonText = text[contentStart..fenceEnd].Trim();
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
}

public sealed class IntentResult
{
    public string? ToolName { get; set; }
    public double Confidence { get; set; }
    public string Description { get; set; } = "";
}
