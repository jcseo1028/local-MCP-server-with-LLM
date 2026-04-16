using LocalMcpServer.Configuration;
using LocalMcpServer.LlmConnector;
using LocalMcpServer.McpServer;
using LocalMcpServer.ResourceCache;
using LocalMcpServer.ToolRegistry;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// Kestrel 요청 타임아웃: 로컬 LLM 추론은 수 분이 걸릴 수 있음
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(10);
});

// --- Configuration 모듈 ---
builder.Services.Configure<ServerConfig>(builder.Configuration);
var config = builder.Configuration.Get<ServerConfig>() ?? new ServerConfig();

// --- LLM Connector 모듈 ---
builder.Services.AddHttpClient<OllamaConnector>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10); // 로컬 7B 모델은 긴 코드에서 수 분 소요될 수 있음
});

// --- Tool Registry 모듈 ---
var promptsDir = Path.GetFullPath(config.Tools.PromptsDirectory);
builder.Services.AddSingleton(sp =>
    new PromptTemplateLoader(promptsDir, sp.GetRequiredService<ILogger<PromptTemplateLoader>>()));
builder.Services.AddSingleton<SummarizeCurrentCodeTool>();
builder.Services.AddSingleton<AddCommentsTool>();
builder.Services.AddSingleton<RefactorCurrentCodeTool>();
builder.Services.AddSingleton<FixCodeIssuesTool>();
builder.Services.AddSingleton<SearchProjectCodeTool>();
builder.Services.AddSingleton<SuggestFixFromErrorLogTool>();
builder.Services.AddSingleton<ToolRegistryService>(sp =>
{
    var registry = new ToolRegistryService();
    registry.Register(sp.GetRequiredService<SummarizeCurrentCodeTool>());
    registry.Register(sp.GetRequiredService<AddCommentsTool>());
    registry.Register(sp.GetRequiredService<RefactorCurrentCodeTool>());
    registry.Register(sp.GetRequiredService<FixCodeIssuesTool>());
    registry.Register(sp.GetRequiredService<SearchProjectCodeTool>());
    registry.Register(sp.GetRequiredService<SuggestFixFromErrorLogTool>());
    return registry;
});

// --- Chat 모듈 (contracts.md §9, §10) ---
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
builder.Services.AddSingleton<IntentResolver>();

// --- Run Orchestration 모듈 (contracts.md §11, pipeline.md v2.1) ---
builder.Services.AddSingleton<DocumentSearcher>();
builder.Services.AddSingleton<RunOrchestrator>();

// --- Resource Cache 모듈 (contracts.md §4, modules.md §4) ---
builder.Services.AddSingleton<ResourceCacheService>();
builder.Services.AddSingleton<IResourceCache>(sp => sp.GetRequiredService<ResourceCacheService>());

var app = builder.Build();

// --- Startup: Ollama 연결 확인 ---
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Resource Cache 초기화 (시작 시 인덱스 구축)
var resourceCache = app.Services.GetRequiredService<ResourceCacheService>();
resourceCache.Initialize();

var ollama = app.Services.GetRequiredService<OllamaConnector>();
var healthy = await ollama.CheckHealthAsync();
if (healthy)
    logger.LogInformation("Ollama 연결 성공: {Endpoint}", config.Llm.Endpoint);
else
    logger.LogWarning("Ollama 연결 실패: {Endpoint}. 서버는 시작하지만 도구 호출 시 오류가 발생할 수 있습니다.", config.Llm.Endpoint);

logger.LogInformation("설정 확인 — DefaultModel={DefaultModel}, SummaryModel={SummaryModel}",
    config.Llm.DefaultModel, config.Llm.SummaryModel ?? "(null)");

// --- MCP Server 엔드포인트 ---
app.MapMcpEndpoints();

logger.LogInformation("MCP Server 시작: http://{Host}:{Port} (transport: {Transport})",
    config.Server.Host, config.Server.Port, config.Server.Transport);

app.Run($"http://{config.Server.Host}:{config.Server.Port}");
