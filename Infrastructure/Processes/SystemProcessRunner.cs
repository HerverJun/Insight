using System.Diagnostics;
using System.Text;

namespace Insight.Infrastructure.Processes
{
    public sealed class SystemProcessRunner : IProcessRunner
    {
        public async Task<ProcessRunResult> RunAsync(
            ProcessStartSpec spec,
            Action<string>? onOutput,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(spec.FileName))
            {
                throw new ArgumentException("Process file name is required.", nameof(spec));
            }

            var output = new StringBuilder();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = spec.FileName,
                    WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                        ? Environment.CurrentDirectory
                        : spec.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = spec.StandardOutputEncoding,
                    StandardErrorEncoding = spec.StandardErrorEncoding
                },
                EnableRaisingEvents = true
            };

            if (spec.ArgumentList.Count > 0)
            {
                foreach (var argument in spec.ArgumentList)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }
            }
            else
            {
                process.StartInfo.Arguments = spec.Arguments;
            }

            foreach (var pair in spec.EnvironmentVariables)
            {
                process.StartInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            process.OutputDataReceived += (_, e) => CaptureLine(e.Data, output, onOutput);
            process.ErrorDataReceived += (_, e) => CaptureLine(e.Data, output, onOutput);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cancelRegistration = cancellationToken.Register(() => TryKillProcessTree(process));
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.Run(process.WaitForExit);

            return new ProcessRunResult(
                process.ExitCode,
                cancellationToken.IsCancellationRequested,
                output.ToString());
        }

        private static void CaptureLine(string? line, StringBuilder output, Action<string>? onOutput)
        {
            if (string.IsNullOrEmpty(line)) return;

            lock (output)
            {
                output.AppendLine(line);
            }

            onOutput?.Invoke(line);
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation must not fail command cleanup.
            }
        }
    }
}
