using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetUsageMonitor.Engine;

/// <summary>A single tick's worth of bytes for one PID.</summary>
public readonly record struct PidDelta(int Pid, long Sent, long Received);

/// <summary>An observed outbound TCP connection (raised on the ETW thread).</summary>
public readonly record struct ConnectionEvent(int Pid, string RemoteAddress, int RemotePort);

/// <summary>
/// Captures per-process network bytes using ETW's kernel TCP/UDP providers (the same data Task
/// Manager uses). Counters are incremented on the high-frequency ETW thread with interlocked adds and
/// drained periodically via <see cref="SnapshotAndReset"/> — so the hot path does almost no work.
/// Requires administrator privileges.
/// </summary>
public sealed class EtwNetworkMonitor : IDisposable
{
    private const string SessionName = "NetUsageMonitor_KernelSession";

    private sealed class Counter
    {
        public long Sent;
        public long Received;
    }

    private readonly ConcurrentDictionary<int, Counter> _counters = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _disposed;

    /// <summary>Raised (on the ETW thread) when the capture session faults — typically a privilege error.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Raised (on the ETW thread) when a process exits, so PID caches can be invalidated.</summary>
    public event Action<int>? ProcessExited;

    /// <summary>Raised (on the ETW thread) for each new outbound TCP connection.</summary>
    public event Action<ConnectionEvent>? ConnectionObserved;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;

        StopStaleSession();

        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.NetworkTCPIP |
            KernelTraceEventParser.Keywords.Process);

        var kernel = _session.Source.Kernel;

        // TCP (IPv4 + IPv6)
        kernel.TcpIpRecv += d => Add(d.ProcessID, received: d.size);
        kernel.TcpIpSend += d => Add(d.ProcessID, sent: d.size);
        kernel.TcpIpRecvIPV6 += d => Add(d.ProcessID, received: d.size);
        kernel.TcpIpSendIPV6 += d => Add(d.ProcessID, sent: d.size);

        // UDP (IPv4 + IPv6)
        kernel.UdpIpRecv += d => Add(d.ProcessID, received: d.size);
        kernel.UdpIpSend += d => Add(d.ProcessID, sent: d.size);
        kernel.UdpIpRecvIPV6 += d => Add(d.ProcessID, received: d.size);
        kernel.UdpIpSendIPV6 += d => Add(d.ProcessID, sent: d.size);

        // Outbound connections (for per-app connection/domain recording).
        kernel.TcpIpConnect += d => ConnectionObserved?.Invoke(new ConnectionEvent(d.ProcessID, d.daddr?.ToString() ?? "", d.dport));
        kernel.TcpIpConnectIPV6 += d => ConnectionObserved?.Invoke(new ConnectionEvent(d.ProcessID, d.daddr?.ToString() ?? "", d.dport));

        // Keep PID->app mapping correct when PIDs get reused.
        kernel.ProcessStop += d => ProcessExited?.Invoke(d.ProcessID);

        _thread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "ETW-NetCapture",
            Priority = ThreadPriority.AboveNormal
        };
        IsRunning = true;
        _thread.Start();
    }

    private void ProcessLoop()
    {
        try
        {
            // Blocks until the session is disposed/stopped.
            _session!.Source.Process();
        }
        catch (Exception ex) when (!_disposed)
        {
            IsRunning = false;
            Faulted?.Invoke(ex);
        }
    }

    private void Add(int pid, long sent = 0, long received = 0)
    {
        if (pid < 0) return;
        var counter = _counters.GetOrAdd(pid, static _ => new Counter());
        if (sent != 0) Interlocked.Add(ref counter.Sent, sent);
        if (received != 0) Interlocked.Add(ref counter.Received, received);
    }

    /// <summary>Atomically reads and zeroes the accumulated per-PID byte counts since the last call.</summary>
    public List<PidDelta> SnapshotAndReset()
    {
        var result = new List<PidDelta>(_counters.Count);
        foreach (var kvp in _counters)
        {
            long sent = Interlocked.Exchange(ref kvp.Value.Sent, 0);
            long received = Interlocked.Exchange(ref kvp.Value.Received, 0);
            if (sent != 0 || received != 0)
                result.Add(new PidDelta(kvp.Key, sent, received));
        }
        return result;
    }

    private static void StopStaleSession()
    {
        // A previous crash can leave the kernel session running; clear it so we can re-create it.
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
                new TraceEventSession(SessionName).Stop(noThrow: true);
        }
        catch
        {
            // Ignore — Start() will surface any real problem.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;
        try { _session?.Dispose(); } catch { /* ignore */ }
        try { _thread?.Join(2000); } catch { /* ignore */ }
        _counters.Clear();
    }
}
