using System;
using System.Collections.Generic;

namespace LocalMcpVsExtension.Services
{
    internal enum ChatMessageRole
    {
        User,
        Assistant,
        System
    }

    internal enum ApprovalState
    {
        None,
        Pending,
        Approved,
        Rejected
    }

    internal enum ChatRunState
    {
        Queued,
        Running,
        WaitingForApproval,
        Completed,
        Rejected,
        Failed
    }

    internal enum ChatStageStatus
    {
        Pending,
        InProgress,
        Completed,
        Skipped,
        Failed
    }

    internal sealed class ChatRunStageViewModel
    {
        public string StageId { get; set; } = "";
        public string Title { get; set; } = "";
        public ChatStageStatus Status { get; set; } = ChatStageStatus.Pending;
        public string? Message { get; set; }

        public static ChatStageStatus ParseStatus(string? status)
        {
            switch (status)
            {
                case "Completed": return ChatStageStatus.Completed;
                case "InProgress": return ChatStageStatus.InProgress;
                case "Skipped": return ChatStageStatus.Skipped;
                case "Failed": return ChatStageStatus.Failed;
                default: return ChatStageStatus.Pending;
            }
        }
    }

    /// <summary>Run 단위 실행 상태 ViewModel (§9c)</summary>
    internal sealed class ChatRunViewModel
    {
        public string RunId { get; set; } = "";
        public ChatRunState State { get; set; } = ChatRunState.Queued;
        public List<ChatRunStageViewModel> Stages { get; set; } = new List<ChatRunStageViewModel>();
        public List<string> PlanItems { get; set; } = new List<string>();
        public List<RunReferenceDto> References { get; set; } = new List<RunReferenceDto>();
        public string? ProposalSummary { get; set; }
        public CodeChangeInfo? CodeChange { get; set; }
        public string? FinalSummary { get; set; }
        public string? Error { get; set; }

        public static ChatRunState ParseState(string? state)
        {
            switch (state)
            {
                case "Running": return ChatRunState.Running;
                case "WaitingForApproval": return ChatRunState.WaitingForApproval;
                case "Completed": return ChatRunState.Completed;
                case "Rejected": return ChatRunState.Rejected;
                case "Failed": return ChatRunState.Failed;
                default: return ChatRunState.Queued;
            }
        }

        public void UpdateFrom(RunSnapshot snapshot)
        {
            State = ParseState(snapshot.State);
            PlanItems = new List<string>(snapshot.PlanItems ?? Array.Empty<string>());
            References = new List<RunReferenceDto>(snapshot.References ?? Array.Empty<RunReferenceDto>());
            ProposalSummary = snapshot.Proposal?.Summary;
            FinalSummary = snapshot.FinalSummary;
            Error = snapshot.Error;

            Stages.Clear();
            if (snapshot.Stages != null)
            {
                foreach (var s in snapshot.Stages)
                {
                    Stages.Add(new ChatRunStageViewModel
                    {
                        StageId = s.StageId,
                        Title = s.Title,
                        Status = ChatRunStageViewModel.ParseStatus(s.Status),
                        Message = s.Message
                    });
                }
            }
        }
    }

    internal sealed class ChatMessageViewModel
    {
        public ChatMessageRole Role { get; set; }
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool HasCodeChange { get; set; }
        public CodeChangeInfo? CodeChange { get; set; }
        public ApprovalState Approval { get; set; } = ApprovalState.None;

        /// <summary>의도 분석 결과 요약 (봇 메시지에 표시)</summary>
        public string? IntentSummary { get; set; }

        /// <summary>Run API 관련: 연결된 runId</summary>
        public string? RunId { get; set; }

        /// <summary>Run API 관련: 실행 ViewModel</summary>
        public ChatRunViewModel? RunViewModel { get; set; }

        /// <summary>UI 요소 참조 (Border 등)</summary>
        public object? Tag { get; set; }

        /// <summary>승인/거부 버튼 패널 참조</summary>
        public object? ApprovalPanel { get; set; }
    }

    internal sealed class CodeChangeInfo
    {
        public string Original { get; set; } = "";
        public string Modified { get; set; } = "";
        public string ToolName { get; set; } = "";
        public bool SelectionOnly { get; set; }
    }

    /// <summary>과거 대화 세션 백업</summary>
    internal sealed class ChatSession
    {
        public string ConversationId { get; set; }
        public string Title { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public List<ChatMessageViewModel> Messages { get; set; } = new List<ChatMessageViewModel>();

        public override string ToString()
        {
            return $"[{CreatedAt:HH:mm}] {Title}";
        }
    }
}
