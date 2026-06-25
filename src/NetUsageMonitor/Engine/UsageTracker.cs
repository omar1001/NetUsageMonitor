using NetUsageMonitor.Configuration;
using NetUsageMonitor.Storage;
using Timer = System.Threading.Timer;

namespace NetUsageMonitor.Engine;

/// <summary>Per-app usage as of one tick — what the UI binds to.</summary>
public sealed class GroupUsage
{
    public required string GroupKey { get; init; }
    public required string DisplayName { get; init; }
    public string? ExePath { get; init; }

    /// <summary>Bytes per second during the last tick.</summary>
    public double SentRate { get; init; }
    public double RecvRate { get; init; }

    /// <summary>Totals since the engine started this run (in-memory).</summary>
    public long SessionSent { get; init; }
    public long SessionReceived { get; init; }

    /// <summary>Totals over the retention window from the database.</summary>
    public long HourSent { get; init; }
    public long HourReceived { get; init; }

    public bool IsRecorded { get; init; }
    public bool IsIgnored { get; init; }
    public bool IsKept { get; init; }
}

/// <summary>A point-in-time view of all tracked apps.</summary>
public sealed class UsageSnapshot
{
    public required IReadOnlyList<GroupUsage> Groups { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public int RetentionMinutes { get; init; }
    public bool IsRecording { get; init; }
}

/// <summary>
/// Coordinates the ETW capture, process identity, in-memory live model, and database persistence on a
/// one-second loop. Runs independently of the UI; emits <see cref="Updated"/> snapshots each tick.
/// </summary>
public sealed class UsageTracker : IDisposable
{
    private sealed class Live
    {
        public string DisplayName = "";
        public string? ExePath;
        public long RateSent, RateRecv;     // bytes during the last tick
        public long SessionSent, SessionRecv;
        public long PendingSent, PendingRecv; // accumulated since the last DB flush
    }

    private readonly AppSettings _settings;
    private readonly ProcessInfoProvider _processInfo = new();
    private readonly UsageDatabase _database = new();
    private readonly Dictionary<string, Live> _live = new(StringComparer.OrdinalIgnoreCase);

    private EtwNetworkMonitor? _monitor;
    private Timer? _timer;
    private int _ticking; // reentrancy guard for the timer callback

    private int _flushCountdown;
    private int _pruneCountdown;
    private long _dbSize;
    private Dictionary<string, UsageTotal> _hourTotals = new(StringComparer.OrdinalIgnoreCase);

    public UsageTracker(AppSettings settings)
    {
        _settings = settings;
        _database.Open(_settings.DatabasePath);
    }

    public bool IsRecording { get; private set; }

    /// <summary>Raised once per tick (on a background thread) with the latest usage snapshot.</summary>
    public event Action<UsageSnapshot>? Updated;

    /// <summary>Raised when the capture session faults (typically: not running as administrator).</summary>
    public event Action<Exception>? Faulted;

    public ProcessInfoProvider ProcessInfo => _processInfo;
    public UsageDatabase Database => _database;

    public void Start()
    {
        if (IsRecording) return;

        _monitor = new EtwNetworkMonitor();
        _monitor.ProcessExited += _processInfo.Invalidate;
        _monitor.Faulted += ex => Faulted?.Invoke(ex);
        _monitor.Start();

        _flushCountdown = _settings.FlushIntervalSeconds;
        _pruneCountdown = 60;
        IsRecording = true;

        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;

        _timer?.Dispose();
        _timer = null;

        // Persist anything buffered so the last few seconds aren't lost.
        FlushPending(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _monitor?.Dispose();
        _monitor = null;

        // Clear live rates but keep session totals visible until restart.
        foreach (var live in _live.Values)
        {
            live.RateSent = 0;
            live.RateRecv = 0;
        }
        EmitSnapshot();
    }

    private void Tick(object? state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var monitor = _monitor;
            if (monitor is null) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var deltas = monitor.SnapshotAndReset();

            // Aggregate this tick's PID deltas into app groups.
            var tickByGroup = new Dictionary<string, (long sent, long recv, ProcessIdentity id)>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in deltas)
            {
                var id = _processInfo.Resolve(d.Pid);
                if (tickByGroup.TryGetValue(id.GroupKey, out var acc))
                    tickByGroup[id.GroupKey] = (acc.sent + d.Sent, acc.recv + d.Received, acc.id);
                else
                    tickByGroup[id.GroupKey] = (d.Sent, d.Received, id);
            }

            // Zero last-tick rates for everyone; refilled below for groups active this tick.
            foreach (var live in _live.Values)
            {
                live.RateSent = 0;
                live.RateRecv = 0;
            }

            foreach (var (key, value) in tickByGroup)
            {
                if (!_live.TryGetValue(key, out var live))
                {
                    live = new Live();
                    _live[key] = live;
                }
                live.DisplayName = value.id.DisplayName;
                live.ExePath = value.id.ExePath;
                live.RateSent = value.sent;     // bytes during this 1-second tick
                live.RateRecv = value.recv;
                live.SessionSent += value.sent;
                live.SessionRecv += value.recv;

                if (_settings.ShouldRecord(key))
                {
                    live.PendingSent += value.sent;
                    live.PendingRecv += value.recv;
                }
            }

            if (--_flushCountdown <= 0)
            {
                _flushCountdown = Math.Max(1, _settings.FlushIntervalSeconds);
                FlushPending(now);
                _hourTotals = _database.GetTotalsSince(now - _settings.RetentionMinutes * 60L);
                _dbSize = _database.GetDatabaseSizeBytes();
            }

            if (--_pruneCountdown <= 0)
            {
                _pruneCountdown = 60;
                _database.PruneExceptKept(now - _settings.RetentionMinutes * 60L, _settings.GetKeptKeysSnapshot());
            }

            EmitSnapshot();
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void FlushPending(long now)
    {
        var rows = new List<SampleRow>();
        foreach (var (key, live) in _live)
        {
            if (live.PendingSent == 0 && live.PendingRecv == 0) continue;
            rows.Add(new SampleRow(key, live.DisplayName, live.ExePath, live.PendingSent, live.PendingRecv));
            live.PendingSent = 0;
            live.PendingRecv = 0;
        }
        if (rows.Count > 0)
            _database.WriteSamples(now, rows);
    }

    private void EmitSnapshot()
    {
        var groups = new List<GroupUsage>(_live.Count);
        foreach (var (key, live) in _live)
        {
            _hourTotals.TryGetValue(key, out var hour);
            bool ignored = _settings.IsIgnored(key);
            bool kept = _settings.IsKept(key);
            groups.Add(new GroupUsage
            {
                GroupKey = key,
                DisplayName = live.DisplayName.Length == 0 ? key : live.DisplayName,
                ExePath = live.ExePath,
                SentRate = live.RateSent,
                RecvRate = live.RateRecv,
                SessionSent = live.SessionSent,
                SessionReceived = live.SessionRecv,
                // Include not-yet-flushed bytes so the hour totals track live.
                HourSent = hour.Sent + live.PendingSent,
                HourReceived = hour.Received + live.PendingRecv,
                IsRecorded = _settings.ShouldRecord(key),
                IsIgnored = ignored,
                IsKept = kept
            });
        }

        Updated?.Invoke(new UsageSnapshot
        {
            Groups = groups,
            DatabaseSizeBytes = _dbSize,
            RetentionMinutes = _settings.RetentionMinutes,
            IsRecording = IsRecording
        });
    }

    /// <summary>Switches the records database to a new folder, moving existing data if possible.</summary>
    public void RebindDatabase(string newDatabasePath)
    {
        _database.Open(newDatabasePath);
        _hourTotals = new(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Stop();
        _database.Dispose();
    }
}
