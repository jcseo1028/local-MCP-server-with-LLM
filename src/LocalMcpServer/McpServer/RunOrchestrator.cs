using LocalMcpServer.Configuration;
using LocalMcpServer.ResourceCache;
using LocalMcpServer.ToolRegistry;
using Microsoft.Extensions.Options;

namespace LocalMcpServer.McpServer;

/// <summary>
/// 9단계 Chat Run 오케스트레이션 엔진.
/// pipeline.md Chat Run Pipeline (v2.1) 구현.
/// 백그라운드 태스크로 실행되며 ConversationStore에 상태를 기록한다.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly IConversationStore _store;
    private readonly IntentResolver _intent;
    private readonly DocumentSearcher _docSearcher;
    private readonly ToolRegistryService _registry;
    private readonly IResourceCache _cache;
    private readonly VectorSearchEngine _vectorSearchEngine;
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly RunLogger _runLogger;
    private readonly ServerConfig _config;
    private readonly ChatSection _chatConfig;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StepExecutionBudget = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan MultiFileRetryMinBudget = TimeSpan.FromSeconds(180);

    public RunOrchestrator(
        IConversationStore store,
        IntentResolver intent,
        DocumentSearcher docSearcher,
        ToolRegistryService registry,
        IResourceCache cache,
        VectorSearchEngine vectorSearchEngine,
        IOptions<ServerConfig> config,
        RunLogger runLogger,
        ILogger<RunOrchestrator> logger)
    {
        _store = store;
        _intent = intent;
        _docSearcher = docSearcher;
        _registry = registry;
        _cache = cache;
        _vectorSearchEngine = vectorSearchEngine;
        _config = config.Value;
        _chatConfig = config.Value.Chat;
        _runLogger = runLogger;
        _logger = logger;
    }

    /// <summary>
    /// 새 Run을 생성하고 백그라운드에서 오케스트레이션을 시작한다.
    /// </summary>
    public RunData StartRun(ChatRunStartRequest req)
    {
        var conversation = _store.GetOrCreate(req.ConversationId);
        conversation.AddMessage("user", req.Message);

        var run = new RunData
        {
            ConversationId = conversation.ConversationId,
            Message = req.Message,
            Code = req.Code,
            Language = req.Language,
            SelectionOnly = req.SelectionOnly,
            ActiveFilePath = req.ActiveFilePath,
            SolutionPath = req.SolutionPath,
            IntentAndPlanOnly = req.IntentAndPlanOnly || _chatConfig.IntentAndPlanOnly,
            SessionSyncEnabled = req.SessionSyncEnabled,
            SessionSnapshotVersion = req.SessionSnapshotVersion,
            Files = req.Files,  // v2.2 멀티 파일 컨텍스트
            AllowMultiToolPlan = req.AllowMultiToolPlan,
            MaxPlanSteps = Math.Clamp(req.MaxPlanSteps ?? 3, 1, 5)
        };

        _store.AddRun(run);

        // SolutionPath가 있고 현재 인덱스 루트와 다르면 백그라운드 재인덱싱
        if (!string.IsNullOrEmpty(req.SolutionPath))
        {
            _ = _cache.ReindexAsync(req.SolutionPath);
        }

        _runLogger.LogRunStart(run);

        _ = Task.Run(() => ExecutePipelineAsync(run));

        return run;
    }

    /// <summary>
    /// 승인/거부 처리 후 나머지 파이프라인을 진행한다.
    /// </summary>
    public void ProcessApproval(RunData run, bool approved)
    {
        EndPause(run);
        var stage = run.GetStage(StageIds.Approval);

        if (approved)
        {
            if (run.PendingPatch is not null)
                run.PendingPatch.State = PendingPatchState.Confirmed;

            if (run.PlanSteps.Count > 0 &&
                run.CurrentStepIndex >= 0 &&
                run.CurrentStepIndex < run.PlanSteps.Count)
            {
                run.PlanSteps[run.CurrentStepIndex].Status = PlanStepStatus.Completed;
            }

            stage.Complete("승인됨");
            run.State = RunState.Running;
            _ = Task.Run(() => ExecutePostApprovalAsync(run));
        }
        else
        {
            if (run.PendingPatch is not null)
                run.PendingPatch.State = PendingPatchState.Reverted;

            if (run.PlanSteps.Count > 0 &&
                run.CurrentStepIndex >= 0 &&
                run.CurrentStepIndex < run.PlanSteps.Count)
            {
                run.PlanSteps[run.CurrentStepIndex].Status = PlanStepStatus.Reverted;
            }

            stage.Fail("거부됨");
            run.State = RunState.Rejected;

            var summary = run.GetStage(StageIds.FinalSummary);
            summary.Start();
            summary.Complete("사용자가 변경을 거부했습니다.");
            run.FinalSummary = "사용자가 변경을 거부했습니다.";
        }
    }

    public bool ProcessConfirm(RunData run, string patchId)
    {
        if (run.PendingPatch is null ||
            run.PendingPatch.State != PendingPatchState.Pending ||
            !string.Equals(run.PendingPatch.PatchId, patchId, StringComparison.Ordinal))
            return false;

        ProcessApproval(run, true);
        return true;
    }

    public bool ProcessRevert(RunData run, string patchId, string? reason)
    {
        if (run.PendingPatch is null ||
            run.PendingPatch.State != PendingPatchState.Pending ||
            !string.Equals(run.PendingPatch.PatchId, patchId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(reason))
            _logger.LogInformation("Run {RunId} 되돌리기 요청: {Reason}", run.RunId, reason);

        ProcessApproval(run, false);
        return true;
    }

    /// <summary>
    /// VSIX에서 build/test 결과를 수신하여 파이프라인을 완료한다.
    /// </summary>
    public async Task ProcessClientResultAsync(RunData run, ChatRunClientResultRequest result)
    {
        EndPause(run);

        // build_test 단계 완료
        var buildStage = run.GetStage(StageIds.BuildTest);
        if (result.Build.Attempted)
        {
            var msg = result.Build.Succeeded == true
                ? $"빌드 성공: {result.Build.Summary}"
                : $"빌드 실패: {result.Build.Summary}";
            if (result.Tests.Attempted)
                msg += result.Tests.Succeeded == true
                    ? $" | 테스트 성공: {result.Tests.Summary}"
                    : $" | 테스트 실패: {result.Tests.Summary}";
            buildStage.Complete(msg);
        }
        else
        {
            buildStage.Skip("빌드/테스트 미실행");
        }

        // 빌드/테스트 실패 시 후속 step으로 진행하지 않고 즉시 실패 종료한다.
        var failReason = GetClientResultFailureReason(result);
        if (!string.IsNullOrWhiteSpace(failReason))
        {
            FailRunAfterClientResult(run, failReason);
            return;
        }

        // 다중 계획 모드에서 다음 step이 있으면 계속 실행
        run.PendingPatch = null;
        if (run.PlanSteps.Count > 0 && run.CurrentStepIndex < run.PlanSteps.Count - 1)
        {
            run.CurrentStepIndex++;
            var remaining = GetRemainingRunBudget(run);
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Run execution budget exceeded.");

            using var cts = new CancellationTokenSource(remaining);
            var completed = await ExecutePlanFromCurrentStepAsync(run, cts.Token);

            if (!completed)
            {
                var approvalStage = run.GetStage(StageIds.Approval);
                approvalStage.Start();
                run.State = RunState.WaitingForApproval;
                StartPause(run);
                return;
            }
        }

        // 최종 요약
        await GenerateFinalSummaryAsync(run);
    }

    private static string? GetClientResultFailureReason(ChatRunClientResultRequest result)
    {
        if (!result.Applied)
            return string.IsNullOrWhiteSpace(result.ApplyMessage)
                ? "클라이언트 코드 적용 실패"
                : $"클라이언트 코드 적용 실패: {result.ApplyMessage}";

        if (result.Build.Attempted && result.Build.Succeeded == false)
            return string.IsNullOrWhiteSpace(result.Build.Summary)
                ? "클라이언트 빌드 실패"
                : $"클라이언트 빌드 실패: {result.Build.Summary}";

        if (result.Tests.Attempted && result.Tests.Succeeded == false)
            return string.IsNullOrWhiteSpace(result.Tests.Summary)
                ? "클라이언트 테스트 실패"
                : $"클라이언트 테스트 실패: {result.Tests.Summary}";

        return null;
    }

    private void FailRunAfterClientResult(RunData run, string reason)
    {
        run.PendingPatch = null;
        run.State = RunState.Failed;
        run.Error = reason;

        if (run.PlanSteps.Count > 0 &&
            run.CurrentStepIndex >= 0 &&
            run.CurrentStepIndex < run.PlanSteps.Count)
        {
            var step = run.PlanSteps[run.CurrentStepIndex];
            step.Status = PlanStepStatus.Failed;
            step.ResultSummary = reason;
        }

        foreach (var stage in run.Stages)
        {
            if (stage.Status == StageStatus.InProgress)
                stage.Fail(reason);
        }

        _logger.LogWarning("Run {RunId} 클라이언트 결과로 실패 종료: {Reason}", run.RunId, reason);
        _runLogger.LogFinalSummary(run.RunId, run);
    }

    private async Task ExecutePipelineAsync(RunData run)
    {
        try
        {
            run.State = RunState.Running;
            var remaining = GetRemainingRunBudget(run);
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Run execution budget exceeded.");

            using var cts = new CancellationTokenSource(remaining);
            var ct = cts.Token;

            // 1. 의도 분석
            var s1 = run.GetStage(StageIds.IntentAnalysis);
            s1.Start();
            run.Intent = await _intent.AnalyzeIntentAsync(run.Message, run.Language, ct);
            s1.Complete($"tool={run.Intent.ToolName ?? "chat"}, confidence={run.Intent.Confidence:F2}");
            _runLogger.LogIntentResult(run.RunId, run.Intent);

            // 2. 계획 수립
            var s2 = run.GetStage(StageIds.Planning);
            s2.Start();
            var planResult = await _intent.GeneratePlanAsync(run.Intent, run.Message, run.Code, run.Language, ct);
            run.PlanItems = planResult.Items;
            run.PlanRawLlmResponse = planResult.RawResponse;
            InitializePlan(run);
            s2.Complete($"{run.PlanItems.Count}개 항목");
            _runLogger.LogPlan(run.RunId, run.PlanItems, run.PlanRawLlmResponse);

            if (run.IntentAndPlanOnly)
            {
                CompleteIntentAndPlanOnlyRun(run);
                return;
            }

            // 3. 컨텍스트 수집 — §8c 컨텍스트 검증 (RAG 우선 적용)
            var s3 = run.GetStage(StageIds.ContextCollection);
            s3.Start();
            
            string ragContext = string.Empty;
            if (run.Code is not null && run.ActiveFilePath is not null)
            {
                // RAG 먼저 조립 (절단 전)
                _logger.LogInformation(
                    "Run {RunId} RAG 시도: toolName={Tool}, path={Path}, codeSize={Size}자",
                    run.RunId, run.Intent?.ToolName ?? "unknown", run.ActiveFilePath, run.Code.Length);
                
                ragContext = await BuildRagContextAsync(
                    run.Message,
                    run.ActiveFilePath,
                    run.Code,
                    run.ConversationId,
                    ResolveSolutionHashForSession(run),
                    CountLines(run.Code),
                    ct);
                
                if (!string.IsNullOrEmpty(ragContext))
                {
                    _logger.LogInformation(
                        "Run {RunId} RAG 성공: {Size}자 추출",
                        run.RunId, ragContext.Length);
                }
            }
            
            if (run.Code is not null)
            {
                const int maxCodeLength = 32_000; // LLM 컨텍스트 윈도우 보호
                var originalLength = run.Code.Length;
                if (originalLength > maxCodeLength)
                {
                    run.Code = run.Code[..maxCodeLength];
                    _logger.LogWarning(
                        "Run {RunId} 코드 절단: {Original}자 → {Max}자 " +
                        "[RAG={HasRag}, 저장소={EmbeddingStoreReady}]",
                        run.RunId, originalLength, maxCodeLength,
                        !string.IsNullOrEmpty(ragContext) ? "조립" : "없음",
                        _cache.IsAvailable ? "준비" : "미준비");
                    s3.Complete($"코드 컨텍스트 수집 완료 ({originalLength}자 → {maxCodeLength}자 절단)" +
                        (!string.IsNullOrEmpty(ragContext) ? " + RAG" : ""));
                }
                else
                {
                    s3.Complete($"코드 컨텍스트 수집 완료 ({originalLength}자)" +
                        (!string.IsNullOrEmpty(ragContext) ? " + RAG" : ""));
                }
            }
            else
            {
                s3.Complete("코드 없음");
            }

            // 4. 문서 검색
            var s4 = run.GetStage(StageIds.DocumentSearch);
            s4.Start();
            run.References = await _docSearcher.SearchAsync(run.Message, ct);
            if (run.References.Count > 0)
                s4.Complete($"{run.References.Count}건 발견");
            else
                s4.Skip("관련 문서 없음");

            // 5. 수정안 생성
            var s5 = run.GetStage(StageIds.ProposalGeneration);
            s5.Start();
            var completed = await ExecutePlanFromCurrentStepAsync(run, ct);
            s5.Complete(run.Proposal?.Summary ?? "응답 생성 완료");

            // 6. 승인 단계 — 코드 수정 도구인 경우만
            var s6 = run.GetStage(StageIds.Approval);
            if (!completed && run.Proposal is { RequiresApproval: true })
            {
                s6.Start();
                run.State = RunState.WaitingForApproval;
                StartPause(run);
                _logger.LogInformation("Run {RunId} 승인 대기 중", run.RunId);
                // 여기서 중단 — 클라이언트가 POST /approval 로 재개
                return;
            }
            else
            {
                s6.Skip("승인 불필요");
            }

            // 승인 불필요한 경우 나머지 진행
            await ExecutePostApprovalAsync(run);
        }
        catch (OperationCanceledException)
        {
            if (GetRemainingRunBudget(run) <= TimeSpan.Zero)
            {
                _logger.LogWarning("Run {RunId} 타임아웃 취소됨 (limit={LimitMinutes}분)", run.RunId, RunTimeout.TotalMinutes);
                run.Error = "작업이 취소되었습니다. 실행 제한 시간(30분)을 초과했습니다.";
            }
            else
            {
                _logger.LogWarning("Run {RunId} 외부 취소됨 (서버 중단/요청 취소)", run.RunId);
                run.Error = "작업이 취소되었습니다. 서버가 중단되었거나 요청이 취소되었습니다.";
            }

            run.State = RunState.Failed;

            // 진행 중인 단계를 실패로 전환
            foreach (var stage in run.Stages)
            {
                if (stage.Status == StageStatus.InProgress)
                    stage.Fail("취소됨");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId} 파이프라인 오류", run.RunId);
            run.State = RunState.Failed;
            run.Error = ex.Message;
        }
    }

    private async Task ExecutePostApprovalAsync(RunData run)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));

            // 7. 적용 단계 — VSIX 클라이언트 측에서 수행, 서버는 상태만 기록
            var s7 = run.GetStage(StageIds.Applying);
            if (run.Proposal is { RequiresApproval: true })
            {
                s7.Start();
                s7.Complete("수정안을 클라이언트로 전달");
            }
            else
            {
                s7.Skip("적용 불필요");
            }

            // 8. 빌드/테스트 — VSIX 클라이언트에서 POST client-result로 전달
            var s8 = run.GetStage(StageIds.BuildTest);
            if (run.Proposal is { RequiresApproval: true })
            {
                s8.Start();
                // 클라이언트가 빌드/테스트 수행 후 POST client-result로 완료
                _logger.LogInformation("Run {RunId} 클라이언트 빌드/테스트 대기 중", run.RunId);
                StartPause(run);
                return;
            }
            else
            {
                s8.Skip("빌드/테스트 불필요");
            }

            // 9. 최종 요약
            await GenerateFinalSummaryAsync(run);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Run {RunId} 후처리 취소됨", run.RunId);
            run.State = RunState.Failed;
            run.Error = "후처리 중 취소되었습니다.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run {RunId} 후처리 오류", run.RunId);
            run.State = RunState.Failed;
            run.Error = ex.Message;
        }
    }

    private void CompleteIntentAndPlanOnlyRun(RunData run)
    {
        run.GetStage(StageIds.ContextCollection).Skip("의도/계획 검증 모드");
        run.GetStage(StageIds.DocumentSearch).Skip("의도/계획 검증 모드");
        run.GetStage(StageIds.ProposalGeneration).Skip("의도/계획 검증 모드");
        run.GetStage(StageIds.Approval).Skip("의도/계획 검증 모드");
        run.GetStage(StageIds.Applying).Skip("의도/계획 검증 모드");
        run.GetStage(StageIds.BuildTest).Skip("의도/계획 검증 모드");

        var summaryStage = run.GetStage(StageIds.FinalSummary);
        summaryStage.Start();

        var toolName = run.Intent?.ToolName ?? "chat";
        var description = string.IsNullOrWhiteSpace(run.Intent?.Description)
            ? "설명 없음"
            : run.Intent!.Description;
        var planSummary = run.PlanItems.Count > 0
            ? string.Join(" | ", run.PlanItems)
            : "계획 항목 없음";

        run.FinalSummary =
            $"의도/계획 검증 완료\n" +
            $"- tool: {toolName}\n" +
            $"- description: {description}\n" +
            $"- plans: {planSummary}";

        summaryStage.Complete("의도/계획 검증 완료");
        run.State = RunState.Completed;
        _runLogger.LogFinalSummary(run.RunId, run);

        _logger.LogInformation("Run {RunId} 의도/계획 검증 모드 완료", run.RunId);
    }

    private void InitializePlan(RunData run)
    {
        var seed = run.Intent?.ToolName;
        var tools = BuildToolPlan(run.Message, seed, run.AllowMultiToolPlan, run.MaxPlanSteps);
        run.PlanSteps = tools.Select((t, i) => new RunPlanStep
        {
            StepId = $"step-{i + 1}",
            ToolName = t,
            Status = PlanStepStatus.Pending
        }).ToList();

        run.ExecutionMode = run.PlanSteps.Count > 1 ? PlanExecutionMode.Multi : PlanExecutionMode.Single;
        run.CurrentStepIndex = 0;
    }

    private async Task<bool> ExecutePlanFromCurrentStepAsync(RunData run, CancellationToken ct)
    {
        if (run.PlanSteps.Count == 0)
        {
            var remaining = GetRemainingRunBudget(run);
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Run execution budget exceeded.");

            var budget = remaining < StepExecutionBudget ? remaining : StepExecutionBudget;
            using var stepCts = new CancellationTokenSource(budget);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stepCts.Token);

            await GenerateProposalAsync(run, linkedCts.Token, run.Intent?.ToolName);
            return run.Proposal is not { RequiresApproval: true };
        }

        for (int i = run.CurrentStepIndex; i < run.PlanSteps.Count; i++)
        {
            run.CurrentStepIndex = i;
            var step = run.PlanSteps[i];
            step.Status = PlanStepStatus.Running;

            var remaining = GetRemainingRunBudget(run);
            if (remaining <= TimeSpan.Zero)
                throw new OperationCanceledException("Run execution budget exceeded.");

            var budget = remaining < StepExecutionBudget ? remaining : StepExecutionBudget;
            using var stepCts = new CancellationTokenSource(budget);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stepCts.Token);

            await GenerateProposalAsync(run, linkedCts.Token, step.ToolName);
            step.ResultSummary = run.Proposal?.Summary;
            step.RequiresApproval = run.Proposal?.RequiresApproval == true;

            if (run.Proposal is { RequiresApproval: true })
            {
                step.Status = PlanStepStatus.WaitingConfirm;
                run.PendingPatch = BuildPendingPatch(run, step);
                return false;
            }

            step.Status = PlanStepStatus.Completed;
        }

        return true;
    }

    private static PendingPatch? BuildPendingPatch(RunData run, RunPlanStep step)
    {
        if (run.Proposal is null || !run.Proposal.RequiresApproval)
            return null;

        var patch = new PendingPatch
        {
            RunId = run.RunId,
            StepId = step.StepId,
            State = PendingPatchState.Pending
        };

        if (run.Proposal.Changes is { Count: > 0 })
        {
            patch.Files = run.Proposal.Changes.Select(c => new FileChange
            {
                FilePath = c.FilePath,
                Original = c.Original,
                Modified = c.Modified,
                SelectionOnly = c.SelectionOnly,
                IsNewFile = c.IsNewFile,
                Description = c.Description,
                Hunks = c.Hunks
            }).ToList();
        }
        else if (run.Proposal.Original is not null || run.Proposal.Modified is not null)
        {
            patch.Files.Add(new FileChange
            {
                FilePath = run.ActiveFilePath ?? "(active)",
                Original = run.Proposal.Original ?? "",
                Modified = run.Proposal.Modified ?? "",
                SelectionOnly = run.SelectionOnly,
                IsNewFile = false,
                Description = null,
                Hunks = null
            });
        }

        return patch;
    }

    private static List<string> BuildToolPlan(string message, string? seedToolName, bool allowMultiToolPlan, int maxPlanSteps)
    {
        var plan = new List<string>();
        if (!string.IsNullOrWhiteSpace(seedToolName))
            plan.Add(seedToolName!);

        if (!allowMultiToolPlan)
            return plan;

        var msg = (message ?? string.Empty).ToLowerInvariant();
        void AddIf(string tool, params string[] keywords)
        {
            if (plan.Count >= maxPlanSteps) return;
            if (plan.Contains(tool, StringComparer.OrdinalIgnoreCase)) return;
            if (keywords.Any(k => msg.Contains(k)))
                plan.Add(tool);
        }

        AddIf("organize_imports", "using", "import", "네임스페이스");
        AddIf("add_comments", "주석", "comment", "문서화");
        AddIf("refactor_current_code", "리팩터", "리팩토링", "refactor");
        AddIf("fix_code_issues", "버그", "오류", "고쳐", "fix");
        AddIf("summarize_current_code", "요약", "설명", "정리해줘");

        return plan.Take(maxPlanSteps).ToList();
    }

    private async Task GenerateProposalAsync(RunData run, CancellationToken ct, string? toolName)
    {
        if (run.Intent is null)
        {
            run.Proposal = new RunProposal { Summary = "의도 분석 실패" };
            return;
        }

        if (toolName is not null && run.Intent.Confidence >= 0.5)
        {
            var tool = _registry.GetTool(toolName);
            if (tool is not null)
            {
                if (IntentResolver.IsEditTool(toolName) && run.Files is { Count: > 0 })
                {
                    await GeneratePerFileProposalAsync(run, toolName, tool, ct);
                    return;
                }

                var filesContext = BuildFilesContext(run);
                if (IntentResolver.IsEditTool(toolName) && run.Code is not null)
                {
                    _logger.LogInformation(
                        "Run {RunId} RAG 시도: toolName={Tool}, activeFilePath={Path}, codeSize={Size}자",
                        run.RunId, toolName, run.ActiveFilePath ?? "(null)", run.Code.Length);
                    
                    var ragContext = await BuildRagContextAsync(
                        run.Message, 
                        run.ActiveFilePath ?? string.Empty, 
                        run.Code, 
                        run.ConversationId,
                        ResolveSolutionHashForSession(run),
                        CountLines(run.Code), 
                        ct);
                    
                    if (!string.IsNullOrWhiteSpace(ragContext))
                    {
                        _logger.LogInformation(
                            "Run {RunId} RAG Context 주입됨: {Size}자",
                            run.RunId, ragContext.Length);
                        filesContext = string.IsNullOrWhiteSpace(filesContext) ? ragContext : ragContext + "\n\n" + filesContext;
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Run {RunId} RAG Context 비어있음 (검색 결과 없음)",
                            run.RunId);
                    }
                }

                var arguments = new Dictionary<string, object?>
                {
                    ["code"] = run.Code ?? "",
                    ["language"] = run.Language ?? "",
                    ["files_context"] = filesContext
                };

                if (IntentResolver.IsEditTool(toolName))
                {
                    arguments["model"] = GetOptimalModelForFile(CountLines(run.Code ?? string.Empty));
                }

                var toolResult = await tool.ExecuteAsync(arguments, ct);
                var resultText = toolResult.Content.FirstOrDefault()?.Text ?? "(결과 없음)";
                _runLogger.LogToolExecution(run.RunId, toolName, resultText);

                if (IntentResolver.IsEditTool(toolName) && run.Code is not null)
                {
                    var modifiedCode = ExtractCodeFromResult(resultText);

                    // v2.2: [FILE:] 블록이 있으면 멀티 파일 모드
                    var fileChanges = ParseFileBlocks(resultText, run);
                    if (fileChanges.Count > 0)
                    {
                        if (IsOrganizeImportsTool(toolName) &&
                            !EnforceOrganizeImportsOnly(fileChanges, out var validationError))
                        {
                            run.Proposal = new RunProposal
                            {
                                Summary = $"using/import 정리 검증 실패: {validationError}",
                                RequiresApproval = false
                            };
                            return;
                        }

                        run.Proposal = new RunProposal
                        {
                            Summary = $"{toolName} 수정안 생성 ({fileChanges.Count}개 파일)",
                            Changes = fileChanges,
                            RequiresApproval = true
                        };
                    }
                    else if (!string.IsNullOrEmpty(filesContext))
                    {
                        // SC-4: 멀티파일 입력이 있었는데 [FILE:] 파싱 실패 → 1회 재시도
                        var remaining = GetRemainingRunBudget(run);
                        if (remaining <= MultiFileRetryMinBudget)
                        {
                            _logger.LogWarning(
                                "Run {RunId} 멀티파일 재시도 생략: 남은 예산 부족 (remaining={RemainingMs}ms)",
                                run.RunId,
                                Math.Max(0, (int)remaining.TotalMilliseconds));
                            run.Proposal = new RunProposal
                            {
                                Summary = "멀티파일 수정안 생성 중단: 실행 시간 예산이 부족합니다. " +
                                          "파일 수를 줄이거나 단일 파일로 다시 시도해 주세요.",
                                RequiresApproval = false
                            };
                            return;
                        }

                        _logger.LogWarning("Run {RunId} 멀티파일 파싱 실패, 출력 포맷 강제 후 재시도", run.RunId);
                        var retryArgs = new Dictionary<string, object?>(arguments)
                        {
                            ["files_context"] = filesContext +
                                "\n\n**필수**: 위 파일들은 반드시 [FILE: 경로]...전체 코드...[/FILE] 형식으로만 출력하라. 코드 블록 단독 출력 금지."
                        };
                        var retryResult = await tool.ExecuteAsync(retryArgs, ct);
                        var retryText = retryResult.Content.FirstOrDefault()?.Text ?? "";
                        var retryChanges = ParseFileBlocks(retryText, run);
                        _runLogger.LogToolRetry(run.RunId, toolName, retryText, retryChanges.Count > 0);

                        if (retryChanges.Count > 0)
                        {
                            if (IsOrganizeImportsTool(toolName) &&
                                !EnforceOrganizeImportsOnly(retryChanges, out var retryValidationError))
                            {
                                run.Proposal = new RunProposal
                                {
                                    Summary = $"using/import 정리 검증 실패: {retryValidationError}",
                                    RequiresApproval = false
                                };
                                return;
                            }

                            run.Proposal = new RunProposal
                            {
                                Summary = $"{toolName} 수정안 생성 ({retryChanges.Count}개 파일)",
                                Changes = retryChanges,
                                RequiresApproval = true
                            };
                        }
                        else
                        {
                            // 재시도 후에도 실패 → 명시적 실패 반환 (조용한 단건 폴백 금지)
                            _logger.LogError("Run {RunId} 멀티파일 재시도 후도 파싱 실패", run.RunId);
                            run.Proposal = new RunProposal
                            {
                                Summary = "멀티파일 수정안 생성 실패: 모델이 [FILE: 경로] 형식으로 응답하지 않았습니다. " +
                                          "단일 파일로 다시 요청하거나 더 큰 모델을 사용해 주세요.",
                                RequiresApproval = false
                            };
                        }
                    }
                    else
                    {
                        var singleModified = modifiedCode ?? resultText;
                        if (run.Code is not null &&
                            !ValidateMethodPreservationRate(run.Code, singleModified, out var preservationError))
                        {
                            run.Proposal = new RunProposal
                            {
                                Summary = $"메서드 보존율 검증 실패: {preservationError}",
                                RequiresApproval = false
                            };
                            return;
                        }

                        if (IsOrganizeImportsTool(toolName) &&
                            run.Code is not null &&
                            !EnforceOrganizeImportsSingle(run.Code, singleModified, out singleModified, out var singleValidationError))
                        {
                            run.Proposal = new RunProposal
                            {
                                Summary = $"using/import 정리 검증 실패: {singleValidationError}",
                                RequiresApproval = false
                            };
                            return;
                        }

                        run.Proposal = new RunProposal
                        {
                            Summary = $"{toolName} 수정안 생성",
                            Original = run.Code,
                            Modified = singleModified,
                            RequiresApproval = true
                        };
                    }
                }
                else
                {
                    run.Proposal = new RunProposal
                    {
                        Summary = resultText,
                        RequiresApproval = false
                    };
                }
            }
            else
            {
                run.Proposal = new RunProposal
                {
                    Summary = $"도구 '{toolName}'을(를) 찾을 수 없습니다."
                };
            }
        }
        else
        {
            var conversation = _store.Get(run.ConversationId);
            var history = conversation?.FormatHistoryForPrompt() ?? "(없음)";
            var chatResult = await _intent.GenerateChatResponseAsync(
                run.Message, run.Code, run.Language, history, ct);
            _runLogger.LogToolExecution(run.RunId, "general_chat", chatResult);

            run.Proposal = new RunProposal
            {
                Summary = chatResult,
                RequiresApproval = false
            };
        }
    }

    private async Task GeneratePerFileProposalAsync(RunData run, string toolName, IMcpTool tool, CancellationToken ct)
    {
        var fileChanges = new List<FileChange>();
        var changedFilePaths = new List<string>();

        foreach (var file in run.Files ?? [])
        {
            var fullCode = file.SelectedCode ?? file.Code;
            var sourceCode = fullCode;
            var language = file.Language ?? run.Language ?? "";
            var lineCount = CountLines(fullCode);
            var selectedModel = GetOptimalModelForFile(lineCount);
            var ragContext = string.Empty;

            // 6.3-1/3/4: 초대형 파일(2000줄 초과)은 메서드 단위 우선, 실패 시 라인 청크로 분할 처리.
            // 모든 청크가 성공할 때만 반영하는 트랜잭션 방식으로 원자성을 보장한다.
            if (!file.SelectionOnly && lineCount > 2000)
            {
                var chunked = await ProcessLargeFileWithTransactionAsync(run, toolName, tool, file, fullCode, language, selectedModel, ct);
                if (!chunked.success)
                {
                    run.Proposal = new RunProposal
                    {
                        Summary = $"대용량 파일 분할 처리 실패: {file.FilePath} — {chunked.error}",
                        RequiresApproval = false
                    };
                    return;
                }

                var chunkedModified = chunked.modifiedCode;
                if (!ValidateMethodPreservationRate(fullCode, chunkedModified, out var chunkPreservationError))
                {
                    run.Proposal = new RunProposal
                    {
                        Summary = $"메서드 보존율 검증 실패: {file.FilePath} — {chunkPreservationError}",
                        RequiresApproval = false
                    };
                    return;
                }

                if (TryAutoRecoverSyntax(language, chunkedModified, out var recoveredChunkCode))
                    chunkedModified = recoveredChunkCode;

                if (!string.Equals(fullCode, chunkedModified, StringComparison.Ordinal))
                {
                    fileChanges.Add(BuildFileChange(file, chunkedModified));
                    changedFilePaths.Add(file.FilePath);
                }

                continue;
            }

            if (!file.SelectionOnly)
            {
                ragContext = await BuildRagContextAsync(
                    run.Message,
                    file.FilePath,
                    sourceCode,
                    run.ConversationId,
                    ResolveSolutionHashForSession(run),
                    lineCount,
                    ct);
            }
            
            // v2.6.6: 파일 크기에 따라 전달 코드 길이를 동적으로 조절한다.
            var maxCodeForPerFile = GetOptimalMaxPerFileChars(lineCount, file.SelectionOnly);
            if (sourceCode.Length > maxCodeForPerFile)
            {
                sourceCode = sourceCode[..maxCodeForPerFile];
            }
            
            var arguments = new Dictionary<string, object?>
            {
                ["code"] = sourceCode,
                ["language"] = language,
                ["model"] = selectedModel,
                ["files_context"] = ragContext,
                ["related_files_context"] = BuildRelatedFilesContext(run, file.FilePath)
            };

            var toolResult = await tool.ExecuteAsync(arguments, ct);
            var resultText = toolResult.Content.FirstOrDefault()?.Text ?? "(결과 없음)";
            _runLogger.LogToolExecution(run.RunId, $"{toolName}:{Path.GetFileName(file.FilePath)}", resultText);

            var modifiedCode = ExtractCodeFromResult(resultText) ?? resultText;
            if (TryAutoRecoverSyntax(language, modifiedCode, out var recoveredCode))
                modifiedCode = recoveredCode;

            if (!file.SelectionOnly &&
                !ValidateMethodPreservationRate(fullCode, modifiedCode, out var preservationError))
            {
                run.Proposal = new RunProposal
                {
                    Summary = $"메서드 보존율 검증 실패: {file.FilePath} — {preservationError}",
                    RequiresApproval = false
                };
                return;
            }

            if (IsOrganizeImportsTool(toolName) &&
                !EnforceOrganizeImportsSingle(sourceCode, modifiedCode, out modifiedCode, out var validationError))
            {
                run.Proposal = new RunProposal
                {
                    Summary = $"using/import 정리 검증 실패: {file.FilePath} — {validationError}",
                    RequiresApproval = false
                };
                return;
            }

            if (string.Equals(sourceCode, modifiedCode, StringComparison.Ordinal))
                continue;

            fileChanges.Add(BuildFileChange(file, modifiedCode));
            changedFilePaths.Add(file.FilePath);
        }

        if (fileChanges.Count == 0)
        {
            run.Proposal = new RunProposal
            {
                Summary = $"{toolName} 변경 없음",
                RequiresApproval = false
            };
            return;
        }

        run.Proposal = new RunProposal
        {
            Summary = $"{toolName} 수정안 생성 ({fileChanges.Count}개 파일)",
            Changes = fileChanges,
            RequiresApproval = true
        };
    }

    private static void StartPause(RunData run)
    {
        if (run.PauseStartedAt is null)
            run.PauseStartedAt = DateTime.UtcNow;
    }

    private static void EndPause(RunData run)
    {
        if (run.PauseStartedAt is DateTime started)
        {
            run.PausedDuration += DateTime.UtcNow - started;
            run.PauseStartedAt = null;
        }
    }

    private static TimeSpan GetRemainingRunBudget(RunData run)
    {
        var elapsed = DateTime.UtcNow - run.CreatedAt - run.PausedDuration;
        if (run.PauseStartedAt is DateTime pauseStart)
            elapsed -= (DateTime.UtcNow - pauseStart);

        var remaining = RunTimeout - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private async Task GenerateFinalSummaryAsync(RunData run)
    {
        var s9 = run.GetStage(StageIds.FinalSummary);
        s9.Start();

        // 승인 불필요(요약/채팅)인 경우: Proposal.Summary가 이미 최종 결과
        // 별도 LLM 요약 호출 없이 바로 사용하여 시간과 리소스 절약
        if (run.Proposal is { RequiresApproval: false } && !string.IsNullOrEmpty(run.Proposal.Summary))
        {
            run.FinalSummary = run.Proposal.Summary;
            s9.Complete("요약 완료");

            var conv = _store.Get(run.ConversationId);
            conv?.AddMessage("assistant", run.FinalSummary);
            run.State = RunState.Completed;
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var summary = await _intent.GenerateSummaryAsync(
                run.Message,
                run.Intent ?? new IntentResult(),
                run.PlanItems,
                run.Proposal?.Summary,
                run.State != RunState.Rejected,
                cts.Token);

            run.FinalSummary = summary;
            s9.Complete("요약 완료");

            var conversation = _store.Get(run.ConversationId);
            conversation?.AddMessage("assistant", summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} 요약 생성 실패", run.RunId);
            run.FinalSummary = "작업이 완료되었습니다.";
            s9.Complete("요약 생성 실패 (기본값 사용)");
        }

        run.State = RunState.Completed;
        _runLogger.LogFinalSummary(run.RunId, run);
    }

    /// <summary>
    /// run.Files가 있을 때 LLM 프롬프트에 삽입할 멀티 파일 컨텍스트 문자열을 빌드한다.
    /// Files가 없거나 비어있으면 빈 문자열을 반환한다 (단건 모드 시 {{files_context}} 자리가 빈 문자열로 치환됨).
    /// </summary>
    // B-5: 파일별/전체 최대 문자 수 제한 (토큰 초과 대응)
    private const int MaxPerFileChars  = 8_000;   // 표준 파일
    private const int MediumPerFileChars = 16_000;
    private const int LargePerFileChars = 24_000; // 800줄 이상 대용량 파일
    private const int ChunkedPerFileChars = 12_000; // 2000줄 초과 청크 기준
    private const int MethodModeChunkLineLimit = 900;
    private const int LineChunkSize = 1200;
    private const int MaxTotalChars    = 32_000;  // ≈8000 tokens total
    private const int MaxRelatedFiles  = 1;       // v2.6.5: per-file 모드에서는 1개만
    private const int MaxRelatedChars  = 500;     // v2.6.5: 관련파일 요약 극소화

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return text.Count(c => c == '\n') + 1;
    }

    private static int GetOptimalMaxPerFileChars(int lineCount, bool selectionOnly)
    {
        if (selectionOnly)
            return MaxPerFileChars;

        if (lineCount > 2000)
            return ChunkedPerFileChars;
        if (lineCount >= 800)
            return LargePerFileChars;
        if (lineCount > 300)
            return MediumPerFileChars;

        return MaxPerFileChars;
    }

    private string GetOptimalModelForFile(int lineCount)
    {
        if (lineCount < 800)
            return _config.Llm.DefaultModel;

        var largeFileModel = _config.Llm.LargeFileModel;
        if (string.IsNullOrWhiteSpace(largeFileModel))
        {
            throw new InvalidOperationException(
                "대용량 파일(800줄 이상) 처리에는 Llm.LargeFileModel 설정이 필요합니다.");
        }

        return largeFileModel;
    }

    private static bool ValidateMethodPreservationRate(string beforeCode, string afterCode, out string error, double minRate = 0.8)
    {
        var beforeMethods = CountPublicMethods(beforeCode);
        var afterMethods = CountPublicMethods(afterCode);

        if (beforeMethods == 0)
        {
            error = string.Empty;
            return true;
        }

        var ratio = (double)afterMethods / beforeMethods;
        if (ratio < minRate)
        {
            error = $"공개 메서드 수 감소: {beforeMethods} -> {afterMethods} (비율 {ratio:P0}, 기준 {minRate:P0})";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static int CountPublicMethods(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return 0;

        var normalized = code.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var count = 0;

        foreach (var raw in lines)
        {
            var line = raw.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("*", StringComparison.Ordinal) ||
                line.StartsWith("/*", StringComparison.Ordinal))
                continue;

            if (!line.Contains("public ", StringComparison.Ordinal))
                continue;

            if (!line.Contains('(') || !line.Contains(')'))
                continue;

            if (line.Contains(" class ", StringComparison.Ordinal) ||
                line.Contains(" interface ", StringComparison.Ordinal) ||
                line.Contains(" struct ", StringComparison.Ordinal) ||
                line.Contains(" enum ", StringComparison.Ordinal))
                continue;

            count++;
        }

        return count;
    }

    private async Task<(bool success, string modifiedCode, string error)> ProcessLargeFileWithTransactionAsync(
        RunData run,
        string toolName,
        IMcpTool tool,
        FileContext file,
        string fullCode,
        string language,
        string selectedModel,
        CancellationToken ct)
    {
        var ragContext = await BuildRagContextAsync(
            run.Message,
            file.FilePath,
            fullCode,
            run.ConversationId,
            ResolveSolutionHashForSession(run),
            CountLines(fullCode),
            ct);
        var chunks = SplitIntoMethodModeChunks(fullCode, MethodModeChunkLineLimit);
        if (chunks.Count <= 1)
            chunks = SplitIntoLineChunks(fullCode, LineChunkSize);

        var chunkResults = new List<string>(chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var arguments = new Dictionary<string, object?>
            {
                ["code"] = chunk,
                ["language"] = language,
                ["model"] = selectedModel,
                ["files_context"] = ragContext,
                ["related_files_context"] = BuildRelatedFilesContext(run, file.FilePath)
            };

            var toolResult = await tool.ExecuteAsync(arguments, ct);
            var resultText = toolResult.Content.FirstOrDefault()?.Text ?? "(결과 없음)";
            var modifiedChunk = ExtractCodeFromResult(resultText) ?? resultText;

            if (TryAutoRecoverSyntax(language, modifiedChunk, out var recoveredChunk))
                modifiedChunk = recoveredChunk;

            if (IsOrganizeImportsTool(toolName) &&
                !EnforceOrganizeImportsSingle(chunk, modifiedChunk, out modifiedChunk, out var organizeErr))
            {
                return (false, string.Empty, $"청크 {i + 1}/{chunks.Count} using/import 검증 실패: {organizeErr}");
            }

            // 트랜잭션 성격: 모든 청크가 성공하기 전에는 원본에 반영하지 않는다.
            chunkResults.Add(modifiedChunk);
        }

        return (true, string.Join("\n", chunkResults), string.Empty);
    }

    private static List<string> SplitIntoLineChunks(string code, int chunkLineSize)
    {
        var lines = code.Split('\n');
        var chunks = new List<string>();

        for (int i = 0; i < lines.Length; i += chunkLineSize)
        {
            var take = Math.Min(chunkLineSize, lines.Length - i);
            chunks.Add(string.Join("\n", lines, i, take));
        }

        return chunks;
    }

    private static List<string> SplitIntoMethodModeChunks(string code, int maxChunkLines)
    {
        var lines = code.Split('\n');
        var boundaries = new List<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            if (LooksLikeMethodDeclaration(t))
                boundaries.Add(i);
        }

        if (boundaries.Count < 2)
            return SplitIntoLineChunks(code, maxChunkLines);

        var chunks = new List<string>();
        var start = 0;
        foreach (var methodStart in boundaries)
        {
            if (methodStart <= start)
                continue;

            var len = methodStart - start;
            if (len > maxChunkLines)
            {
                chunks.AddRange(SplitIntoLineChunks(string.Join("\n", lines, start, len), maxChunkLines));
            }
            else
            {
                chunks.Add(string.Join("\n", lines, start, len));
            }

            start = methodStart;
        }

        if (start < lines.Length)
            chunks.Add(string.Join("\n", lines, start, lines.Length - start));

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    private static bool LooksLikeMethodDeclaration(string line)
    {
        if (!(line.StartsWith("public ", StringComparison.Ordinal) ||
              line.StartsWith("private ", StringComparison.Ordinal) ||
              line.StartsWith("protected ", StringComparison.Ordinal) ||
              line.StartsWith("internal ", StringComparison.Ordinal)))
            return false;

        if (!line.Contains('(') || !line.Contains(')'))
            return false;

        if (line.Contains(" class ", StringComparison.Ordinal) ||
            line.Contains(" interface ", StringComparison.Ordinal) ||
            line.Contains(" struct ", StringComparison.Ordinal) ||
            line.Contains(" enum ", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool TryAutoRecoverSyntax(string language, string code, out string recovered)
    {
        recovered = code;
        if (!string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(language, "cs", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HasBalancedDelimiters(code))
            return false;

        var openCurly = code.Count(c => c == '{');
        var closeCurly = code.Count(c => c == '}');
        var openParen = code.Count(c => c == '(');
        var closeParen = code.Count(c => c == ')');
        var openBracket = code.Count(c => c == '[');
        var closeBracket = code.Count(c => c == ']');

        if (closeCurly > openCurly || closeParen > openParen || closeBracket > openBracket)
            return false;

        var sb = new System.Text.StringBuilder(code);
        for (int i = 0; i < openParen - closeParen; i++) sb.Append(')');
        for (int i = 0; i < openBracket - closeBracket; i++) sb.Append(']');
        for (int i = 0; i < openCurly - closeCurly; i++) sb.AppendLine().Append('}');

        recovered = sb.ToString();
        return HasBalancedDelimiters(recovered);
    }

    private static bool HasBalancedDelimiters(string code)
    {
        int curly = 0, paren = 0, bracket = 0;
        foreach (var ch in code)
        {
            switch (ch)
            {
                case '{': curly++; break;
                case '}': curly--; break;
                case '(': paren++; break;
                case ')': paren--; break;
                case '[': bracket++; break;
                case ']': bracket--; break;
            }

            if (curly < 0 || paren < 0 || bracket < 0)
                return false;
        }

        return curly == 0 && paren == 0 && bracket == 0;
    }

    private static string BuildFilesContext(RunData run)
    {
        if (run.Files == null || run.Files.Count == 0)
            return "";

        // B-5: 파일별 개별 제한 적용 후 전체 총량 재확인
        var fileContents = run.Files.Select(f =>
        {
            var code = f.SelectedCode ?? f.Code;
            bool truncated = false;
            if (code.Length > MaxPerFileChars)
            {
                code = code[..MaxPerFileChars];
                truncated = true;
            }
            return (f, code, truncated);
        }).ToList();

        int totalChars = fileContents.Sum(x => x.code.Length);
        if (totalChars > MaxTotalChars)
        {
            // 전체 초과 시 파일별 비율 분배
            double ratio = (double)MaxTotalChars / totalChars;
            fileContents = fileContents.Select(x =>
            {
                int limit = Math.Max(200, (int)(x.code.Length * ratio));
                if (x.code.Length > limit)
                    return (x.f, x.code[..limit], true);
                return x;
            }).ToList();
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## 멀티 파일 수정 모드");
        sb.AppendLine();
        sb.AppendLine("여러 파일을 수정해야 하는 경우, 각 파일의 **전체 수정 코드**를 아래 형식으로 감싸서 반환한다:");
        sb.AppendLine();
        sb.AppendLine("[FILE: 파일경로]");
        sb.AppendLine("수정된 전체 코드");
        sb.AppendLine("[/FILE]");
        sb.AppendLine();
        sb.AppendLine("수정이 필요 없는 파일은 블록에 포함하지 않는다.");
        sb.AppendLine();
        sb.AppendLine("### 제공된 파일 목록:");
        sb.AppendLine();
        foreach (var (f, code, truncated) in fileContents)
        {
            sb.AppendLine($"[FILE: {f.FilePath}]");
            sb.AppendLine($"```{f.Language ?? ""}");
            sb.AppendLine(code);
            if (truncated) sb.AppendLine("// ... (이하 생략 — 토큰 제한으로 잘림)");
            sb.AppendLine("```");
            sb.AppendLine("[/FILE]");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildRelatedFilesContext(RunData run, string currentFilePath)
    {
        if (run.Files == null || run.Files.Count <= 1)
            return string.Empty;

        var relatedFiles = run.Files
            .Where(f => !f.FilePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase))
            .Take(MaxRelatedFiles)
            .ToList();
        if (relatedFiles.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 관련 파일 요약");
        sb.AppendLine("다음 파일들은 참고용이다. 이 요청에서는 현재 파일만 수정하고, 다른 파일 코드는 출력하지 않는다.");
        sb.AppendLine();

        foreach (var file in relatedFiles)
        {
            sb.AppendLine($"### {file.FilePath}");
            var summary = SummarizeFileForContext(file.SelectedCode ?? file.Code);
            sb.AppendLine(summary);
            sb.AppendLine();

            if (sb.Length >= MaxRelatedChars)
                break;
        }

        if (sb.Length > MaxRelatedChars)
            return sb.ToString(0, MaxRelatedChars) + "\n...(생략)";

        return sb.ToString();
    }

    private async Task<string> BuildRagContextAsync(
        string query,
        string currentFilePath,
        string sourceCode,
        string? conversationId,
        string? solutionHash,
        int lineCount,
        CancellationToken ct)
    {
        var rag = _config.Rag;
        
        // RAG 조건 확인
        if (!rag.Enabled)
        {
            _logger.LogInformation("RAG Context 스킵: RAG 비활성화 (설정)");
            return string.Empty;
        }
        
        if (lineCount < rag.MinFileLineCount && sourceCode.Length < rag.MinFileCharCount)
        {
            _logger.LogInformation(
                "RAG Context 스킵: 파일줄수 {LineCount} < 최소 {MinLine}줄 AND 코드길이 {CodeSize} < 최소 {MinChars}자",
                lineCount,
                rag.MinFileLineCount,
                sourceCode.Length,
                rag.MinFileCharCount);
            return string.Empty;
        }

        try
        {
            var searchQuery = string.IsNullOrWhiteSpace(query) ? sourceCode : query;
            var chunks = await _vectorSearchEngine.SearchAsync(
                searchQuery,
                rag.TopKChunks,
                rag.SimilarityThreshold,
                conversationId,
                solutionHash,
                ct);
            
            if (chunks.Count == 0)
            {
                _logger.LogWarning("RAG 검색 결과 없음: 쿼리='{Query}' (길이={Len}), 유사도기준={Threshold}, 저장소상태={EmbeddingStoreReady}",
                    searchQuery.Length > 100 ? searchQuery[..100] + "..." : searchQuery, 
                    searchQuery.Length, 
                    rag.SimilarityThreshold,
                    _cache.IsAvailable ? "준비됨" : "미준비");
                return string.Empty;
            }

            var preferred = chunks
                .Where(chunk => !string.IsNullOrWhiteSpace(currentFilePath) &&
                                !chunk.Chunk.FilePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (preferred.Count == 0)
                preferred = chunks;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## RAG 관련 코드");
            sb.AppendLine("다음 조각은 의미 기반 검색으로 선택한 참고용 컨텍스트다. 현재 코드와 함께 읽고, 불필요한 다른 파일 수정은 하지 않는다.");
            sb.AppendLine();

            foreach (var chunk in preferred)
            {
                if (sb.Length >= rag.MaxContextChars)
                    break;

                sb.AppendLine($"### {chunk.Chunk.FilePath}");
                sb.AppendLine($"Lines: {chunk.Chunk.StartLine}-{chunk.Chunk.EndLine}");
                sb.AppendLine($"Similarity: {chunk.Similarity:F2}");
                if (!string.IsNullOrWhiteSpace(chunk.Chunk.Summary))
                    sb.AppendLine($"Summary: {chunk.Chunk.Summary}");
                sb.AppendLine("```csharp");
                sb.AppendLine(chunk.Chunk.Content.TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (sb.Length == 0)
                return string.Empty;

            var result = sb.Length > rag.MaxContextChars
                ? sb.ToString(0, rag.MaxContextChars) + "\n...(생략)"
                : sb.ToString();
            
            _logger.LogInformation("RAG Context 조립 완료: {Chunks}개 청크, {Size}자", preferred.Count, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAG Context 조립 실패: {Error}", ex.Message);
            return string.Empty;
        }
    }

    private static string SummarizeFileForContext(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "(코드 없음)";

        var normalized = code.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var picked = new List<string>();

        // v2.6.5: 관련파일 요약은 namespace/class 선언만 추출 (매우 제한적)
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith("namespace ", StringComparison.Ordinal) ||
                (trimmed.Contains(" class ") && !trimmed.StartsWith("//")) ||
                (trimmed.Contains(" interface ") && !trimmed.StartsWith("//")) ||
                (trimmed.Contains(" struct ") && !trimmed.StartsWith("//")))
            {
                picked.Add(trimmed);
            }

            if (picked.Count >= 3)  // 최대 3줄만
                break;
        }

        if (picked.Count == 0)
        {
            // 관련 선언을 찾을 수 없으면 빈 줄로
            return "(코드 참고만)";
        }

        return string.Join("\n", picked);
    }

    private string? ResolveSolutionHashForSession(RunData run)
    {
        var source = !string.IsNullOrWhiteSpace(run.SolutionPath)
            ? run.SolutionPath
            : !string.IsNullOrWhiteSpace(_cache.CurrentIndexRoot)
                ? _cache.CurrentIndexRoot
                : _config.CodeIndex.RootPath;

        if (string.IsNullOrWhiteSpace(source))
            return null;

        return source.Replace('\\', '/');
    }

    private static bool IsRelevantContextLine(string line)
    {
        if (line.StartsWith("namespace ", StringComparison.Ordinal) ||
            line.Contains(" class ", StringComparison.Ordinal) ||
            line.Contains(" struct ", StringComparison.Ordinal) ||
            line.Contains(" interface ", StringComparison.Ordinal) ||
            line.Contains(" enum ", StringComparison.Ordinal))
            return true;

        if (!line.Contains('(', StringComparison.Ordinal) || !line.Contains(')', StringComparison.Ordinal))
            return false;

        return !line.StartsWith("if ", StringComparison.Ordinal) &&
               !line.StartsWith("for ", StringComparison.Ordinal) &&
               !line.StartsWith("foreach ", StringComparison.Ordinal) &&
               !line.StartsWith("while ", StringComparison.Ordinal) &&
               !line.StartsWith("switch ", StringComparison.Ordinal) &&
               !line.StartsWith("catch", StringComparison.Ordinal);
    }

    /// <summary>
    /// LLM 응답에서 [FILE: 경로]...[/FILE] 블록을 파싱하여 FileChange 목록을 반환한다.
    /// 1차: 표준 [FILE:]...[/FILE] 패턴 / 2차 폴백: ### 파일경로 + 코드 펜스 패턴.
    /// 블록이 없으면 빈 목록을 반환한다 (단건 폴백).
    /// </summary>
    private static List<FileChange> ParseFileBlocks(string llmOutput, RunData run)
    {
        // 1차: 표준 패턴 [FILE: path]...[/FILE]
        var result = ParseFileBlocksStandard(llmOutput, run);
        if (result.Count > 0) return result;

        // 2차 폴백: ### path.ext 또는 **path.ext** 마크다운 헤딩 + 코드 펜스
        result = ParseFileBlocksMarkdownHeading(llmOutput, run);
        return result;
    }

    private static bool IsOrganizeImportsTool(string? toolName) =>
        string.Equals(toolName, "organize_imports", StringComparison.OrdinalIgnoreCase);

    private static bool EnforceOrganizeImportsOnly(List<FileChange> changes, out string error)
    {
        foreach (var change in changes)
        {
            if (!EnforceOrganizeImportsSingle(change.Original, change.Modified, out var sanitized, out error))
            {
                error = $"{change.FilePath} — {error}";
                return false;
            }

            change.Modified = sanitized;
            var recomputed = DiffEngine.Compute(change.Original, change.Modified);
            change.Hunks = recomputed.Count > 0 ? [.. recomputed] : null;
        }

        error = string.Empty;
        return true;
    }

    private static bool EnforceOrganizeImportsSingle(string original, string modified, out string sanitized, out string error)
    {
        if (ValidateOrganizeImportsSingle(original, modified))
        {
            sanitized = modified;
            error = string.Empty;
            return true;
        }

        // 모델이 본문까지 바꿔도 import 블록만 투영해 복구를 시도한다.
        if (TryProjectImportsOnly(original, modified, out var projected) &&
            ValidateOrganizeImportsSingle(original, projected))
        {
            sanitized = projected;
            error = string.Empty;
            return true;
        }

        sanitized = modified;
        error = "using/import 외 코드가 변경되었습니다.";
        return false;
    }

    private static bool ValidateOrganizeImportsSingle(string original, string modified)
    {
        var normalizedOriginalBody = NormalizeBodyWithoutUsingLines(original);
        var normalizedModifiedBody = NormalizeBodyWithoutUsingLines(modified);

        return string.Equals(normalizedOriginalBody, normalizedModifiedBody, StringComparison.Ordinal);
    }

    private static bool TryProjectImportsOnly(string original, string modified, out string projected)
    {
        projected = original;

        var origLines = SplitLinesKeepEol(original);
        var modLines  = SplitLinesKeepEol(modified);
        if (origLines.Count == 0 || modLines.Count == 0)
            return false;

        var (origFirst, origLast) = FindTopImportRange(origLines);
        if (origFirst < 0 || origLast < origFirst)
            return false;

        var modImports = CollectTopImportLines(modLines);
        if (modImports.Count == 0)
            return false;

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var dedup = new HashSet<string>(StringComparer.Ordinal);
        var normalizedImports = new List<string>();
        foreach (var imp in modImports)
        {
            var key = imp.Trim();
            if (!dedup.Add(key)) continue;
            normalizedImports.Add(imp.TrimEnd('\r', '\n') + newline);
        }

        var sb = new System.Text.StringBuilder(original.Length + 256);
        for (int i = 0; i < origFirst; i++) sb.Append(origLines[i]);
        foreach (var imp in normalizedImports) sb.Append(imp);
        for (int i = origLast + 1; i < origLines.Count; i++) sb.Append(origLines[i]);

        projected = sb.ToString();
        return true;
    }

    private static (int first, int last) FindTopImportRange(List<string> lines)
    {
        int preambleEnd = FindPreambleEnd(lines);
        int first = -1, last = -1;
        for (int i = 0; i < preambleEnd; i++)
        {
            if (!IsImportDirective(lines[i])) continue;
            if (first < 0) first = i;
            last = i;
        }
        return (first, last);
    }

    private static List<string> CollectTopImportLines(List<string> lines)
    {
        int preambleEnd = FindPreambleEnd(lines);
        var imports = new List<string>();
        for (int i = 0; i < preambleEnd; i++)
            if (IsImportDirective(lines[i]))
                imports.Add(lines[i]);
        return imports;
    }

    private static int FindPreambleEnd(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0 ||
                t.StartsWith("//", StringComparison.Ordinal) ||
                t.StartsWith("/*", StringComparison.Ordinal) ||
                t.StartsWith("*", StringComparison.Ordinal) ||
                t.StartsWith("#", StringComparison.Ordinal) ||
                IsImportDirective(lines[i]))
                continue;

            return i;
        }

        return lines.Count;
    }

    private static bool IsImportDirective(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("using ", StringComparison.Ordinal) ||
               t.StartsWith("global using ", StringComparison.Ordinal) ||
               t.StartsWith("extern alias ", StringComparison.Ordinal);
    }

    private static List<string> SplitLinesKeepEol(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;

        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text.Substring(start));

        return lines;
    }

    private static string NormalizeBodyWithoutUsingLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("using ", StringComparison.Ordinal) ||
                t.StartsWith("global using ", StringComparison.Ordinal) ||
                t.StartsWith("extern alias ", StringComparison.Ordinal))
            {
                continue;
            }

            // using 블록 주변 공백 차이는 무시하기 위해 빈 줄 제거
            if (string.IsNullOrWhiteSpace(line))
                continue;

            kept.Add(line.TrimEnd());
        }

        return string.Join("\n", kept);
    }

    private static List<FileChange> ParseFileBlocksStandard(string llmOutput, RunData run)
    {
        var result = new List<FileChange>();
        var pattern = new System.Text.RegularExpressions.Regex(
            @"\[FILE:\s*(?<path>[^\]]+)\](?<code>[\s\S]*?)\[/FILE\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var matches = pattern.Matches(llmOutput);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var filePath = m.Groups["path"].Value.Trim();
            var modifiedCode = m.Groups["code"].Value.Trim();

            var extracted = ExtractCodeFromResult(modifiedCode);
            if (extracted != null) modifiedCode = extracted;

            result.Add(BuildFileChange(filePath, modifiedCode, run));
        }

        return result;
    }

    /// <summary>
    /// B-4 폴백: LLM이 [FILE:] 블록 대신 마크다운 헤딩 형식으로 파일 경로를 출력했을 때 파싱한다.
    /// 지원 패턴:
    ///   ### path/to/File.cs          (헤딩 + 코드 펜스)
    ///   **path/to/File.cs**          (볼드 + 코드 펜스)
    ///   // File: path/to/File.cs     (주석 스타일)
    /// </summary>
    private static List<FileChange> ParseFileBlocksMarkdownHeading(string llmOutput, RunData run)
    {
        var result = new List<FileChange>();

        // 파일 경로처럼 보이는 마크다운 구분자 → 뒤에 오는 코드 펜스를 연결
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?:#{1,4}\s+|(?:\*\*)|//\s*[Ff]ile:\s*)" +  // 헤딩/볼드/주석
            @"(?<path>[\w./\\-]+\.\w{1,6})" +               // 파일경로 (확장자 필수)
            @"(?:\*\*)?\s*\n+" +                             // 헤딩 끝
            @"```(?:\w*)\n(?<code>[\s\S]*?)```",            // 코드 펜스
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var matches = pattern.Matches(llmOutput);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var filePath = m.Groups["path"].Value.Trim();
            var modifiedCode = m.Groups["code"].Value.TrimEnd();
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(modifiedCode))
                continue;

            result.Add(BuildFileChange(filePath, modifiedCode, run));
        }

        return result;
    }

    private static FileChange BuildFileChange(string filePath, string modifiedCode, RunData run)
    {
        string originalCode = run.Code ?? "";
        bool selectionOnly = run.SelectionOnly;

        if (run.Files != null)
        {
            var match = run.Files.Find(f =>
                f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetFileName(f.FilePath).Equals(
                    System.IO.Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                originalCode = match.SelectedCode ?? match.Code;
                selectionOnly = match.SelectionOnly;
            }
        }

        // A-2: 서버 측 hunks 사전 계산 — VSIX 재계산 중복 제거
        DiffHunkInfo[]? hunks = null;
        if (!string.IsNullOrEmpty(originalCode) && !string.IsNullOrEmpty(modifiedCode))
        {
            var computed = DiffEngine.Compute(originalCode, modifiedCode);
            if (computed.Count > 0)
                hunks = [.. computed];
        }

        return new FileChange
        {
            FilePath = filePath,
            Original = originalCode,
            Modified = modifiedCode,
            SelectionOnly = selectionOnly,
            IsNewFile = string.IsNullOrEmpty(originalCode),
            Description = null,
            Hunks = hunks
        };
    }

    private static FileChange BuildFileChange(FileContext file, string modifiedCode)
    {
        var originalCode = file.SelectedCode ?? file.Code;

        DiffHunkInfo[]? hunks = null;
        if (!string.IsNullOrEmpty(originalCode) && !string.IsNullOrEmpty(modifiedCode))
        {
            var computed = DiffEngine.Compute(originalCode, modifiedCode);
            if (computed.Count > 0)
                hunks = [.. computed];
        }

        return new FileChange
        {
            FilePath = file.FilePath,
            Original = originalCode,
            Modified = modifiedCode,
            SelectionOnly = file.SelectionOnly,
            IsNewFile = string.IsNullOrEmpty(originalCode),
            Description = null,
            Hunks = hunks
        };
    }

    private static string? ExtractCodeFromResult(string result)
    {
        // 1. 코드 펜스 블록에서 마지막(가장 큰) 코드 블록 추출
        var lastFenceStart = -1;
        var lastFenceEnd = -1;
        var largestLen = 0;
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

            // 가장 큰 코드 블록을 선택 (설명 코드 스니펫이 아닌 전체 코드 블록)
            var blockLen = end - contentStart;
            if (blockLen > largestLen)
            {
                largestLen = blockLen;
                lastFenceStart = contentStart;
                lastFenceEnd = end;
            }
            searchFrom = end + 3;
        }

        if (lastFenceStart >= 0 && lastFenceEnd > lastFenceStart)
            return result[lastFenceStart..lastFenceEnd].TrimEnd();

        // 2. 코드 펜스가 없는 경우: 설명 텍스트가 앞에 있으면 코드 부분만 추출 시도
        // "### " 또는 "##" 헤딩 이후 코드가 시작되는 패턴 감지
        var lines = result.Split('\n');
        var lastHeadingEnd = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart().StartsWith("###") || lines[i].TrimStart().StartsWith("##"))
            {
                lastHeadingEnd = i + 1;
                break;
            }
        }

        if (lastHeadingEnd > 0 && lastHeadingEnd < lines.Length)
        {
            var codeLines = lines[lastHeadingEnd..];
            var codeText = string.Join('\n', codeLines).Trim();
            if (codeText.Length > 0)
                return codeText;
        }

        return null;
    }
}
