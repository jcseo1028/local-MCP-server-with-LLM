using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace LocalMcpVsExtension.Commands
{
    [Command(PackageGuids.guidLocalMcpVsExtensionPackageCmdSetString,
             PackageIds.ShowSummaryWindowCommand)]
    internal sealed class ShowSummaryWindowCommand : BaseCommand<ShowSummaryWindowCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ToolWindows.SummaryToolWindow.ShowAsync();
        }
    }
}
