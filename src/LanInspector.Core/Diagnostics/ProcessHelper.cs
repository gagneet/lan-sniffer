using System.Diagnostics;
using System.Net.Sockets;

namespace LanInspector.Core.Diagnostics;

public static class ProcessHelper
{
    public static async Task<string> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                var output = await outputTask;
                var error = await errorTask;
                return string.IsNullOrWhiteSpace(output) ? error : output;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            return string.Empty;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public static bool IsAvailable(string fileName)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(2000);
            return true;
        }
        catch
        {
            return false;
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
