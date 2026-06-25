using System.Collections.Concurrent;
using System.Net;

namespace NetUsageMonitor.Network;

/// <summary>
/// Best-effort reverse-DNS (PTR) lookups for remote IPs, cached, resolved asynchronously off the hot
/// path. Hostnames from PTR records are approximate (CDNs/ISPs often differ from the site domain), but
/// they require no driver and work for HTTPS. Calls back when a name is found.
/// </summary>
public sealed class ReverseDnsResolver
{
    // null value = resolved-but-no-name (or pending). Presence of key = don't queue again.
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private readonly Action<string, string> _onResolved;

    public ReverseDnsResolver(Action<string, string> onResolved) => _onResolved = onResolved;

    public string? TryGet(string ip)
    {
        _cache.TryGetValue(ip, out var host);
        return host;
    }

    public void Resolve(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return;
        if (!_cache.TryAdd(ip, null)) return; // already known or in flight

        _ = Task.Run(async () =>
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(3));
                if (!string.IsNullOrWhiteSpace(entry.HostName) &&
                    !string.Equals(entry.HostName, ip, StringComparison.OrdinalIgnoreCase))
                {
                    _cache[ip] = entry.HostName;
                    _onResolved(ip, entry.HostName);
                }
            }
            catch
            {
                // No PTR record / lookup failed — leave as null so we don't retry.
            }
        });
    }
}
