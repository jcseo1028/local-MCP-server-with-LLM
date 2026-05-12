using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

            // VS 확장 환경에서 다루는 레거시 솔루션은 MSBuild(.NET Framework) 호환성이 더 높다.
            // 가능하면 VS MSBuild를 우선 사용하고, 없을 때만 dotnet으로 폴백한다.
            if (TryGetVisualStudioMsBuildPath(out var msbuildPath))
            {
                var msbuildArgs = $"\"{solutionPath}\" /t:Build /p:RestorePackages=false /p:Configuration=Debug /v:q /nologo";
                var msbuildResult = await RunProcessAsync(msbuildPath, msbuildArgs, "빌드(MSBuild)");
                if (msbuildResult.Succeeded == true)
                    return msbuildResult;

                // MSBuild가 실패하면 dotnet으로 1회 폴백해 진단 정보를 추가 확보한다.
                var dotnetArgs = $"build \"{solutionPath}\" --no-restore --verbosity quiet";
                var dotnetResult = await RunDotnetAsync(dotnetArgs, "빌드(dotnet 폴백)");
                dotnetResult.Summary =
                    $"MSBuild 실패 후 dotnet 폴백 결과\n- msbuild: {msbuildResult.Summary}\n- dotnet: {dotnetResult.Summary}";
                return dotnetResult;
            }

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
            => await RunProcessAsync("dotnet", arguments, label);

        private static async Task<BuildRunResult> RunProcessAsync(string fileName, string arguments, string label)
        {
            var result = new BuildRunResult { Attempted = true };

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
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
                    var outText = stdout.ToString().Trim();
                    var errText = stderr.ToString().Trim();
                    var merged = new StringBuilder();
                    if (!string.IsNullOrEmpty(errText))
                        merged.AppendLine(errText);
                    if (!string.IsNullOrEmpty(outText))
                        merged.AppendLine(outText);

                    var output = merged.ToString().Trim();
                    if (string.IsNullOrEmpty(output))
                        output = $"{label} 종료 (exitCode={process.ExitCode})";

                    result.Summary = BuildSummary(label, output, process.ExitCode, result.Succeeded == true);
                }
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.Summary = $"{label} 실행 오류: {ex.Message}";
            }

            return result;
        }

        private static string BuildSummary(string label, string output, int exitCode, bool succeeded)
        {
            var normalized = output.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');

            var errorLines = lines
                .Where(l => l.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(8)
                .ToArray();
            if (errorLines.Length > 0)
            {
                var joined = string.Join("\n", errorLines);
                return $"{label} 실패 (exitCode={exitCode})\n{joined}";
            }

            var warnLines = lines
                .Where(l => l.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(8)
                .ToArray();
            if (!succeeded && warnLines.Length > 0)
            {
                var joined = string.Join("\n", warnLines);
                return $"{label} 실패 (exitCode={exitCode}, 오류 라인 미검출, 경고 일부 표시)\n{joined}";
            }

            if (normalized.Length > 1000)
                normalized = normalized.Substring(normalized.Length - 1000);

            return $"{label} {(succeeded ? "성공" : "실패")} (exitCode={exitCode})\n{normalized}";
        }

        private static bool TryGetVisualStudioMsBuildPath(out string path)
        {
            path = string.Empty;

            var candidates = new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class BuildRunResult
    {
        public bool Attempted { get; set; }
        public bool? Succeeded { get; set; }
        public string Summary { get; set; } = "";
    }
}
