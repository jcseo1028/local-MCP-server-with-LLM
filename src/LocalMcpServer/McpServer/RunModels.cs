namespace LocalMcpServer.McpServer;

/// <summary>
/// Run 단위 다단계 오케스트레이션 모델.
/// contracts.md §11, pipeline.md Chat Run Pipeline 준수.
/// </summary>

public enum RunState
{
    Queued,
    Running,
    WaitingForApproval,
    Completed,
    Rejected,
    Failed
}

public enum StageStatus
{
    Pending,
    InProgress,
    Completed,
    Skipped,
    Failed
}

public enum PlanExecutionMode
{
    Single,
    Multi
}

public enum PlanStepStatus
{
    Pending,
    Running,
    WaitingConfirm,
    Completed,
    Failed,
    Reverted
}

public enum PendingPatchState
{
    Pending,
    Confirmed,
    Reverted,
    Expired
}

public static class StageIds
{
    public const string IntentAnalysis = "intent_analysis";
    public const string Planning = "planning";
    public const string ContextCollection = "context_collection";
    public const string DocumentSearch = "document_search";
    public const string ProposalGeneration = "proposal_generation";
    public const string Approval = "approval";
    public const string Applying = "applying";
    public const string BuildTest = "build_test";
    public const string FinalSummary = "final_summary";

    public static readonly string[] All =
    [
        IntentAnalysis, Planning, ContextCollection, DocumentSearch,
        ProposalGeneration, Approval, Applying, BuildTest, FinalSummary
    ];

    public static readonly Dictionary<string, string> Titles = new()
    {
        [IntentAnalysis] = "의도 분석",
        [Planning] = "계획 수립",
        [ContextCollection] = "컨텍스트 수집",
        [DocumentSearch] = "문서 검색",
        [ProposalGeneration] = "수정안 생성",
        [Approval] = "승인",
        [Applying] = "적용",
        [BuildTest] = "빌드/테스트",
        [FinalSummary] = "결과 요약"
    };
}

public sealed class RunStage
{
    public string StageId { get; set; } = "";
    public string Title { get; set; } = "";
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public void Start()
    {
        Status = StageStatus.InProgress;
        StartedAt = DateTime.UtcNow;
    }

    public void Complete(string? message = null)
    {
        Status = StageStatus.Completed;
        Message = message;
        CompletedAt = DateTime.UtcNow;
    }

    public void Skip(string? message = null)
    {
        Status = StageStatus.Skipped;
        Message = message;
        CompletedAt = DateTime.UtcNow;
    }

    public void Fail(string? message = null)
    {
        Status = StageStatus.Failed;
        Message = message;
        CompletedAt = DateTime.UtcNow;
    }
}

public sealed class RunData
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "";
    public RunState State { get; set; } = RunState.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // 입력
    public string Message { get; set; } = "";
    public string? Code { get; set; }
    public string? Language { get; set; }
    public bool SelectionOnly { get; set; }
    public string? ActiveFilePath { get; set; }
    public string? SolutionPath { get; set; }
    public bool IntentAndPlanOnly { get; set; }
    public List<FileContext>? Files { get; set; } // v2.2 멀티 파일 컨텍스트
    public bool AllowMultiToolPlan { get; set; }
    public int MaxPlanSteps { get; set; } = 3;

    // 단계
    public List<RunStage> Stages { get; set; } = StageIds.All
        .Select(id => new RunStage { StageId = id, Title = StageIds.Titles[id] })
        .ToList();

    // 결과
    public IntentResult? Intent { get; set; }
    public List<string> PlanItems { get; set; } = [];
    public string? PlanRawLlmResponse { get; set; }
    public List<RunPlanStep> PlanSteps { get; set; } = [];
    public int CurrentStepIndex { get; set; }
    public PlanExecutionMode ExecutionMode { get; set; } = PlanExecutionMode.Single;
    public PendingPatch? PendingPatch { get; set; }
    public TimeSpan PausedDuration { get; set; } = TimeSpan.Zero;
    public DateTime? PauseStartedAt { get; set; }
    public List<DocumentReference> References { get; set; } = [];
    public RunProposal? Proposal { get; set; }
    public string? FinalSummary { get; set; }
    public string? Error { get; set; }

    public RunStage GetStage(string stageId) =>
        Stages.First(s => s.StageId == stageId);
}

public sealed class DocumentReference
{
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Excerpt { get; set; } = "";
}

public sealed class RunProposal
{
    public string Summary { get; set; } = "";
    public bool RequiresApproval { get; set; }

    // 하위 호환 단건 (deprecated — 신규 코드에서 사용 금지)
    public string? Original { get; set; }
    public string? Modified { get; set; }

    // 멀티 파일 변경 목록 — v2.2
    public List<FileChange>? Changes { get; set; }

    /// <summary>true면 멀티 파일 모드로 동작</summary>
    public bool IsMultiFile => Changes != null && Changes.Count > 0;
}

public sealed class RunPlanStep
{
    public string StepId { get; set; } = Guid.NewGuid().ToString("N");
    public string ToolName { get; set; } = "";
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
    public string? ResultSummary { get; set; }
    public bool RequiresApproval { get; set; }
}

public sealed class PendingPatch
{
    public string PatchId { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = "";
    public string? StepId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public PendingPatchState State { get; set; } = PendingPatchState.Pending;
    public List<FileChange> Files { get; set; } = [];
}

// --- API DTO ---

public sealed class FileChange
{
    public string FilePath { get; set; } = "";
    public string Original { get; set; } = "";
    public string Modified { get; set; } = "";
    public bool SelectionOnly { get; set; }
    public bool IsNewFile { get; set; }
    public string? Description { get; set; }

    // A-2: 서버 측 사전 계산 hunks (VSIX 재계산 생략 목적)
    public DiffHunkInfo[]? Hunks { get; set; }
}

/// <summary>A-2: 서버 측 사전 계산 diff hunk 정보.</summary>
public sealed class DiffHunkInfo
{
    public int OriginalStart { get; }
    public int OriginalEnd   { get; }
    public string NewText    { get; }

    public DiffHunkInfo(int originalStart, int originalEnd, string newText)
    {
        OriginalStart = originalStart;
        OriginalEnd   = originalEnd;
        NewText       = newText;
    }
}

public sealed class FileContext
{
    public string FilePath { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Language { get; set; }
    public bool SelectionOnly { get; set; }
    public string? SelectedCode { get; set; }
}

public sealed class ChatRunStartRequest
{
    public string Message { get; set; } = "";
    public string? Code { get; set; }
    public string? Language { get; set; }
    public bool SelectionOnly { get; set; }
    public string? ConversationId { get; set; }
    public string? ActiveFilePath { get; set; }
    public string? SolutionPath { get; set; }
    public bool IntentAndPlanOnly { get; set; }
    public bool AllowMultiToolPlan { get; set; }
    public int? MaxPlanSteps { get; set; }

    // 멀티 파일 컨텍스트 — v2.2 (null이면 단건 필드 사용)
    public List<FileContext>? Files { get; set; }
}

public sealed class ChatRunClientResultRequest
{
    public bool Applied { get; set; }
    public string? ApplyMessage { get; set; }
    public string[] AppliedTargets { get; set; } = [];

    // 파일별 적용 결과 — v2.2 (null이면 단건 Applied 사용)
    public List<FileApplyResult>? ApplyResults { get; set; }

    public BuildResult Build { get; set; } = new();
    public TestResult Tests { get; set; } = new();
}

public sealed class FileApplyResult
{
    public string FilePath { get; set; } = "";
    public bool Applied { get; set; }
    public string? Message { get; set; }
}

public sealed class BuildResult
{
    public bool Attempted { get; set; }
    public bool? Succeeded { get; set; }
    public string? Summary { get; set; }
}

public sealed class TestResult
{
    public bool Attempted { get; set; }
    public bool? Succeeded { get; set; }
    public string? Summary { get; set; }
}
