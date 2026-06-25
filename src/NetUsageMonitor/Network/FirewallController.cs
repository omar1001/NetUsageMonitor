using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NetUsageMonitor.Network;

/// <summary>
/// Blocks/unblocks an application's internet access using Windows Firewall rules (outbound + inbound)
/// via <c>netsh advfirewall</c>. Rules are named deterministically per executable so they can be
/// queried and removed. Requires administrator (the app already runs elevated).
/// </summary>
public static class FirewallController
{
    private const string Prefix = "NetUsageMonitor Block";

    private static string RuleName(string exePath) => $"{Prefix} {ShortHash(exePath)}";

    private static string ShortHash(string s)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 6);
    }

    public static bool Block(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        string name = RuleName(exePath);
        // Clear any stale rule first so we don't stack duplicates.
        RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
        bool outOk = RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir=out action=block program=\"{exePath}\" enable=yes").ok;
        bool inOk = RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir=in action=block program=\"{exePath}\" enable=yes").ok;
        return outOk && inOk;
    }

    public static bool Unblock(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        return RunNetsh($"advfirewall firewall delete rule name=\"{RuleName(exePath)}\"").ok;
    }

    /// <summary>Returns true if a block rule currently exists for this executable.</summary>
    public static bool IsBlocked(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        // `show rule` exits 0 when a matching rule exists, 1 otherwise.
        return RunNetsh($"advfirewall firewall show rule name=\"{RuleName(exePath)}\"").ok;
    }

    private static (bool ok, string output) RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return (p.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
