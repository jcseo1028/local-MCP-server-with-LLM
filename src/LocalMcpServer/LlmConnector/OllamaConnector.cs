using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMcpServer.Configuration;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.LlmConnector;

/// <summary>
/// Ollama REST API를 통해 로컬 LLM과 통신한다.
/// contracts.md §3 (Tool Registry → LLM Connector) 구현.
/// </summary>
public sealed class OllamaConnector
{
    private readonly HttpClient _http;
    private readonly ServerConfig _config;
    private readonly ILogger<OllamaConnector> _logger;

    public OllamaConnector(HttpClient http, IOptions<ServerConfig> config, ILogger<OllamaConnector> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// LLMRequest를 받아 Ollama /api/chat 를 호출하고 LLMResponse를 반환한다.
    /// </summary>
    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = !string.IsNullOrEmpty(request.Model) ? request.Model
            : !string.IsNullOrEmpty(_config.Llm.SummaryModel) ? _config.Llm.SummaryModel
            : !string.IsNullOrEmpty(_config.Llm.DefaultModel) ? _config.Llm.DefaultModel
            : "qwen2.5-coder:7b";

        var body = new OllamaChatRequest
        {
            Model = model,
            Messages = [new OllamaChatMessage { Role = "user", Content = request.Prompt }],
            Stream = false,
            Options = new OllamaOptions
            {
                Temperature = request.Options.Temperature,
                NumPredict = request.Options.MaxTokens,
                NumCtx = request.Options.NumCtx > 0 ? request.Options.NumCtx : 2048
            }
        };

        var json = JsonSerializer.Serialize(body, OllamaJsonContext.Default.OllamaChatRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var endpoint = _config.Llm.Endpoint.TrimEnd('/');
        var url = $"{endpoint}/api/chat";
        _logger.LogInformation("Ollama 요청: POST {Url}, model={Model}, prompt 길이={Length}", url, model, request.Prompt.Length);

        var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var ollamaResponse = JsonSerializer.Deserialize(responseJson, OllamaJsonContext.Default.OllamaChatResponse);

        if (ollamaResponse is null)
            throw new InvalidOperationException("Ollama 응답 파싱 실패");

        var text = ollamaResponse.Message?.Content ?? string.Empty;
        _logger.LogInformation("Ollama 응답: 길이={Length}", text.Length);

        return new LlmResponse
        {
            Text = text,
            Usage = ollamaResponse.PromptEvalCount is not null
                ? new LlmUsage
                {
                    PromptTokens = ollamaResponse.PromptEvalCount,
                    CompletionTokens = ollamaResponse.EvalCount
                }
                : null
        };
    }

    /// <summary>
    /// Ollama 연결 상태와 모델 가용성을 확인한다.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var endpoint = _config.Llm.Endpoint.TrimEnd('/');
            var response = await _http.GetAsync($"{endpoint}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama 연결 실패");
            return false;
        }
    }
}

// --- Ollama /api/chat 내부 모델 ---

internal sealed class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("messages")]
    public required List<OllamaChatMessage> Messages { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptions? Options { get; set; }
}

internal sealed class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal sealed class OllamaOptions
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("num_predict")]
    public int NumPredict { get; set; }

    [JsonPropertyName("num_ctx")]
    public int NumCtx { get; set; }
}

internal sealed class OllamaChatResponse
{
    [JsonPropertyName("message")]
    public OllamaChatMessage? Message { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }
}

[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatResponse))]
[JsonSerializable(typeof(OllamaChatMessage))]
[JsonSerializable(typeof(List<OllamaChatMessage>))]
internal partial class OllamaJsonContext : JsonSerializerContext
{
}
