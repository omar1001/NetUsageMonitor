using System.Windows.Media;
using NetUsageMonitor.Common;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;

namespace NetUsageMonitor.ViewModels;

/// <summary>One row in the apps grid; updated in place each tick so sorting/selection stay stable.</summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    public string GroupKey { get; }

    public ProcessRowViewModel(string groupKey)
    {
        GroupKey = groupKey;
    }

    private string _displayName = "";
    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }

    private string? _exePath;
    public string? ExePath { get => _exePath; private set => SetProperty(ref _exePath, value); }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; private set => SetProperty(ref _icon, value); }

    private double _downRate;
    public double DownRate { get => _downRate; private set => SetProperty(ref _downRate, value); }

    private double _upRate;
    public double UpRate { get => _upRate; private set => SetProperty(ref _upRate, value); }

    private long _downHour;
    public long DownHour { get => _downHour; private set => SetProperty(ref _downHour, value); }

    private long _upHour;
    public long UpHour { get => _upHour; private set => SetProperty(ref _upHour, value); }

    private long _totalHour;
    public long TotalHour { get => _totalHour; private set => SetProperty(ref _totalHour, value); }

    private long _sessionDown;
    public long SessionDown { get => _sessionDown; private set => SetProperty(ref _sessionDown, value); }

    private long _sessionUp;
    public long SessionUp { get => _sessionUp; private set => SetProperty(ref _sessionUp, value); }

    private bool _isIgnored;
    public bool IsIgnored { get => _isIgnored; set => SetProperty(ref _isIgnored, value); }

    private bool _isKept;
    public bool IsKept { get => _isKept; set => SetProperty(ref _isKept, value); }

    private bool _isRecorded;
    public bool IsRecorded { get => _isRecorded; private set => SetProperty(ref _isRecorded, value); }

    private bool _isBlocked;
    public bool IsBlocked { get => _isBlocked; set => SetProperty(ref _isBlocked, value); }

    private bool _isRecordingConnections;
    public bool IsRecordingConnections { get => _isRecordingConnections; set => SetProperty(ref _isRecordingConnections, value); }

    private bool _hasCap;
    public bool HasCap { get => _hasCap; set => SetProperty(ref _hasCap, value); }

    private long _capLimit;
    public long CapLimit { get => _capLimit; set => SetProperty(ref _capLimit, value); }

    private long _capUsed;
    public long CapUsed { get => _capUsed; set => SetProperty(ref _capUsed, value); }

    private bool _capTripped;
    public bool CapTripped { get => _capTripped; set => SetProperty(ref _capTripped, value); }

    private string _statusText = "Idle";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    // ---- Derived (for the detail panel) ----
    public double CapPercent => HasCap && CapLimit > 0 ? Math.Min(100.0, CapUsed * 100.0 / CapLimit) : 0;
    public string CapText => HasCap ? $"{ByteFormatter.Bytes(CapUsed)} / {ByteFormatter.Bytes(CapLimit)}" : "No limit set";
    public string AvgPerMinDownText => ByteFormatter.Bytes((long)(DownHour / 60.0)) + "/min";
    public string AvgPerMinUpText => ByteFormatter.Bytes((long)(UpHour / 60.0)) + "/min";
    public string BlockText => IsBlocked ? "Unblock internet" : "Block internet";
    public string RecordConnText => IsRecordingConnections ? "Stop recording connections" : "Record connections";

    public void Update(GroupUsage g, IconProvider icons)
    {
        DisplayName = g.DisplayName;
        if (ExePath != g.ExePath)
        {
            ExePath = g.ExePath;
            Icon = icons.GetIcon(g.ExePath);
        }
        DownRate = g.RecvRate;
        UpRate = g.SentRate;
        DownHour = g.HourReceived;
        UpHour = g.HourSent;
        TotalHour = g.HourReceived + g.HourSent;
        SessionDown = g.SessionReceived;
        SessionUp = g.SessionSent;
        IsIgnored = g.IsIgnored;
        IsKept = g.IsKept;
        IsRecorded = g.IsRecorded;
        IsBlocked = g.IsBlocked;
        IsRecordingConnections = g.IsRecordingConnections;
        HasCap = g.HasCap;
        CapLimit = g.CapLimitBytes;
        CapUsed = g.CapUsedBytes;
        CapTripped = g.CapTripped;
        StatusText = ComputeStatus(g);

        // Refresh derived values shown in the detail panel.
        OnPropertyChanged(nameof(CapPercent));
        OnPropertyChanged(nameof(CapText));
        OnPropertyChanged(nameof(AvgPerMinDownText));
        OnPropertyChanged(nameof(AvgPerMinUpText));
        OnPropertyChanged(nameof(BlockText));
        OnPropertyChanged(nameof(RecordConnText));
    }

    private static string ComputeStatus(GroupUsage g)
    {
        if (g.IsBlocked) return "Blocked";
        if (g.IsIgnored) return "Ignored";
        if (!g.IsRecorded) return "Not tracked";
        bool active = g.RecvRate >= 1 || g.SentRate >= 1;
        if (g.HasCap && g.CapTripped) return "Capped";
        if (g.IsKept) return active ? "Kept · Active" : "Kept";
        return active ? "Active" : "Idle";
    }
}
