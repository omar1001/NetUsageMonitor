using System.Windows;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;

namespace NetUsageMonitor;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly UsageTracker _tracker;

    public SettingsWindow(AppSettings settings, UsageTracker tracker)
    {
        InitializeComponent();
        _settings = settings;
        _tracker = tracker;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        FolderBox.Text = _settings.RecordsFolder;
        RetentionBox.Text = _settings.RetentionMinutes.ToString();
        FlushBox.Text = _settings.FlushIntervalSeconds.ToString();
        ModeAll.IsChecked = _settings.TrackingMode == TrackingMode.All;
        ModeOnly.IsChecked = _settings.TrackingMode == TrackingMode.OnlyListed;
        ShowIgnoredBox.IsChecked = _settings.ShowIgnoredInList;
        BackgroundBox.IsChecked = _settings.KeepRecordingInBackground;
        StartupBox.IsChecked = _settings.StartWithWindows;
        StartMinBox.IsChecked = _settings.StartMinimized;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to keep records in",
            InitialDirectory = System.IO.Directory.Exists(FolderBox.Text) ? FolderBox.Text : ""
        };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private int ParseRetention()
        => int.TryParse(RetentionBox.Text, out int m) && m >= 1 ? m : _settings.RetentionMinutes;

    private void OnDeleteOld(object sender, RoutedEventArgs e)
    {
        if (Confirm("Delete records older than the retention window?\n\"Kept\" apps are preserved."))
        {
            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ParseRetention() * 60L;
            _tracker.Database.PruneExceptKept(cutoff, _settings.GetKeptKeysSnapshot());
        }
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        if (Confirm("Delete ALL stored records? This cannot be undone."))
            _tracker.Database.DeleteAllSamples();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.RecordsFolder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? AppPaths.DefaultRecordsFolder : FolderBox.Text;
        _settings.RetentionMinutes = ParseRetention();
        if (int.TryParse(FlushBox.Text, out int flush) && flush >= 1)
            _settings.FlushIntervalSeconds = flush;
        _settings.TrackingMode = ModeOnly.IsChecked == true ? TrackingMode.OnlyListed : TrackingMode.All;
        _settings.ShowIgnoredInList = ShowIgnoredBox.IsChecked == true;
        _settings.KeepRecordingInBackground = BackgroundBox.IsChecked == true;
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.StartMinimized = StartMinBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool Confirm(string message)
        => MessageBox.Show(message, "NetUsage Monitor", MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;
}
