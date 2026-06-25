using System.Globalization;

namespace NetUsageMonitor.Common;

/// <summary>Human-readable byte and rate formatting (auto-scales to KB / MB / GB / TB).</summary>
public static class ByteFormatter
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Formats a byte count, e.g. 1536 -> "1.50 KB".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Whole bytes need no decimals; larger units read better with two.
        string format = unit == 0 ? "0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + SizeUnits[unit];
    }

    /// <summary>Formats a rate in bytes-per-second, e.g. "1.50 MB/s". Returns "—" for zero.</summary>
    public static string Rate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1) return "—";
        return Bytes((long)Math.Round(bytesPerSecond)) + "/s";
    }
}
