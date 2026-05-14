using LocalMcpServer.Configuration;
using LocalMcpServer.LlmConnector;
using LocalMcpServer.ResourceCache;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.ToolRegistry;

/// <summary>
/// 프로젝트 전체 구조를 분석하는 도구.
/// ResourceCache 코드 인덱스에서 파일/심볼 구조를 수집하고
/// LLM으로 아키텍처 요약을 생성한다.
/// </summary>
public sealed class AnalyzeProjectStructureTool : IMcpTool
{
    private readonly OllamaConnector _llm;
    private readonly PromptTemplateLoader _promptLoader;
    private readonly IResourceCache _cache;
    private readonly ProjectSummarySection _summaryConfig;
    private readonly ILogger<AnalyzeProjectStructureTool> _logger;

    public AnalyzeProjectStructureTool(
        OllamaConnector llm,
        PromptTemplateLoader promptLoader,
        IResourceCache cache,
        IOptions<ServerConfig> config,
        ILogger<AnalyzeProjectStructureTool> logger)
    {
        _llm = llm;
        _promptLoader = promptLoader;
        _cache = cache;
        _summaryConfig = config.Value.ProjectSummary;
        _logger = logger;
    }

    public string Name => "analyze_project_structure";

    public string Description => "프로젝트 전체 구조를 분석합니다 (파일 구성, 모듈, 클래스/인터페이스 관계 등).";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "현재 열린 코드 (참고용, 선택)" },
            language = new { type = "string", description = "프로그래밍 언어 (선택)" }
        }
    };

    public async Task<ToolCallResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        if (!_cache.IsAvailable)
        {
            return new ToolCallResult
            {
                Content = [new ToolContent { Text = "코드 인덱스가 구성되지 않았습니다. SolutionPath를 전달하거나 appsettings.json의 CodeIndex.RootPath를 설정하세요." }]
            };
        }

        var structureSummary = _cache.GetProjectStructureSummary();

        // LLM 컨텍스트 보호: 구조 요약이 너무 길면 절단
        const int maxStructureLength = 12_000;
        if (structureSummary.Length > maxStructureLength)
            structureSummary = structureSummary[..maxStructureLength] + "\n\n... (이하 생략)";

        var prompt = await _promptLoader.LoadAndRenderAsync(
            Name,
            new Dictionary<string, string>
            {
                ["structure"] = structureSummary,
                ["language"] = GetStringArg(arguments, "language") ?? "C#"
            },
            ct);

        var llmResponse = await _llm.GenerateAsync(new LlmRequest
        {
            Prompt = prompt,
            Options = new LlmOptions
            {
                Temperature = 0.3,
                MaxTokens = 4096,
                NumCtx = 16384
            }
        }, ct);

        var summaryText = llmResponse.Text;

        // 결과 파일 저장
        if (_summaryConfig.Enabled && !string.IsNullOrWhiteSpace(summaryText))
        {
            await SaveSummaryToFileAsync(summaryText, ct);
        }

        return new ToolCallResult
        {
            Content = [new ToolContent { Text = summaryText }]
        };
    }

    private async Task SaveSummaryToFileAsync(string summaryText, CancellationToken ct)
    {
        try
        {
            var projectRoot = _cache.CurrentIndexRoot;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                _logger.LogWarning("프로젝트 루트 경로를 확인할 수 없어 요약 파일을 저장하지 않습니다.");
                return;
            }

            var outputPath = Path.IsPathRooted(_summaryConfig.OutputPath)
                ? _summaryConfig.OutputPath
                : Path.GetFullPath(_summaryConfig.OutputPath, projectRoot);

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            if (_summaryConfig.BackupOld && File.Exists(outputPath))
            {
                var backupPath = outputPath + $".{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                File.Move(outputPath, backupPath);
                _logger.LogInformation("기존 요약 파일 백업: {BackupPath}", backupPath);
            }

            await File.WriteAllTextAsync(outputPath, summaryText, System.Text.Encoding.UTF8, ct);
            _logger.LogInformation("프로젝트 요약 저장 완료: {OutputPath}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "프로젝트 요약 파일 저장 실패 (결과는 반환됩니다)");
        }
    }

    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() : je.ToString();

        return value.ToString();
    }
}
