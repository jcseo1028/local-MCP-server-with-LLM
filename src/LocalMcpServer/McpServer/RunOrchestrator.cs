using LocalMcpServer.ResourceCache;
using LocalMcpServer.ToolRegistry;

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
    private readonly ILogger<RunOrchestrator> _logger;

    public RunOrchestrator(
        IConversationStore store,
        IntentResolver intent,
        DocumentSearcher docSearcher,
        ToolRegistryService registry,
        IResourceCache cache,
        ILogger<RunOrchestrator> logger)
    {
        _store = store;
        _intent = intent;
        _docSearcher = docSearcher;
        _registry = registry;
        _cache = cache;
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
            SolutionPath = req.SolutionPath
        };

        _store.AddRun(run);

        // SolutionPath가 있고 현재 인덱스 루트와 다르면 백그라운드 재인덱싱
        if (!string.IsNullOrEmpty(req.SolutionPath))
        {
            _ = _cache.ReindexAsync(req.SolutionPath);
        }

        _ = Task.Run(() => ExecutePipelineAsync(run));

        return run;
    }

    /// <summary>
    /// 승인/거부 처리 후 나머지 파이프라인을 진행한다.
    /// </summary>
    public void ProcessApproval(RunData run, bool approved)
    {
        var stage = run.GetStage(StageIds.Approval);

        if (approved)
        {
            stage.Complete("승인됨");
            run.State = RunState.Running;
            _ = Task.Run(() => ExecutePostApprovalAsync(run));
        }
        else
        {
            stage.Fail("거부됨");
            run.State = RunState.Rejected;

            var summary = run.GetStage(StageIds.FinalSummary);
            summary.Start();
            summary.Complete("사용자가 변경을 거부했습니다.");
            run.FinalSummary = "사용자가 변경을 거부했습니다.";
        }
    }

    /// <summary>
    /// VSIX에서 build/test 결과를 수신하여 파이프라인을 완료한다.
    /// </summary>
    public async Task ProcessClientResultAsync(RunData run, ChatRunClientResultRequest result)
    {
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

        // 최종 요약
        await GenerateFinalSummaryAsync(run);
    }

    private async Task ExecutePipelineAsync(RunData run)
    {
        try
        {
            run.State = RunState.Running;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ct = cts.Token;

            // 1. 의도 분석
            var s1 = run.GetStage(StageIds.IntentAnalysis);
            s1.Start();
            run.Intent = await _intent.AnalyzeIntentAsync(run.Message, run.Language, ct);
            s1.Complete($"tool={run.Intent.ToolName ?? "chat"}, confidence={run.Intent.Confidence:F2}");

            // 2. 계획 수립
            var s2 = run.GetStage(StageIds.Planning);
            s2.Start();
            run.PlanItems = await _intent.GeneratePlanAsync(run.Intent, run.Message, run.Code, run.Language, ct);
            s2.Complete($"{run.PlanItems.Count}개 항목");

            // 3. 컨텍스트 수집 — §8c 컨텍스트 검증
            var s3 = run.GetStage(StageIds.ContextCollection);
            s3.Start();
            if (run.Code is not null)
            {
                const int maxCodeLength = 32_000; // LLM 컨텍스트 윈도우 보호
                var originalLength = run.Code.Length;
                if (originalLength > maxCodeLength)
                {
                    run.Code = run.Code[..maxCodeLength];
                    _logger.LogWarning("Run {RunId} 코드 절단: {Original}자 → {Max}자",
                        run.RunId, originalLength, maxCodeLength);
                    s3.Complete($"코드 컨텍스트 수집 완료 ({originalLength}자 → {maxCodeLength}자 절단)");
                }
                else
                {
                    s3.Complete($"코드 컨텍스트 수집 완료 ({originalLength}자)");
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
            await GenerateProposalAsync(run, ct);
            s5.Complete(run.Proposal?.Summary ?? "응답 생성 완료");

            // 6. 승인 단계 — 코드 수정 도구인 경우만
            var s6 = run.GetStage(StageIds.Approval);
            if (run.Proposal is { RequiresApproval: true })
            {
                s6.Start();
                run.State = RunState.WaitingForApproval;
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
            _logger.LogWarning("Run {RunId} 취소됨 (타임아웃 또는 서버 종료)", run.RunId);
            run.State = RunState.Failed;
            run.Error = "작업이 취소되었습니다. LLM 응답 시간이 초과되었거나 서버가 종료되었습니다.";

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
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

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

    private async Task GenerateProposalAsync(RunData run, CancellationToken ct)
    {
        if (run.Intent is null)
        {
            run.Proposal = new RunProposal { Summary = "의도 분석 실패" };
            return;
        }

        if (run.Intent.ToolName is not null && run.Intent.Confidence >= 0.5)
        {
            var tool = _registry.GetTool(run.Intent.ToolName);
            if (tool is not null)
            {
                var arguments = new Dictionary<string, object?>
                {
                    ["code"] = run.Code ?? "",
                    ["language"] = run.Language ?? ""
                };

                var toolResult = await tool.ExecuteAsync(arguments, ct);
                var resultText = toolResult.Content.FirstOrDefault()?.Text ?? "(결과 없음)";

                if (IntentResolver.IsEditTool(run.Intent.ToolName) && run.Code is not null)
                {
                    var modifiedCode = ExtractCodeFromResult(resultText);
                    run.Proposal = new RunProposal
                    {
                        Summary = $"{run.Intent.ToolName} 수정안 생성",
                        Original = run.Code,
                        Modified = modifiedCode ?? resultText,
                        RequiresApproval = true
                    };
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
                    Summary = $"도구 '{run.Intent.ToolName}'을(를) 찾을 수 없습니다."
                };
            }
        }
        else
        {
            var conversation = _store.Get(run.ConversationId);
            var history = conversation?.FormatHistoryForPrompt() ?? "(없음)";
            var chatResult = await _intent.GenerateChatResponseAsync(
                run.Message, run.Code, run.Language, history, ct);

            run.Proposal = new RunProposal
            {
                Summary = chatResult,
                RequiresApproval = false
            };
        }
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
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
