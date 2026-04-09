using System;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace LocalMcpVsExtension
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Local MCP 코드 요약",
        "오프라인 환경에서 로컬 MCP 서버의 코드 요약 도구를 호출합니다.", "1.0.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.guidLocalMcpVsExtensionPackageString)]
    [ProvideToolWindow(typeof(ToolWindows.SummaryToolWindow.Pane))]
    public sealed class LocalMcpVsExtensionPackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}
