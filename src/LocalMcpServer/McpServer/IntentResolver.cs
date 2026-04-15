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
