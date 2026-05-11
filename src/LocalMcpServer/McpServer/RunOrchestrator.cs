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
    private readonly ILogger<RunOrchestrator> _logger;
    private readonly RunLogger _runLogger;
    private readonly ChatSection _chatConfig;

    public RunOrchestrator(
        IConversationStore store,
        IntentResolver intent,
        DocumentSearcher docSearcher,
        ToolRegistryService registry,
        IResourceCache cache,
        IOptions<ServerConfig> config,
        RunLogger runLogger,
        ILogger<RunOrchestrator> logger)
    {
        _store = store;
        _intent = intent;
        _docSearcher = docSearcher;
        _registry = registry;
        _cache = cache;
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
            Files = req.Files  // v2.2 멀티 파일 컨텍스트
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
            _runLogger.LogIntentResult(run.RunId, run.Intent);

            // 2. 계획 수립
            var s2 = run.GetStage(StageIds.Planning);
            s2.Start();
            var planResult = await _intent.GeneratePlanAsync(run.Intent, run.Message, run.Code, run.Language, ct);
            run.PlanItems = planResult.Items;
            run.PlanRawLlmResponse = planResult.RawResponse;
            s2.Complete($"{run.PlanItems.Count}개 항목");
            _runLogger.LogPlan(run.RunId, run.PlanItems, run.PlanRawLlmResponse);

            if (run.IntentAndPlanOnly)
            {
                CompleteIntentAndPlanOnlyRun(run);
                return;
            }

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
                var filesContext = BuildFilesContext(run);
                var arguments = new Dictionary<string, object?>
                {
                    ["code"] = run.Code ?? "",
                    ["language"] = run.Language ?? "",
                    ["files_context"] = filesContext
                };

                var toolResult = await tool.ExecuteAsync(arguments, ct);
                var resultText = toolResult.Content.FirstOrDefault()?.Text ?? "(결과 없음)";
                _runLogger.LogToolExecution(run.RunId, run.Intent.ToolName, resultText);

                if (IntentResolver.IsEditTool(run.Intent.ToolName) && run.Code is not null)
                {
                    var modifiedCode = ExtractCodeFromResult(resultText);

                    // v2.2: [FILE:] 블록이 있으면 멀티 파일 모드
                    var fileChanges = ParseFileBlocks(resultText, run);
                    if (fileChanges.Count > 0)
                    {
                        if (IsOrganizeImportsTool(run.Intent.ToolName) &&
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
                            Summary = $"{run.Intent.ToolName} 수정안 생성 ({fileChanges.Count}개 파일)",
                            Changes = fileChanges,
                            RequiresApproval = true
                        };
                    }
                    else if (!string.IsNullOrEmpty(filesContext))
                    {
                        // SC-4: 멀티파일 입력이 있었는데 [FILE:] 파싱 실패 → 1회 재시도
                        _logger.LogWarning("Run {RunId} 멀티파일 파싱 실패, 출력 포맷 강제 후 재시도", run.RunId);
                        var retryArgs = new Dictionary<string, object?>(arguments)
                        {
                            ["files_context"] = filesContext +
                                "\n\n**필수**: 위 파일들은 반드시 [FILE: 경로]...전체 코드...[/FILE] 형식으로만 출력하라. 코드 블록 단독 출력 금지."
                        };
                        var retryResult = await tool.ExecuteAsync(retryArgs, ct);
                        var retryText = retryResult.Content.FirstOrDefault()?.Text ?? "";
                        var retryChanges = ParseFileBlocks(retryText, run);

                        if (retryChanges.Count > 0)
                        {
                            if (IsOrganizeImportsTool(run.Intent.ToolName) &&
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
                                Summary = $"{run.Intent.ToolName} 수정안 생성 ({retryChanges.Count}개 파일)",
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
                        if (IsOrganizeImportsTool(run.Intent.ToolName) &&
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
                            Summary = $"{run.Intent.ToolName} 수정안 생성",
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
            _runLogger.LogToolExecution(run.RunId, "general_chat", chatResult);

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
        _runLogger.LogFinalSummary(run.RunId, run);
    }

    /// <summary>
    /// run.Files가 있을 때 LLM 프롬프트에 삽입할 멀티 파일 컨텍스트 문자열을 빌드한다.
    /// Files가 없거나 비어있으면 빈 문자열을 반환한다 (단건 모드 시 {{files_context}} 자리가 빈 문자열로 치환됨).
    /// </summary>
    // B-5: 파일별/전체 최대 문자 수 제한 (토큰 초과 대응)
    private const int MaxPerFileChars  = 8_000;   // ≈2000 tokens/file
    private const int MaxTotalChars    = 32_000;  // ≈8000 tokens total

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
