using System.Diagnostics;
using System.Net.Sockets;

namespace LanInspector.Core.Diagnostics;

/// <summary>
/// Outcome of running an external tool.
/// </summary>
/// <param name="Started">
/// False when the executable could not be launched at all — it is missing from PATH, or the
/// platform refused. Callers must distinguish this from a tool that ran and reported an error:
/// "not installed" and "installed but unhappy" need different advice.
/// </param>
public sealed record ProcessResult(
    bool Started,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    string? StartError = null)
{
    /// <summary>Standard output, falling back to standard error when stdout is empty.</summary>
    public string Text => string.IsNullOrWhiteSpace(StandardOutput) ? StandardError : StandardOutput;

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

public static class ProcessHelper
{
    /// <summary>
    /// Runs a command and returns its output, or an empty string if it could not be run.
    /// Prefer <see cref="TryRunAsync"/> when the caller needs to tell those two cases apart.
    /// </summary>
    public static async Task<string> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await TryRunAsync(fileName, arguments, timeout, cancellationToken);
        return result.Started ? result.Text : string.Empty;
    }

    /// <param name="standardInput">Text written to the tool's standard input, which is then closed;
    /// null leaves standard input alone.</param>
    public static async Task<ProcessResult> TryRunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = standardInput is not null,
                // No byte-order mark: a shell reading the script would take it as part of the first command.
                StandardInputEncoding = standardInput is null ? null : new System.Text.UTF8Encoding(false),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                return new ProcessResult(false, null, string.Empty, string.Empty, false, ex.Message);
            }

            if (process is null)
            {
                return new ProcessResult(false, null, string.Empty, string.Empty, false, $"Could not start '{fileName}'.");
            }

            try
            {
                // Both streams are read concurrently with the wait: a tool that fills one pipe
                // buffer while nobody drains it never exits.
                var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

                if (standardInput is not null)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutCts.Token);
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // The tool exited without reading its input, as ssh does when it cannot
                        // connect; its output says why.
                    }
                }

                await process.WaitForExitAsync(timeoutCts.Token);

                return new ProcessResult(true, process.ExitCode, await outputTask, await errorTask, false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                return new ProcessResult(true, null, string.Empty, string.Empty, true);
            }
            catch (InvalidOperationException ex)
            {
                return new ProcessResult(true, null, string.Empty, string.Empty, false, ex.Message);
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return new ProcessResult(true, null, string.Empty, string.Empty, true);
        }
        catch (SocketException ex)
        {
            return new ProcessResult(false, null, string.Empty, string.Empty, false, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// Reports whether an executable can be launched. The streams are drained before waiting:
    /// a tool that writes more than the pipe buffer to stdout/stderr blocks forever on exit if
    /// nobody reads them, which would hang this check instead of answering it.
    /// </summary>
    public static bool IsAvailable(string fileName)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(2000))
            {
                TryKillProcess(process);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKillProcess(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
