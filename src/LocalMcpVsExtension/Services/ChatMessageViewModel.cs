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
