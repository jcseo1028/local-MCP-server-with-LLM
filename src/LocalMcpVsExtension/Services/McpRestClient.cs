using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LocalMcpVsExtension.Services
{
    /// <summary>
    /// MCP Server Direct REST API 클라이언트.
    /// contracts.md §8 (GET /api/tools/list, POST /api/tools/call) 준수.
    /// contracts.md §9, §10 (POST /api/chat, POST /api/chat/approve) 준수.
    /// contracts.md §11 (Run API) 준수.
    /// </summary>
    internal sealed class McpRestClient
    {
        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 서버에 등록된 도구 목록을 조회한다. (GET /api/tools/list)
        /// </summary>
        public async Task<ToolInfo[]> GetToolsAsync(string serverUrl)
        {
            var response = await s_http.GetAsync(
                $"{serverUrl}/api/tools/list").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<ToolListResponse>(json, s_jsonOptions);
            return result?.Tools ?? Array.Empty<ToolInfo>();
        }

        /// <summary>
        /// MCP 도구를 REST API로 호출한다. (POST /api/tools/call)
        /// </summary>
        public async Task<string> CallToolAsync(
            string serverUrl, string toolName, IDictionary<string, string> arguments)
        {
            var request = new ToolCallRequest
            {
                Name = toolName,
                Arguments = arguments
            };

            var json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await s_http.PostAsync(
                $"{serverUrl}/api/tools/call", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<ToolCallResponse>(responseJson, s_jsonOptions);

            if (result != null && !string.IsNullOrEmpty(result.Error))
            {
                return "서버 오류: " + result.Error;
            }

            if (result?.Content != null && result.Content.Length > 0)
            {
                return result.Content[0].Text ?? "(빈 응답)";
            }

            return "(응답 없음)";
        }

        /// <summary>
        /// 채팅 메시지를 전송한다. (POST /api/chat, contracts.md §9)
        /// </summary>
        public async Task<ChatResponse> SendChatAsync(string serverUrl, ChatRequest request)
        {
            var json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await s_http.PostAsync(
                $"{serverUrl}/api/chat", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<ChatResponse>(responseJson, s_jsonOptions)
                ?? new ChatResponse();
        }

        /// <summary>
        /// 코드 변경 승인/거부를 전송한다. (POST /api/chat/approve, contracts.md §10)
        /// </summary>
        public async Task<ChatApprovalResponse> SendApprovalAsync(
            string serverUrl, string conversationId, bool approved)
        {
            var request = new { conversationId, approved };
            var json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await s_http.PostAsync(
                $"{serverUrl}/api/chat/approve", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<ChatApprovalResponse>(responseJson, s_jsonOptions)
                ?? new ChatApprovalResponse();
        }

        // --- Run API (contracts.md §11) ---

        /// <summary>
        /// Run을 시작한다. (POST /api/chat/runs)
        /// </summary>
        public async Task<RunStartResponse> StartRunAsync(string serverUrl, RunStartRequest request)
        {
            var json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await s_http.PostAsync(
                $"{serverUrl}/api/chat/runs", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunStartResponse>(responseJson, s_jsonOptions)
                ?? new RunStartResponse();
        }

        /// <summary>
        /// Run 상태를 폴링한다. (GET /api/chat/runs/{runId})
        /// </summary>
        public async Task<RunSnapshot> GetRunAsync(string serverUrl, string runId)
        {
            var response = await s_http.GetAsync(
                $"{serverUrl}/api/chat/runs/{runId}").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunSnapshot>(responseJson, s_jsonOptions)
                ?? new RunSnapshot();
        }

        /// <summary>
        /// Run 승인/거부를 전송한다. (POST /api/chat/runs/{runId}/approval)
        /// </summary>
        public async Task<RunSnapshot> SendRunApprovalAsync(string serverUrl, string runId, bool approved)
        {
            var request = new { approved };
            var json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await s_http.PostAsync(
                $"{serverUrl}/api/chat/runs/{runId}/approval", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunSnapshot>(responseJson, s_jsonOptions)
                ?? new RunSnapshot();
        }

        /// <summary>
        /// 빌드/테스트 결과를 전송한다. (POST /api/chat/runs/{runId}/client-result)
        /// </summary>
        public async Task<RunSnapshot> SendClientResultAsync(
            string serverUrl, string runId, ClientResultRequest request)
        {
            var json = JsonSerializer.Serialize(request, s_jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await s_http.PostAsync(
                $"{serverUrl}/api/chat/runs/{runId}/client-result", content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<RunSnapshot>(responseJson, s_jsonOptions)
                ?? new RunSnapshot();
        }
    }

    // ── DTO: 도구 목록 ──────────────────────────────────────

    internal sealed class ToolListResponse
    {
        public ToolInfo[]? Tools { get; set; }
    }

    internal sealed class ToolInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    // ── DTO: 도구 호출 ──────────────────────────────────────

    internal sealed class ToolCallRequest
    {
        public string Name { get; set; } = "";
        public IDictionary<string, string> Arguments { get; set; } = new Dictionary<string, string>();
    }

    internal sealed class ToolCallResponse
    {
        public ToolCallContentItem[]? Content { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class ToolCallContentItem
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }
    }

    // ── DTO: 채팅 (contracts.md §9, §10) ────────────────────

    internal sealed class ChatRequest
    {
        public string Message { get; set; } = "";
        public string? Code { get; set; }
        public string? Language { get; set; }
        public bool SelectionOnly { get; set; }
        public string? ConversationId { get; set; }
    }

    internal sealed class ChatResponse
    {
        public string? ConversationId { get; set; }
        public ChatIntent? Intent { get; set; }
        public string? Result { get; set; }
        public ChatCodeChange? CodeChange { get; set; }
        public bool RequiresApproval { get; set; }
    }

    internal sealed class ChatIntent
    {
        public string? ToolName { get; set; }
        public double Confidence { get; set; }
        public string? Description { get; set; }
    }

    internal sealed class ChatCodeChange
    {
        public string? Original { get; set; }
        public string? Modified { get; set; }
        public string? ToolName { get; set; }
    }

    internal sealed class ChatApprovalResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
    }

    // ── DTO: Run API (contracts.md §11) ─────────────────────

    internal sealed class RunStartRequest
    {
        public string Message { get; set; } = "";
        public string? Code { get; set; }
        public string? Language { get; set; }
        public bool SelectionOnly { get; set; }
        public string? ConversationId { get; set; }
        public string? ActiveFilePath { get; set; }
        public string? SolutionPath { get; set; }
    }

    internal sealed class RunStartResponse
    {
        public string RunId { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string State { get; set; } = "";
    }

    internal sealed class RunSnapshot
    {
        public string RunId { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string State { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public RunStageDto[] Stages { get; set; } = Array.Empty<RunStageDto>();
        public ChatIntent? Intent { get; set; }
        public string[] PlanItems { get; set; } = Array.Empty<string>();
        public RunReferenceDto[] References { get; set; } = Array.Empty<RunReferenceDto>();
        public RunProposalDto? Proposal { get; set; }
        public string? FinalSummary { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class RunStageDto
    {
        public string StageId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public string? Message { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    internal sealed class RunReferenceDto
    {
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public string Excerpt { get; set; } = "";
    }

    internal sealed class RunProposalDto
    {
        public string Summary { get; set; } = "";
        public string? Original { get; set; }
        public string? Modified { get; set; }
        public bool RequiresApproval { get; set; }
    }

    internal sealed class ClientResultRequest
    {
        public bool Applied { get; set; }
        public string? ApplyMessage { get; set; }
        public string[] AppliedTargets { get; set; } = Array.Empty<string>();
        public ClientBuildResult Build { get; set; } = new ClientBuildResult();
        public ClientTestResult Tests { get; set; } = new ClientTestResult();
    }

    internal sealed class ClientBuildResult
    {
        public bool Attempted { get; set; }
        public bool? Succeeded { get; set; }
        public string? Summary { get; set; }
    }

    internal sealed class ClientTestResult
    {
        public bool Attempted { get; set; }
        public bool? Succeeded { get; set; }
        public string? Summary { get; set; }
    }
}
