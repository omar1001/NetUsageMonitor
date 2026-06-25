using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;
using NetUsageMonitor.ViewModels;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace NetUsageMonitor;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\NetUsageMonitor.Instance";
    private const string ShowEventName = @"Local\NetUsageMonitor.ShowWindow";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    private AppSettings _settings = null!;
    private UsageTracker _tracker = null!;
    private IconProvider _icons = null!;
    private MainViewModel _viewModel = null!;
    private MainWindow? _mainWindow;
    private WinForms.NotifyIcon? _trayIcon;

    private bool _trayBalloonShown;
    private bool _faultReported;

    public bool IsExiting { get; private set; }
    public AppSettings Settings => _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- Single instance: bring the existing window forward instead of starting twice ----
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        StartShowSignalListener();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Unexpected error: " + args.Exception.Message, "NetUsage Monitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // ---- Settings & records folder ----
        _settings = AppSettings.Load();
        TryEnsureRecordsFolder();
        SyncAutoStart();

        // ---- Engine ----
        _icons = new IconProvider();
        _tracker = new UsageTracker(_settings);
        _tracker.Faulted += OnTrackerFaulted;
        _tracker.Start();

        // ---- UI ----
        _viewModel = new MainViewModel(_settings, _tracker, _icons, Dispatcher);
        _mainWindow = new MainWindow(_viewModel);

        CreateTrayIcon();

        bool startMinimized = e.Args.Contains("--minimized") || _settings.StartMinimized;
        if (!startMinimized)
            ShowMainWindow();
    }

    private void StartShowSignalListener()
    {
        var thread = new Thread(() =>
        {
            while (!IsExiting)
            {
                try
                {
                    if (_showEvent!.WaitOne())
                        Dispatcher.BeginInvoke(ShowMainWindow);
                }
                catch { break; }
            }
        })
        { IsBackground = true, Name = "ShowSignalListener" };
        thread.Start();
    }

    private void TryEnsureRecordsFolder()
    {
        try { Directory.CreateDirectory(_settings.RecordsFolder); }
        catch
        {
            _settings.RecordsFolder = AppPaths.DefaultRecordsFolder;
            Directory.CreateDirectory(_settings.RecordsFolder);
            _settings.Save();
        }
    }

    private void SyncAutoStart()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
            bool enabled = AutoStartManager.IsEnabled();
            if (_settings.StartWithWindows && !enabled) AutoStartManager.Enable(exe);
            else if (!_settings.StartWithWindows && enabled) AutoStartManager.Disable();
        }
        catch { /* best effort */ }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "NetUsage Monitor",
            Visible = true,
            Icon = LoadTrayIcon()
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open NetUsage Monitor", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Pause / resume recording", null, (_, _) => _viewModel.ToggleRecordingCommand.Execute(null));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream != null) return new Drawing.Icon(stream);
        }
        catch { /* ignore */ }
        return Drawing.SystemIcons.Application;
    }

    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    /// <summary>Hides the window to the tray, keeping the engine running.</summary>
    public void HideToTray()
    {
        _mainWindow?.Hide();
        if (!_trayBalloonShown && _trayIcon != null)
        {
            _trayBalloonShown = true;
            _trayIcon.ShowBalloonTip(3000, "Still recording",
                "NetUsage Monitor keeps recording in the background. Right-click the tray icon to exit.",
                WinForms.ToolTipIcon.Info);
        }
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;

        try { _showEvent?.Set(); } catch { /* unblock listener */ }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        try { _tracker?.Dispose(); } catch { /* ignore */ }
        try { _settings?.Save(); } catch { /* ignore */ }

        Shutdown();
    }

    private void OnTrackerFaulted(Exception ex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try { _tracker.Stop(); } catch { /* ignore */ }
            if (_faultReported) return;
            _faultReported = true;
            MessageBox.Show(
                "Network capture could not run.\n\n" + ex.Message +
                "\n\nThis app needs to run as administrator to read per-app network usage. " +
                "Close it and choose \"Run as administrator\".",
                "NetUsage Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _trayIcon?.Dispose(); } catch { /* ignore */ }
        try { _instanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
