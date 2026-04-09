using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMcpServer.ToolRegistry;

namespace LocalMcpServer.McpServer;

/// <summary>
/// MCP 프로토콜(JSON-RPC 2.0) SSE 전송 방식 서버 + 동기 REST API.
/// contracts.md §1, §2, §8 준수.
/// 
/// SSE 엔드포인트 (VS 2022 Agent mode):
///   GET  /sse         → 클라이언트가 SSE 스트림에 연결
///   POST /message     → 클라이언트가 JSON-RPC 요청 전송
/// 
/// Direct REST 엔드포인트 (오프라인 CLI):
///   GET  /api/tools/list → 도구 목록 조회
///   POST /api/tools/call → 도구 직접 실행
/// </summary>
public static class McpEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void MapMcpEndpoints(this WebApplication app)
    {
        var sessions = new ConcurrentDictionary<string, SseSession>();

        // SSE 연결 엔드포인트
        app.MapGet("/sse", async (HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var sessionId = Guid.NewGuid().ToString("N");
            var session = new SseSession(sessionId);
            sessions[sessionId] = session;

            var logger = ctx.RequestServices.GetRequiredService<ILogger<SseSession>>();
            logger.LogInformation("SSE 세션 연결: {SessionId}", sessionId);

            // endpoint 이벤트 전송 — 클라이언트에게 메시지 전송 URL 알림
            var messageUrl = $"/message?sessionId={sessionId}";
            await WriteSseEventAsync(ctx.Response, "endpoint", messageUrl);
            await ctx.Response.Body.FlushAsync(ct);

            // 세션이 살아있는 동안 응답 대기
            try
            {
                while (!ct.IsCancellationRequested && !session.Closed)
                {
                    if (session.ResponseQueue.TryDequeue(out var responseJson))
                    {
                        await WriteSseEventAsync(ctx.Response, "message", responseJson);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                    else
                    {
                        await Task.Delay(50, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                sessions.TryRemove(sessionId, out _);
                logger.LogInformation("SSE 세션 종료: {SessionId}", sessionId);
            }
        });

        // 메시지 수신 엔드포인트
        app.MapPost("/message", async (HttpContext ctx, CancellationToken ct) =>
        {
            var sessionId = ctx.Request.Query["sessionId"].ToString();
            if (string.IsNullOrEmpty(sessionId) || !sessions.TryGetValue(sessionId, out var session))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Invalid session", ct);
                return;
            }

            var logger = ctx.RequestServices.GetRequiredService<ILogger<SseSession>>();
            var registry = ctx.RequestServices.GetRequiredService<ToolRegistryService>();

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            logger.LogInformation("MCP 요청 수신: {Body}", body);

            var response = await HandleJsonRpcAsync(body, registry, logger, ct);

            if (response is not null)
            {
                logger.LogInformation("MCP 응답 전송: {Response}", response);
                session.ResponseQueue.Enqueue(response);
            }

            ctx.Response.StatusCode = 202;
            await ctx.Response.WriteAsync("Accepted", ct);
        });

        // --- Direct REST 엔드포인트 (contracts.md §8) ---

        // 도구 목록 조회
        app.MapGet("/api/tools/list", (ToolRegistryService registry) =>
        {
            var tools = registry.ListTools().Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema
            }).ToArray();

            return Results.Json(new { tools }, s_jsonOptions);
        });

        // 도구 직접 실행
        app.MapPost("/api/tools/call", async (HttpContext ctx, ToolRegistryService registry, CancellationToken ct) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<SseSession>>();

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);

            try
            {
                var request = JsonSerializer.Deserialize<JsonElement>(body);

                var toolName = request.GetProperty("name").GetString()!;
                var arguments = new Dictionary<string, object?>();

                if (request.TryGetProperty("arguments", out var argsEl))
                {
                    foreach (var prop in argsEl.EnumerateObject())
                    {
                        arguments[prop.Name] = prop.Value.Clone();
                    }
                }

                var tool = registry.GetTool(toolName);
                if (tool is null)
                {
                    return Results.Json(new { content = Array.Empty<object>(), error = $"Unknown tool: {toolName}" }, s_jsonOptions);
                }

                logger.LogInformation("REST 도구 호출: {ToolName}", toolName);

                // 로컬 LLM 추론은 수 분이 걸릴 수 있으므로, 클라이언트 취소(ct)와 별도로
                // 5분 타임아웃 CancellationToken을 사용한다.
                using var llmCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var result = await tool.ExecuteAsync(arguments, llmCts.Token);

                return Results.Json(new
                {
                    content = result.Content.Select(c => new { type = c.Type, text = c.Text }).ToArray(),
                    error = (string?)null
                }, s_jsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "REST 도구 실행 오류");
                return Results.Json(new { content = Array.Empty<object>(), error = ex.Message }, s_jsonOptions);
            }
        });
    }

    private static async Task<string?> HandleJsonRpcAsync(
        string body,
        ToolRegistryService registry,
        ILogger logger,
        CancellationToken ct)
    {
        JsonElement request;
        try
        {
            request = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return SerializeResponse(null, error: new { code = -32700, message = "Parse error" });
        }

        var id = request.TryGetProperty("id", out var idProp) ? (object?)idProp.Clone() : null;
        var method = request.TryGetProperty("method", out var methodProp) ? methodProp.GetString() : null;

        // notification (id 없음) 은 응답 불필요
        if (id is null && method == "notifications/initialized")
            return null;

        logger.LogInformation("MCP 메서드: {Method}, id={Id}", method, id);

        try
        {
            return method switch
            {
                "initialize" => HandleInitialize(id),
                "tools/list" => HandleToolsList(id, registry),
                "tools/call" => await HandleToolsCallAsync(id, request, registry, ct),
                "notifications/initialized" => null,
                _ => SerializeResponse(id, error: new { code = -32601, message = $"Method not found: {method}" })
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "도구 실행 오류");
            return SerializeResponse(id, error: new { code = -32603, message = ex.Message });
        }
    }

    private static string HandleInitialize(object? id)
    {
        var result = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { listChanged = false }
            },
            serverInfo = new
            {
                name = "local-mcp-server",
                version = "0.1.0"
            }
        };
        return SerializeResponse(id, result: result);
    }

    private static string HandleToolsList(object? id, ToolRegistryService registry)
    {
        var tools = registry.ListTools().Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema
        }).ToArray();

        return SerializeResponse(id, result: new { tools });
    }

    private static async Task<string> HandleToolsCallAsync(
        object? id,
        JsonElement request,
        ToolRegistryService registry,
        CancellationToken ct)
    {
        var paramsEl = request.GetProperty("params");
        var toolName = paramsEl.GetProperty("name").GetString()!;
        var arguments = new Dictionary<string, object?>();

        if (paramsEl.TryGetProperty("arguments", out var argsEl))
        {
            foreach (var prop in argsEl.EnumerateObject())
            {
                arguments[prop.Name] = prop.Value.Clone();
            }
        }

        var tool = registry.GetTool(toolName);
        if (tool is null)
        {
            return SerializeResponse(id, error: new { code = -32602, message = $"Unknown tool: {toolName}" });
        }

        var result = await tool.ExecuteAsync(arguments, ct);

        return SerializeResponse(id, result: new
        {
            content = result.Content.Select(c => new { type = c.Type, text = c.Text }).ToArray()
        });
    }

    private static string SerializeResponse(object? id, object? result = null, object? error = null)
    {
        var response = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id
        };

        if (error is not null)
            response["error"] = error;
        else
            response["result"] = result;

        return JsonSerializer.Serialize(response, s_jsonOptions);
    }

    private static async Task WriteSseEventAsync(HttpResponse response, string eventType, string data)
    {
        await response.WriteAsync($"event: {eventType}\ndata: {data}\n\n");
    }
}

internal sealed class SseSession(string id)
{
    public string Id { get; } = id;
    public ConcurrentQueue<string> ResponseQueue { get; } = new();
    public bool Closed { get; set; }
}
