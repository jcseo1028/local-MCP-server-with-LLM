using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMcpServer.Configuration;
using Microsoft.Extensions.Options;
using System.Net;

namespace LocalMcpServer.LlmConnector;

/// <summary>
/// Ollama /api/embed 호출을 담당한다.
/// </summary>
public sealed class EmbeddingConnector
{
    private readonly HttpClient _http;
    private readonly ServerConfig _config;
    private readonly ILogger<EmbeddingConnector> _logger;

    public EmbeddingConnector(HttpClient http, IOptions<ServerConfig> config, ILogger<EmbeddingConnector> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<float[]> EmbedAsync(string text, string? model = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var selectedModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : !string.IsNullOrWhiteSpace(_config.Rag.EmbeddingModel)
                ? _config.Rag.EmbeddingModel
                : "nomic-embed-text";

        var endpoint = _config.Llm.Endpoint.TrimEnd('/');
        var request = new OllamaEmbedRequest
        {
            Model = selectedModel,
            Input = text
        };

        var json = JsonSerializer.Serialize(request);
        _logger.LogInformation("Embedding 요청: model={Model}, length={Length}", selectedModel, text.Length);

        var vector = await TryEmbedWithEndpointAsync($"{endpoint}/api/embed", json, ct);
        if (vector.Length == 0)
        {
            _logger.LogWarning("/api/embed 결과 없음, /api/embeddings 폴백 시도");
            vector = await TryEmbedWithEndpointAsync($"{endpoint}/api/embeddings", json, ct);
        }

        if (vector.Length == 0)
            throw new InvalidOperationException("Embedding 벡터를 생성하지 못했습니다.");

        _logger.LogInformation("Embedding 응답: dim={Dimension}", vector.Length);
        return vector;
    }

    private async Task<float[]> TryEmbedWithEndpointAsync(string url, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var embedResponse = JsonSerializer.Deserialize(responseJson, EmbedJsonContext.Default.OllamaEmbedResponse);
        if (embedResponse is not null)
        {
            if (embedResponse.Embeddings is { Count: > 0 })
                return embedResponse.Embeddings[0];
            if (embedResponse.Embedding is { Length: > 0 })
                return embedResponse.Embedding;
        }

        return [];
    }
}

internal sealed class OllamaEmbedRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("input")]
    public required string Input { get; set; }
}

internal sealed class OllamaEmbedResponse
{
    [JsonPropertyName("embeddings")]
    public List<float[]>? Embeddings { get; set; }

    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

[JsonSerializable(typeof(OllamaEmbedResponse))]
internal partial class EmbedJsonContext : JsonSerializerContext
{
}
