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
}
