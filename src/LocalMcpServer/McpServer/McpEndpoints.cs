using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMcpServer.ToolRegistry;

namespace LocalMcpServer.McpServer;

/// <summary>
/// MCP 프로토콜(JSON-RPC 2.0) SSE 전송 방식 서버 + 동기 REST API + Run API.
/// contracts.md §1, §2, §8, §11 준수.
/// 
/// SSE 엔드포인트 (VS 2022 Agent mode):
///   GET  /sse         → 클라이언트가 SSE 스트림에 연결
///   POST /message     → 클라이언트가 JSON-RPC 요청 전송
/// 
/// Direct REST 엔드포인트 (오프라인 CLI):
///   GET  /api/tools/list → 도구 목록 조회
///   POST /api/tools/call → 도구 직접 실행
/// 
/// Run API 엔드포인트 (v2.1 오케스트레이션):
///   POST /api/chat/runs            → Run 시작
///   GET  /api/chat/runs/{runId}    → Run 상태 폴링
///   POST /api/chat/runs/{runId}/approval      → 승인/거부
///   POST /api/chat/runs/{runId}/client-result  → 빌드/테스트 결과 수신
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

        // --- Chat REST 엔드포인트 (contracts.md §9, §10) ---

        // 채팅 메시지 처리 (의도 분석 → 도구 실행)
        app.MapPost("/api/chat", async (HttpContext ctx, CancellationToken ct) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<IntentResolver>>();
            var intentResolver = ctx.RequestServices.GetRequiredService<IntentResolver>();
            var conversationStore = ctx.RequestServices.GetRequiredService<IConversationStore>();
            var registry = ctx.RequestServices.GetRequiredService<ToolRegistryService>();

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);

            try
            {
                var request = JsonSerializer.Deserialize<JsonElement>(body);

                var message = request.GetProperty("message").GetString() ?? "";
                var code = request.TryGetProperty("code", out var codeProp) && codeProp.ValueKind != JsonValueKind.Null
                    ? codeProp.GetString() : null;
                var language = request.TryGetProperty("language", out var langProp) && langProp.ValueKind != JsonValueKind.Null
                    ? langProp.GetString() : null;
                var selectionOnly = request.TryGetProperty("selectionOnly", out var selProp) && selProp.GetBoolean();
                var conversationId = request.TryGetProperty("conversationId", out var convProp) && convProp.ValueKind != JsonValueKind.Null
                    ? convProp.GetString() : null;

                // 대화 상태 가져오기/생성
                var conversation = conversationStore.GetOrCreate(conversationId);
                conversation.AddMessage("user", message);

                // 만료된 대화 정리 (비동기적으로)
                conversationStore.CleanupExpired();

                logger.LogInformation("Chat 요청: conversationId={ConvId}, message={Msg}",
                    conversation.ConversationId, message.Length > 50 ? message[..50] + "..." : message);

                // 의도 분석
                using var llmCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var intent = await intentResolver.AnalyzeIntentAsync(message, language, llmCts.Token);

                string resultText;
                object? codeChange = null;
                var requiresApproval = false;

                if (intent.ToolName is not null && intent.Confidence >= 0.5)
                {
                    // 도구 실행
                    var tool = registry.GetTool(intent.ToolName);
                    if (tool is not null)
                    {
                        var arguments = new Dictionary<string, object?>
                        {
                            ["code"] = code ?? "",
                            ["language"] = language ?? ""
                        };

                        var toolResult = await tool.ExecuteAsync(arguments, llmCts.Token);
                        resultText = toolResult.Content.FirstOrDefault()?.Text ?? "(결과 없음)";

                        // 코드 수정 도구면 승인 필요
                        if (IntentResolver.IsEditTool(intent.ToolName) && code is not null)
                        {
                            var modifiedCode = ExtractCodeFromResult(resultText);
                            codeChange = new
                            {
                                original = code,
                                modified = modifiedCode ?? resultText,
                                toolName = intent.ToolName
                            };
                            requiresApproval = true;

                            conversation.PendingChange = new PendingCodeChange
                            {
                                Original = code,
                                Modified = modifiedCode ?? resultText,
                                ToolName = intent.ToolName
                            };
                        }
                    }
                    else
                    {
                        resultText = $"도구 '{intent.ToolName}'을(를) 찾을 수 없습니다.";
                    }
                }
                else
                {
                    // 일반 대화 응답
                    var history = conversation.FormatHistoryForPrompt();
                    resultText = await intentResolver.GenerateChatResponseAsync(
                        message, code, language, history, llmCts.Token);
                }

                conversation.AddMessage("assistant", resultText);

                return Results.Json(new
                {
                    conversationId = conversation.ConversationId,
                    intent = new
                    {
                        toolName = intent.ToolName,
                        confidence = intent.Confidence,
                        description = intent.Description
                    },
                    result = resultText,
                    codeChange,
                    requiresApproval
                }, s_jsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat 처리 오류");
                return Results.Json(new
                {
                    conversationId = (string?)null,
                    intent = new { toolName = (string?)null, confidence = 0.0, description = "오류 발생" },
                    result = "오류가 발생했습니다: " + ex.Message,
                    codeChange = (object?)null,
                    requiresApproval = false
                }, s_jsonOptions);
            }
        });

        // 코드 변경 승인/거부
        app.MapPost("/api/chat/approve", async (HttpContext ctx, CancellationToken ct) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<IntentResolver>>();
            var conversationStore = ctx.RequestServices.GetRequiredService<IConversationStore>();

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);

            try
            {
                var request = JsonSerializer.Deserialize<JsonElement>(body);

                var conversationId = request.GetProperty("conversationId").GetString() ?? "";
                var approved = request.GetProperty("approved").GetBoolean();

                var conversation = conversationStore.Get(conversationId);
                if (conversation is null)
                {
                    return Results.Json(new { success = false, message = "대화 세션을 찾을 수 없습니다." }, s_jsonOptions);
                }

                if (approved)
                {
                    logger.LogInformation("코드 변경 승인: {ConvId}", conversationId);
                    conversation.AddMessage("system", "사용자가 코드 변경을 승인했습니다.");
                }
                else
                {
                    logger.LogInformation("코드 변경 거부: {ConvId}", conversationId);
                    conversation.AddMessage("system", "사용자가 코드 변경을 거부했습니다.");
                }

                conversation.PendingChange = null;

                return Results.Json(new
                {
                    success = true,
                    message = approved ? "적용 완료" : "취소됨"
                }, s_jsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat approve 처리 오류");
                return Results.Json(new { success = false, message = ex.Message }, s_jsonOptions);
            }
        });

        // --- Run API 엔드포인트 (contracts.md §11) ---

        // Run 시작
        app.MapPost("/api/chat/runs", (HttpContext ctx, RunOrchestrator orchestrator) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<RunOrchestrator>>();

            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = reader.ReadToEndAsync().GetAwaiter().GetResult();
                var request = JsonSerializer.Deserialize<ChatRunStartRequest>(body, s_jsonOptions);

                if (request is null || string.IsNullOrWhiteSpace(request.Message))
                    return Results.BadRequest(new { error = "message is required" });

                var run = orchestrator.StartRun(request);

                logger.LogInformation("Run 시작: {RunId}, message={Msg}", run.RunId,
                    run.Message.Length > 50 ? run.Message[..50] + "..." : run.Message);

                return Results.Json(new
                {
                    runId = run.RunId,
                    conversationId = run.ConversationId,
                    state = run.State.ToString()
                }, s_jsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run 시작 오류");
                return Results.Json(new { error = ex.Message }, s_jsonOptions, statusCode: 500);
            }
        });

        // Run 상태 조회 (폴링)
        app.MapGet("/api/chat/runs/{runId}", (string runId, IConversationStore store) =>
        {
            var run = store.GetRun(runId);
            if (run is null)
                return Results.NotFound(new { error = "Run not found" });

            return Results.Json(BuildRunSnapshot(run), s_jsonOptions);
        });

        // Run 승인/거부
        app.MapPost("/api/chat/runs/{runId}/approval", async (HttpContext ctx, string runId,
            IConversationStore store, RunOrchestrator orchestrator) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<RunOrchestrator>>();

            var run = store.GetRun(runId);
            if (run is null)
                return Results.NotFound(new { error = "Run not found" });

            if (run.State != RunState.WaitingForApproval)
                return Results.BadRequest(new { error = $"Run is not waiting for approval (state={run.State})" });

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<JsonElement>(body);
            var approved = request.GetProperty("approved").GetBoolean();

            logger.LogInformation("Run {RunId} 승인 처리: approved={Approved}", runId, approved);
            orchestrator.ProcessApproval(run, approved);

            return Results.Json(BuildRunSnapshot(run), s_jsonOptions);
        });

        // 클라이언트 빌드/테스트 결과 수신
        app.MapPost("/api/chat/runs/{runId}/client-result", async (HttpContext ctx, string runId,
            IConversationStore store, RunOrchestrator orchestrator) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<ILogger<RunOrchestrator>>();

            var run = store.GetRun(runId);
            if (run is null)
                return Results.NotFound(new { error = "Run not found" });

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<ChatRunClientResultRequest>(body, s_jsonOptions);

            if (request is null)
                return Results.BadRequest(new { error = "Invalid request body" });

            logger.LogInformation("Run {RunId} 클라이언트 결과 수신: build={Build}, tests={Tests}",
                runId, request.Build.Attempted, request.Tests.Attempted);

            await orchestrator.ProcessClientResultAsync(run, request);

            return Results.Json(BuildRunSnapshot(run), s_jsonOptions);
        });
    }

    private static object BuildRunSnapshot(RunData run)
    {
        return new
        {
            runId = run.RunId,
            conversationId = run.ConversationId,
            state = run.State.ToString(),
            createdAt = run.CreatedAt,
            stages = run.Stages.Select(s => new
            {
                stageId = s.StageId,
                title = s.Title,
                status = s.Status.ToString(),
                message = s.Message,
                startedAt = s.StartedAt,
                completedAt = s.CompletedAt
            }).ToArray(),
            intent = run.Intent is not null ? new
            {
                toolName = run.Intent.ToolName,
                confidence = run.Intent.Confidence,
                description = run.Intent.Description
            } : null,
            planItems = run.PlanItems,
            references = run.References.Select(r => new
            {
                title = r.Title,
                source = r.Source,
                excerpt = r.Excerpt
            }).ToArray(),
            proposal = run.Proposal is not null ? new
            {
                summary = run.Proposal.Summary,
                original = run.Proposal.Original,
                modified = run.Proposal.Modified,
                requiresApproval = run.Proposal.RequiresApproval
            } : null,
            finalSummary = run.FinalSummary,
            error = run.Error
        };
    }

    /// <summary>
    /// LLM 응답에서 마지막 코드 블록을 추출한다.
    /// </summary>
    private static string? ExtractCodeFromResult(string result)
    {
        var lastFenceStart = -1;
        var lastFenceEnd = -1;
        var searchFrom = 0;

        while (true)
        {
            var start = result.IndexOf("```", searchFrom, StringComparison.Ordinal);
            if (start < 0) break;

            var contentStart = result.IndexOf('\n', start);
            if (contentStart < 0) break;
            contentStart++;

            var end = result.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (end < 0) break;

            lastFenceStart = contentStart;
            lastFenceEnd = end;
            searchFrom = end + 3;
        }

        if (lastFenceStart >= 0 && lastFenceEnd > lastFenceStart)
            return result[lastFenceStart..lastFenceEnd].TrimEnd();

        return null;
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
