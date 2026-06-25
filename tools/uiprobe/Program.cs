using System;
using System.IO;
using NetUsageMonitor;
using NetUsageMonitor.Configuration;
using NetUsageMonitor.Engine;
using NetUsageMonitor.Ui;
using NetUsageMonitor.ViewModels;

namespace UiProbe;

// Headless smoke test of the WPF object graph: forces App.xaml + MainWindow.xaml + SettingsWindow.xaml
// to parse and lay out, surfacing missing StaticResources, bad converters, and template errors.
// Does NOT call App.Run()/OnStartup, so no ETW and no admin needed. The windows are never shown.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new App();
            app.InitializeComponent(); // load App.xaml resources

            var settings = new AppSettings
            {
                RecordsFolder = Path.Combine(Path.GetTempPath(), "netusage_uiprobe_" + Guid.NewGuid().ToString("N"))
            };

            var tracker = new UsageTracker(settings); // opens the DB only (no capture)
            var icons = new IconProvider();
            var vm = new MainViewModel(settings, tracker, icons, app.Dispatcher);

            var win = new MainWindow(vm);
            win.Measure(new System.Windows.Size(1000, 650));
            win.Arrange(new System.Windows.Rect(0, 0, 1000, 650));
            win.UpdateLayout();

            var settingsWin = new SettingsWindow(settings, tracker);
            settingsWin.Measure(new System.Windows.Size(560, 700));
            settingsWin.Arrange(new System.Windows.Rect(0, 0, 560, 700));
            settingsWin.UpdateLayout();

            tracker.Dispose();
            try { Directory.Delete(settings.RecordsFolder, true); } catch { /* ignore */ }

            Console.WriteLine("UI SMOKE OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("UI SMOKE FAIL:");
            Console.WriteLine(ex);
            return 1;
        }
    }
}
