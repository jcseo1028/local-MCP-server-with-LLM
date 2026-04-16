using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace LocalMcpVsExtension.Services
{
    /// <summary>
    /// 오프라인 빌드 및 테스트를 실행한다.
    /// contracts.md §11, rules.md 오프라인 규칙 준수.
    /// --no-restore 옵션으로 네트워크 접근 방지.
    /// </summary>
    internal sealed class BuildTestRunner
    {
        /// <summary>
        /// dotnet build --no-restore 실행.
        /// </summary>
        public async Task<BuildRunResult> BuildAsync(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath))
                return new BuildRunResult { Attempted = false, Summary = "솔루션 경로 없음" };

            var args = $"build \"{solutionPath}\" --no-restore --verbosity quiet";
            return await RunDotnetAsync(args, "빌드");
        }

        /// <summary>
        /// dotnet test --no-restore --filter 실행.
        /// 네트워크 의존 테스트(Integration, E2E)를 제외한다.
        /// </summary>
        public async Task<BuildRunResult> TestAsync(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath))
                return new BuildRunResult { Attempted = false, Summary = "솔루션 경로 없음" };

            var filter = "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E&FullyQualifiedName!~Network";
            var args = $"test \"{solutionPath}\" --no-restore --no-build --filter \"{filter}\" --verbosity quiet";
            return await RunDotnetAsync(args, "테스트");
        }

        private static async Task<BuildRunResult> RunDotnetAsync(string arguments, string label)
        {
            var result = new BuildRunResult { Attempted = true };

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                using (var process = new Process { StartInfo = psi })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    var exited = await Task.Run(() => process.WaitForExit(120_000));
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        result.Succeeded = false;
                        result.Summary = $"{label} 타임아웃 (120초)";
                        return result;
                    }

                    result.Succeeded = process.ExitCode == 0;

                    var output = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
                    result.Summary = output.Length > 500 ? output.Substring(0, 500) + "..." : output;
                }
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.Summary = $"{label} 실행 오류: {ex.Message}";
            }

            return result;
        }
    }

    internal sealed class BuildRunResult
    {
        public bool Attempted { get; set; }
        public bool? Succeeded { get; set; }
        public string Summary { get; set; } = "";
    }
}
