using System.Diagnostics;

namespace NetUsageMonitor.Ui;

/// <summary>
/// Manages "start with Windows" via a Scheduled Task with highest privileges. A scheduled task is used
/// (instead of a Run-key entry) so the elevated app can start at sign-in without a UAC prompt.
/// </summary>
public static class AutoStartManager
{
    private const string TaskName = "NetUsageMonitor Autostart";

    public static bool IsEnabled()
    {
        try
        {
            var result = RunSchTasks($"/Query /TN \"{TaskName}\"");
            return result.exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Enable(string exePath)
    {
        try
        {
            // /RL HIGHEST = run elevated; /SC ONLOGON = at sign-in; /F = replace if present.
            var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F";
            return RunSchTasks(args).exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            return RunSchTasks($"/Delete /TN \"{TaskName}\" /F").exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int exitCode, string output) RunSchTasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        return (process.ExitCode, output);
    }
}
