using System.Text;

namespace LocalMcpServer.McpServer;

/// <summary>
/// Run 파이프라인의 각 단계별 요청/응답을 마크다운 로그 파일로 기록한다.
/// logs/ 디렉터리에 Run별 파일을 생성하며, 디버깅 및 원인 분석에 활용한다.
/// </summary>
public sealed class RunLogger
{
    private readonly string _logDirectory;
    private readonly ILogger<RunLogger> _logger;

    public RunLogger(ILogger<RunLogger> logger)
    {
        _logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        _logger = logger;
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>Run 시작 — 사용자 요청 기록.</summary>
    public void LogRunStart(RunData run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Run {run.RunId}");
        sb.AppendLine($"- **시각**: {run.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **ConversationId**: {run.ConversationId}");
        sb.AppendLine($"- **IntentAndPlanOnly**: {run.IntentAndPlanOnly}");
        sb.AppendLine();
        sb.AppendLine("## 1. 사용자 요청");
        sb.AppendLine($"- **메시지**: {run.Message}");
        sb.AppendLine($"- **언어**: {run.Language ?? "(없음)"}");
        sb.AppendLine($"- **ActiveFile**: {run.ActiveFilePath ?? "(없음)"}");
        sb.AppendLine($"- **SolutionPath**: {run.SolutionPath ?? "(없음)"}");
        sb.AppendLine($"- **코드 길이**: {run.Code?.Length ?? 0}자");
        if (run.Code is not null)
        {
            var preview = run.Code.Length > 500 ? run.Code[..500] + "\n...(생략)" : run.Code;
            sb.AppendLine("```");
            sb.AppendLine(preview);
            sb.AppendLine("```");
        }
        sb.AppendLine();

        WriteLog(run.RunId, sb.ToString());
    }

    /// <summary>의도 분석 결과 기록.</summary>
    public void LogIntentResult(string runId, IntentResult intent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 2. 의도 분석");
        sb.AppendLine($"- **ToolName**: {intent.ToolName ?? "(null)"}");
        sb.AppendLine($"- **Confidence**: {intent.Confidence:F2}");
        sb.AppendLine($"- **Description**: {intent.Description}");
        if (!string.IsNullOrWhiteSpace(intent.RawLlmResponse))
        {
            sb.AppendLine("### LLM Raw 응답");
            sb.AppendLine("```");
            sb.AppendLine(intent.RawLlmResponse);
            sb.AppendLine("```");
        }
        if (intent.FallbackUsed)
        {
            sb.AppendLine($"- **Fallback 사용**: 키워드 fallback");
        }
        sb.AppendLine();

        AppendLog(runId, sb.ToString());
    }

    /// <summary>계획 수립 결과 기록.</summary>
    public void LogPlan(string runId, List<string> planItems, string? rawLlmResponse = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 3. 계획 수립");
        sb.AppendLine($"- **항목 수**: {planItems.Count}");
        for (int i = 0; i < planItems.Count; i++)
            sb.AppendLine($"  {i + 1}. {planItems[i]}");
        if (!string.IsNullOrWhiteSpace(rawLlmResponse))
        {
            sb.AppendLine("### LLM Raw 응답");
            sb.AppendLine("```");
            sb.AppendLine(rawLlmResponse);
            sb.AppendLine("```");
        }
        sb.AppendLine();

        AppendLog(runId, sb.ToString());
    }

    /// <summary>도구 실행 결과 기록.</summary>
    public void LogToolExecution(string runId, string toolName, string resultSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 5. 도구 실행");
        sb.AppendLine($"- **Tool**: {toolName}");
        sb.AppendLine("### 결과");
        var preview = resultSummary.Length > 3000
            ? resultSummary[..3000] + $"\n...(총 {resultSummary.Length}자 중 3000자 표시)"
            : resultSummary;
        sb.AppendLine("```");
        sb.AppendLine(preview);
        sb.AppendLine("```");
        sb.AppendLine();

        AppendLog(runId, sb.ToString());
    }

    /// <summary>최종 요약 기록.</summary>
    public void LogFinalSummary(string runId, RunData run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 9. 최종 결과");
        sb.AppendLine($"- **State**: {run.State}");
        sb.AppendLine($"- **Error**: {run.Error ?? "(없음)"}");
        sb.AppendLine();

        sb.AppendLine("### 단계 요약");
        foreach (var stage in run.Stages)
        {
            var status = stage.Status switch
            {
                StageStatus.Completed => "✅",
                StageStatus.Skipped => "⏭️",
                StageStatus.Failed => "❌",
                StageStatus.InProgress => "🔄",
                _ => "⬜"
            };
            sb.AppendLine($"  {status} **{stage.Title}** — {stage.Message ?? stage.Status.ToString()}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(run.FinalSummary))
        {
            sb.AppendLine("### 최종 응답");
            var preview = run.FinalSummary.Length > 5000
                ? run.FinalSummary[..5000] + $"\n...(총 {run.FinalSummary.Length}자 중 5000자 표시)"
                : run.FinalSummary;
            sb.AppendLine(preview);
        }
        sb.AppendLine();

        AppendLog(runId, sb.ToString());
    }

    /// <summary>임의 단계 메시지 기록.</summary>
    public void LogStage(string runId, string stageTitle, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {stageTitle}");
        sb.AppendLine(content);
        sb.AppendLine();

        AppendLog(runId, sb.ToString());
    }

    private string GetLogFilePath(string runId)
    {
        // 날짜별 하위 디렉터리
        var dateDir = Path.Combine(_logDirectory, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dateDir);
        return Path.Combine(dateDir, $"run-{runId[..8]}.md");
    }

    private void WriteLog(string runId, string content)
    {
        try
        {
            var path = GetLogFilePath(runId);
            File.WriteAllText(path, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run 로그 파일 쓰기 실패: {RunId}", runId);
        }
    }

    private void AppendLog(string runId, string content)
    {
        try
        {
            var path = GetLogFilePath(runId);
            File.AppendAllText(path, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run 로그 파일 추가 실패: {RunId}", runId);
        }
    }
}
