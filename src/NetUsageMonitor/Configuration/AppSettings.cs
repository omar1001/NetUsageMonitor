using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetUsageMonitor.Configuration;

/// <summary>How the engine decides which apps to record.</summary>
public enum TrackingMode
{
    /// <summary>Record every app except those explicitly ignored (default).</summary>
    All = 0,

    /// <summary>Record only the apps in <see cref="AppSettings.OnlyListedKeys"/>.</summary>
    OnlyListed = 1
}

/// <summary>The window over which a data cap is measured.</summary>
public enum CapPeriod
{
    /// <summary>Cumulative usage since the cap was set / last reset.</summary>
    Total = 0,
    /// <summary>Resets at local midnight.</summary>
    Daily = 1,
    /// <summary>Resets on the 1st of the month.</summary>
    Monthly = 2
}

/// <summary>A per-app data limit. When usage over the period reaches the limit, the app is blocked.</summary>
public sealed class AppCap
{
    public long LimitBytes { get; set; }
    public CapPeriod Period { get; set; } = CapPeriod.Total;
    /// <summary>Unix seconds baseline used for the "Total" period (set when created / reset).</summary>
    public long ResetUnix { get; set; }
    /// <summary>True once the cap has tripped and the app was auto-blocked.</summary>
    public bool Tripped { get; set; }
}

/// <summary>Well-known on-disk locations used by the app.</summary>
public static class AppPaths
{
    public const string AppFolderName = "NetUsageMonitor";

    /// <summary>%APPDATA%\NetUsageMonitor (roaming) — holds settings.json.</summary>
    public static string ConfigFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string SettingsFile { get; } = Path.Combine(ConfigFolder, "settings.json");

    /// <summary>%LOCALAPPDATA%\NetUsageMonitor — default records location.</summary>
    public static string DefaultRecordsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public const string DatabaseFileName = "netusage.db";
}

/// <summary>
/// User-configurable settings, persisted as JSON. The membership sets are accessed from both the
/// UI thread (mutation) and the engine thread (reads), so all access goes through the locked helpers.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Folder where the records database is stored.</summary>
    public string RecordsFolder { get; set; } = AppPaths.DefaultRecordsFolder;

    /// <summary>Default rolling retention window, in minutes, for apps that are NOT kept indefinitely.</summary>
    public int RetentionMinutes { get; set; } = 60;

    /// <summary>How often aggregated samples are flushed to the database, in seconds.</summary>
    public int FlushIntervalSeconds { get; set; } = 5;

    /// <summary>When the window is closed, keep the engine running (minimize to tray) instead of stopping.</summary>
    public bool KeepRecordingInBackground { get; set; } = true;

    /// <summary>Launch automatically at Windows sign-in.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>Start hidden in the tray (used together with StartWithWindows).</summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>Whether ignored apps remain visible (greyed) in the list so they can be re-enabled.</summary>
    public bool ShowIgnoredInList { get; set; } = true;

    public TrackingMode TrackingMode { get; set; } = TrackingMode.All;

    /// <summary>Group keys (lower-cased exe paths) that are never recorded.</summary>
    public HashSet<string> IgnoredKeys { get; set; } = new();

    /// <summary>Group keys recorded indefinitely (exempt from retention pruning).</summary>
    public HashSet<string> KeptKeys { get; set; } = new();

    /// <summary>Group keys recorded when <see cref="TrackingMode"/> is <see cref="TrackingMode.OnlyListed"/>.</summary>
    public HashSet<string> OnlyListedKeys { get; set; } = new();

    /// <summary>Group keys whose internet is blocked via Windows Firewall.</summary>
    public HashSet<string> BlockedKeys { get; set; } = new();

    /// <summary>Group keys for which connection/domain recording is enabled.</summary>
    public HashSet<string> RecordConnectionsKeys { get; set; } = new();

    /// <summary>Per-app data caps, keyed by group key.</summary>
    public Dictionary<string, AppCap> Caps { get; set; } = new();

    /// <summary>How long connection records are kept, in days.</summary>
    public int ConnectionRetentionDays { get; set; } = 7;

    /// <summary>Re-sort the list automatically as live values change (off = rows stay put, easier to click).</summary>
    public bool AutoSort { get; set; } = false;

    /// <summary>Show a notification when a data cap trips.</summary>
    public bool NotifyOnCap { get; set; } = true;

    [JsonIgnore]
    private readonly object _gate = new();

    [JsonIgnore]
    public string DatabasePath => Path.Combine(RecordsFolder, AppPaths.DatabaseFileName);

    // ---- Thread-safe membership helpers ---------------------------------------------------------

    public bool IsIgnored(string key)
    {
        lock (_gate) return IgnoredKeys.Contains(key);
    }

    public bool IsKept(string key)
    {
        lock (_gate) return KeptKeys.Contains(key);
    }

    /// <summary>Returns true if the engine should record this group given the current mode/lists.</summary>
    public bool ShouldRecord(string key)
    {
        lock (_gate)
        {
            if (IgnoredKeys.Contains(key)) return false;
            if (TrackingMode == TrackingMode.OnlyListed) return OnlyListedKeys.Contains(key);
            return true;
        }
    }

    /// <summary>Snapshot of the kept keys, for the retention query.</summary>
    public string[] GetKeptKeysSnapshot()
    {
        lock (_gate) return KeptKeys.ToArray();
    }

    public void SetIgnored(string key, bool ignored)
    {
        lock (_gate)
        {
            if (ignored) IgnoredKeys.Add(key); else IgnoredKeys.Remove(key);
        }
    }

    public void SetKept(string key, bool kept)
    {
        lock (_gate)
        {
            if (kept) KeptKeys.Add(key); else KeptKeys.Remove(key);
        }
    }

    public void SetOnlyListed(string key, bool listed)
    {
        lock (_gate)
        {
            if (listed) OnlyListedKeys.Add(key); else OnlyListedKeys.Remove(key);
        }
    }

    public bool IsBlocked(string key)
    {
        lock (_gate) return BlockedKeys.Contains(key);
    }

    public void SetBlocked(string key, bool blocked)
    {
        lock (_gate)
        {
            if (blocked) BlockedKeys.Add(key); else BlockedKeys.Remove(key);
        }
    }

    public bool IsRecordingConnections(string key)
    {
        lock (_gate) return RecordConnectionsKeys.Contains(key);
    }

    public void SetRecordingConnections(string key, bool on)
    {
        lock (_gate)
        {
            if (on) RecordConnectionsKeys.Add(key); else RecordConnectionsKeys.Remove(key);
        }
    }

    /// <summary>True if any app currently has connection recording enabled.</summary>
    public bool AnyConnectionRecording()
    {
        lock (_gate) return RecordConnectionsKeys.Count > 0;
    }

    public AppCap? GetCap(string key)
    {
        lock (_gate) return Caps.TryGetValue(key, out var c) ? c : null;
    }

    public void SetCap(string key, AppCap? cap)
    {
        lock (_gate)
        {
            if (cap is null) Caps.Remove(key); else Caps[key] = cap;
        }
    }

    /// <summary>Snapshot of (key, cap) pairs for the engine to evaluate each tick.</summary>
    public List<KeyValuePair<string, AppCap>> GetCapsSnapshot()
    {
        lock (_gate) return Caps.ToList();
    }

    // ---- Persistence ----------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigFolder);
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(this, JsonOptions);
            }
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch
        {
            // Best-effort persistence; never crash the app over a settings write.
        }
    }

    private void Normalize()
    {
        if (string.IsNullOrWhiteSpace(RecordsFolder))
            RecordsFolder = AppPaths.DefaultRecordsFolder;
        if (RetentionMinutes < 1) RetentionMinutes = 60;
        if (FlushIntervalSeconds < 1) FlushIntervalSeconds = 5;

        if (ConnectionRetentionDays < 1) ConnectionRetentionDays = 7;

        // Rebuild sets with a case-insensitive comparer so membership matches the lower-cased keys.
        IgnoredKeys = new HashSet<string>(IgnoredKeys ?? new(), StringComparer.OrdinalIgnoreCase);
        KeptKeys = new HashSet<string>(KeptKeys ?? new(), StringComparer.OrdinalIgnoreCase);
        OnlyListedKeys = new HashSet<string>(OnlyListedKeys ?? new(), StringComparer.OrdinalIgnoreCase);
        BlockedKeys = new HashSet<string>(BlockedKeys ?? new(), StringComparer.OrdinalIgnoreCase);
        RecordConnectionsKeys = new HashSet<string>(RecordConnectionsKeys ?? new(), StringComparer.OrdinalIgnoreCase);
        Caps = new Dictionary<string, AppCap>(Caps ?? new(), StringComparer.OrdinalIgnoreCase);
    }
}
