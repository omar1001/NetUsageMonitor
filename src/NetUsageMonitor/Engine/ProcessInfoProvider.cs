using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NetUsageMonitor.Native;

namespace NetUsageMonitor.Engine;

/// <summary>Identity of the application a network event belongs to.</summary>
/// <param name="GroupKey">Stable key used for grouping/aggregation (lower-cased exe path, or a special key).</param>
/// <param name="DisplayName">Friendly name for display.</param>
/// <param name="ExePath">Full executable path, or null when unknown.</param>
public sealed record ProcessIdentity(string GroupKey, string DisplayName, string? ExePath);

/// <summary>
/// Resolves PIDs to application identities and caches the result. Multiple PIDs that map to the same
/// executable share a group key, which is how instances of one app (e.g. all chrome.exe) are grouped.
/// </summary>
public sealed class ProcessInfoProvider
{
    /// <summary>Group key for kernel / system-attributed traffic (PID 0 and 4).</summary>
    public const string SystemKey = "::system";

    /// <summary>Group key for traffic whose owning process could not be resolved (often already exited).</summary>
    public const string UnknownKey = "::unknown";

    private readonly ConcurrentDictionary<int, ProcessIdentity> _cache = new();

    public ProcessIdentity Resolve(int pid)
        => _cache.TryGetValue(pid, out var id) ? id : _cache.GetOrAdd(pid, ResolveCore);

    /// <summary>Drop a cached PID (called when a process exits, so reused PIDs re-resolve).</summary>
    public void Invalidate(int pid) => _cache.TryRemove(pid, out _);

    private static ProcessIdentity ResolveCore(int pid)
    {
        if (pid is 0 or 4)
            return new ProcessIdentity(SystemKey, "System", null);

        var path = NativeMethods.TryGetProcessImagePath(pid);
        if (string.IsNullOrEmpty(path))
            return new ProcessIdentity(UnknownKey, "Unknown / closed app", null);

        return new ProcessIdentity(path.ToLowerInvariant(), FriendlyName(path), path);
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
                return desc.Trim();
        }
        catch
        {
            // Some files have no/locked version info; fall back to the file name.
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
