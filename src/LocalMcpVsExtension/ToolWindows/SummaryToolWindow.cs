using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace LocalMcpVsExtension.ToolWindows
{
    public class SummaryToolWindow : BaseToolWindow<SummaryToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Local MCP 코드 요약";

        public override Type PaneType => typeof(Pane);

        public override async Task<FrameworkElement> CreateAsync(
            int toolWindowId, CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            return new SummaryToolWindowControl();
        }

        [Guid("f0b9c823-7d4a-4e1b-d8a0-3c4d5e6f7a8b")]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
                BitmapImageMoniker = KnownMonikers.ToolWindow;
            }
        }
    }
}
