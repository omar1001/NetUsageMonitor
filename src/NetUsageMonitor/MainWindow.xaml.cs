using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using NetUsageMonitor.Common;
using NetUsageMonitor.Ui;
using NetUsageMonitor.ViewModels;

namespace NetUsageMonitor;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private int _chartTick;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.SnapshotApplied += OnSnapshotApplied;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedRow))
            RefreshDetail(refreshAllTime: true);
    }

    private void OnSnapshotApplied()
    {
        // Refresh the chart periodically (~every 5s) while an app is selected.
        if (_vm.SelectedRow is null) return;
        if (++_chartTick % 5 == 0)
            RefreshDetail(refreshAllTime: false);
    }

    private void RefreshDetail(bool refreshAllTime)
    {
        var row = _vm.SelectedRow;
        if (row is null)
        {
            Chart.Clear();
            AllTimeText.Text = "—";
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int minutes = _vm.Settings.RetentionMinutes;
        var history = _vm.Tracker.Database.GetHistory(row.GroupKey, now - minutes * 60L);
        Chart.SetData(history, minutes, now);

        if (refreshAllTime)
        {
            var totals = _vm.Tracker.Database.GetTotalsSince(0);
            AllTimeText.Text = totals.TryGetValue(row.GroupKey, out var t)
                ? ByteFormatter.Bytes(t.Sent + t.Received)
                : "0 B";
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        string oldFolder = _vm.Settings.RecordsFolder;

        var window = new SettingsWindow(_vm.Settings, _vm.Tracker) { Owner = this };
        window.ShowDialog();

        _vm.Settings.Save();

        if (!string.Equals(oldFolder, _vm.Settings.RecordsFolder, StringComparison.OrdinalIgnoreCase))
        {
            try { _vm.Tracker.RebindDatabase(_vm.Settings.DatabasePath); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not switch records folder: " + ex.Message, "NetUsage Monitor",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        SyncAutoStart();
        _vm.UpdateStatusStrings();
        _vm.RowsView.Refresh();
    }

    private void SyncAutoStart()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
            bool enabled = AutoStartManager.IsEnabled();
            if (_vm.Settings.StartWithWindows && !enabled) AutoStartManager.Enable(exe);
            else if (!_vm.Settings.StartWithWindows && enabled) AutoStartManager.Disable();
        }
        catch { /* best effort */ }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.IsExiting)
        {
            base.OnClosing(e);
            return;
        }

        if (_vm.Settings.KeepRecordingInBackground)
        {
            // Keep the engine running; just hide to the tray.
            e.Cancel = true;
            app.HideToTray();
        }
        else
        {
            base.OnClosing(e);
            app.ExitApp();
        }
    }
}
