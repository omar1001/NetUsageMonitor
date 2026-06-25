using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using NetUsageMonitor.Common;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;

namespace NetUsageMonitor.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly UsageTracker _tracker;
    private readonly IconProvider _icons;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ProcessRowViewModel> _rowMap = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = new();
    public ListCollectionView RowsView { get; }

    /// <summary>Raised on the UI thread after each snapshot is applied (used to refresh the chart).</summary>
    public event Action? SnapshotApplied;

    public MainViewModel(AppSettings settings, UsageTracker tracker, IconProvider icons, Dispatcher dispatcher)
    {
        _settings = settings;
        _tracker = tracker;
        _icons = icons;
        _dispatcher = dispatcher;

        RowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);
        RowsView.IsLiveSorting = true;
        foreach (var p in new[]
                 {
                     nameof(ProcessRowViewModel.DownRate), nameof(ProcessRowViewModel.UpRate),
                     nameof(ProcessRowViewModel.DownHour), nameof(ProcessRowViewModel.UpHour),
                     nameof(ProcessRowViewModel.TotalHour), nameof(ProcessRowViewModel.DisplayName),
                     nameof(ProcessRowViewModel.StatusText)
                 })
            RowsView.LiveSortingProperties.Add(p);
        RowsView.Filter = FilterRow;
        RowsView.SortDescriptions.Add(new SortDescription(nameof(ProcessRowViewModel.DownRate), ListSortDirection.Descending));

        ToggleKeepCommand = new RelayCommand(o => ToggleKeep(AsRow(o)), o => AsRow(o) != null);
        ToggleIgnoreCommand = new RelayCommand(o => ToggleIgnore(AsRow(o)), o => AsRow(o) != null);
        OnlyTrackThisCommand = new RelayCommand(o => OnlyTrackThis(AsRow(o)), o => AsRow(o) != null);
        TrackAllCommand = new RelayCommand(_ => TrackAll());
        DeleteAppRecordsCommand = new RelayCommand(o => DeleteAppRecords(AsRow(o)), o => AsRow(o) != null);
        OpenFileLocationCommand = new RelayCommand(o => OpenFileLocation(AsRow(o)), o => AsRow(o)?.ExePath != null);
        CopyNameCommand = new RelayCommand(o => CopyName(AsRow(o)), o => AsRow(o) != null);
        DeleteAllRecordsCommand = new RelayCommand(_ => DeleteAllRecords());
        ToggleRecordingCommand = new RelayCommand(_ => ToggleRecording());
        ExportCsvCommand = new RelayCommand(_ => ExportCsv());

        UpdateStatusStrings();

        _tracker.Updated += OnTrackerUpdated;
    }

    // ---- Commands -----------------------------------------------------------

    public RelayCommand ToggleKeepCommand { get; }
    public RelayCommand ToggleIgnoreCommand { get; }
    public RelayCommand OnlyTrackThisCommand { get; }
    public RelayCommand TrackAllCommand { get; }
    public RelayCommand DeleteAppRecordsCommand { get; }
    public RelayCommand OpenFileLocationCommand { get; }
    public RelayCommand CopyNameCommand { get; }
    public RelayCommand DeleteAllRecordsCommand { get; }
    public RelayCommand ToggleRecordingCommand { get; }
    public RelayCommand ExportCsvCommand { get; }

    private ProcessRowViewModel? AsRow(object? o) => (o as ProcessRowViewModel) ?? SelectedRow;

    // ---- Bound state --------------------------------------------------------

    private ProcessRowViewModel? _selectedRow;
    public ProcessRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetProperty(ref _selectedRow, value);
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) RowsView.Refresh(); }
    }

    private string _totalDownText = "—";
    public string TotalDownText { get => _totalDownText; private set => SetProperty(ref _totalDownText, value); }

    private string _totalUpText = "—";
    public string TotalUpText { get => _totalUpText; private set => SetProperty(ref _totalUpText, value); }

    private string _dbSizeText = "";
    public string DbSizeText { get => _dbSizeText; private set => SetProperty(ref _dbSizeText, value); }

    private string _retentionText = "";
    public string RetentionText { get => _retentionText; private set => SetProperty(ref _retentionText, value); }

    private string _folderText = "";
    public string FolderText { get => _folderText; private set => SetProperty(ref _folderText, value); }

    private string _modeText = "";
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }

    private string _appCountText = "";
    public string AppCountText { get => _appCountText; private set => SetProperty(ref _appCountText, value); }

    private bool _isRecording = true;
    public bool IsRecording
    {
        get => _isRecording;
        private set { if (SetProperty(ref _isRecording, value)) OnPropertyChanged(nameof(RecordingText)); }
    }

    public string RecordingText => IsRecording ? "Recording" : "Paused";

    public AppSettings Settings => _settings;
    public UsageTracker Tracker => _tracker;

    // ---- Snapshot handling --------------------------------------------------

    private void OnTrackerUpdated(UsageSnapshot snapshot)
        => _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplySnapshot(snapshot));

    private void ApplySnapshot(UsageSnapshot snapshot)
    {
        double downRate = 0, upRate = 0;
        foreach (var g in snapshot.Groups)
        {
            if (!_rowMap.TryGetValue(g.GroupKey, out var row))
            {
                row = new ProcessRowViewModel(g.GroupKey);
                _rowMap[g.GroupKey] = row;
                Rows.Add(row);
            }
            row.Update(g, _icons);
            downRate += g.RecvRate;
            upRate += g.SentRate;
        }

        TotalDownText = ByteFormatter.Rate(downRate);
        TotalUpText = ByteFormatter.Rate(upRate);
        DbSizeText = "Records: " + ByteFormatter.Bytes(snapshot.DatabaseSizeBytes);
        AppCountText = $"{Rows.Count} apps";
        IsRecording = snapshot.IsRecording;

        SnapshotApplied?.Invoke();
    }

    private bool FilterRow(object o)
    {
        var r = (ProcessRowViewModel)o;
        if (!_settings.ShowIgnoredInList && r.IsIgnored) return false;

        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (r.ExePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ---- Command implementations -------------------------------------------

    private void ToggleKeep(ProcessRowViewModel? row)
    {
        if (row is null) return;
        bool keep = !row.IsKept;
        _settings.SetKept(row.GroupKey, keep);
        row.IsKept = keep;
        _settings.Save();
        UpdateStatusStrings();
    }

    private void ToggleIgnore(ProcessRowViewModel? row)
    {
        if (row is null) return;
        bool ignore = !row.IsIgnored;
        _settings.SetIgnored(row.GroupKey, ignore);
        row.IsIgnored = ignore;
        _settings.Save();
        RowsView.Refresh();
    }

    private void OnlyTrackThis(ProcessRowViewModel? row)
    {
        if (row is null) return;
        _settings.TrackingMode = TrackingMode.OnlyListed;
        _settings.OnlyListedKeys.Clear();
        _settings.SetOnlyListed(row.GroupKey, true);
        _settings.Save();
        UpdateStatusStrings();
        RowsView.Refresh();
    }

    private void TrackAll()
    {
        _settings.TrackingMode = TrackingMode.All;
        _settings.Save();
        UpdateStatusStrings();
        RowsView.Refresh();
    }

    private void DeleteAppRecords(ProcessRowViewModel? row)
    {
        if (row is null) return;
        var answer = MessageBox.Show(
            $"Delete all stored records for \"{row.DisplayName}\"?\nLive monitoring continues.",
            "Delete records", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer == System.Windows.MessageBoxResult.Yes)
            _tracker.Database.DeleteApp(row.GroupKey);
    }

    private static void OpenFileLocation(ProcessRowViewModel? row)
    {
        if (row?.ExePath is not { } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static void CopyName(ProcessRowViewModel? row)
    {
        if (row is null) return;
        try { Clipboard.SetText(row.DisplayName); } catch { /* ignore */ }
    }

    private void DeleteAllRecords()
    {
        var answer = MessageBox.Show(
            "Delete ALL stored records?\nThis cannot be undone. Live monitoring continues.",
            "Delete all records", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer == System.Windows.MessageBoxResult.Yes)
            _tracker.Database.DeleteAllSamples();
    }

    private void ToggleRecording()
    {
        if (_tracker.IsRecording) _tracker.Stop();
        else _tracker.Start();
        IsRecording = _tracker.IsRecording;
    }

    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export current view to CSV",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"netusage-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("App,Path,DownRateBytesPerSec,UpRateBytesPerSec,DownLastHourBytes,UpLastHourBytes,SessionDownBytes,SessionUpBytes,Status");
        foreach (ProcessRowViewModel r in RowsView)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.DisplayName), Csv(r.ExePath ?? ""),
                ((long)r.DownRate), ((long)r.UpRate),
                r.DownHour, r.UpHour, r.SessionDown, r.SessionUp, Csv(r.StatusText)));
        }
        try { File.WriteAllText(dialog.FileName, sb.ToString()); }
        catch (Exception ex) { MessageBox.Show("Could not write file: " + ex.Message); }
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- Status strings -----------------------------------------------------

    public void UpdateStatusStrings()
    {
        int kept = _settings.GetKeptKeysSnapshot().Length;
        RetentionText = $"Keeping last {_settings.RetentionMinutes} min" + (kept > 0 ? $" · {kept} kept forever" : "");
        FolderText = _settings.RecordsFolder;
        ModeText = _settings.TrackingMode == TrackingMode.All ? "Tracking: all apps" : "Tracking: only selected";
    }
}
