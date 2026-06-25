using System.Windows;
using NetUsageMonitor.Common;
using NetUsageMonitor.Configuration;

namespace NetUsageMonitor;

public partial class CapDialog : Window
{
    public long LimitBytes { get; private set; }
    public CapPeriod Period { get; private set; }

    public CapDialog(string appName, AppCap? existing)
    {
        InitializeComponent();
        HeaderText.Text = $"Block \"{appName}\" when it uses more than:";

        if (existing is { LimitBytes: > 0 })
        {
            // Prefill from the existing cap.
            if (existing.LimitBytes % (1024L * 1024 * 1024) == 0)
            {
                AmountBox.Text = (existing.LimitBytes / (1024L * 1024 * 1024)).ToString();
                UnitBox.SelectedIndex = 1;
            }
            else
            {
                AmountBox.Text = Math.Max(1, existing.LimitBytes / (1024L * 1024)).ToString();
                UnitBox.SelectedIndex = 0;
            }
            PeriodBox.SelectedIndex = (int)existing.Period;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(AmountBox.Text, out double amount) || amount <= 0)
        {
            MessageBox.Show("Enter a number greater than zero.", "Set data limit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long multiplier = UnitBox.SelectedIndex == 1 ? 1024L * 1024 * 1024 : 1024L * 1024;
        LimitBytes = (long)(amount * multiplier);
        Period = (CapPeriod)PeriodBox.SelectedIndex;

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
