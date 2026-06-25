using NetUsageMonitor.Common;
using NetUsageMonitor.Storage;

// Headless test of the real UsageDatabase + ByteFormatter (no admin / no ETW needed).
int failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok) failures++;
}

// ---- ByteFormatter ----
Check("Bytes 0", ByteFormatter.Bytes(0) == "0 B");
Check("Bytes 1536 -> 1.50 KB", ByteFormatter.Bytes(1536) == "1.50 KB");
Check("Bytes 5MB", ByteFormatter.Bytes(5L * 1024 * 1024) == "5.00 MB");
Check("Rate idle -> dash", ByteFormatter.Rate(0) == "—");
Check("Rate 2048 -> 2.00 KB/s", ByteFormatter.Rate(2048) == "2.00 KB/s");

// ---- UsageDatabase ----
string dir = Path.Combine(Path.GetTempPath(), "netusage_probe_" + Guid.NewGuid().ToString("N"));
string dbPath = Path.Combine(dir, "netusage.db");
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

using (var db = new UsageDatabase())
{
    db.Open(dbPath);
    Check("DB file created", File.Exists(dbPath));

    // Write two flushes for chrome, one for an old idle app.
    db.WriteSamples(now - 7200, new[] { new SampleRow("c:\\old\\idle.exe", "Idle App", "c:\\old\\idle.exe", 1000, 2000) }); // 2h old
    db.WriteSamples(now - 30, new[]
    {
        new SampleRow("c:\\app\\chrome.exe", "Google Chrome", "c:\\app\\chrome.exe", 100, 900),
        new SampleRow("c:\\app\\steam.exe",  "Steam",        "c:\\app\\steam.exe",  50,  50),
    });
    db.WriteSamples(now - 10, new[]
    {
        new SampleRow("c:\\app\\chrome.exe", "Google Chrome", "c:\\app\\chrome.exe", 200, 1100),
    });

    // ON CONFLICT upsert must reuse the same app id (chrome written twice).
    var lastHour = db.GetTotalsSince(now - 3600);
    Check("chrome totals merged across flushes",
        lastHour.TryGetValue("c:\\app\\chrome.exe", out var chrome) && chrome.Sent == 300 && chrome.Received == 2000);
    Check("steam present in last hour", lastHour.ContainsKey("c:\\app\\steam.exe"));
    Check("old idle app excluded from last hour", !lastHour.ContainsKey("c:\\old\\idle.exe"));

    // All-time includes the 2h-old idle app.
    var allTime = db.GetTotalsSince(0);
    Check("all-time includes idle app", allTime.ContainsKey("c:\\old\\idle.exe"));

    // History series for chrome (two points).
    var history = db.GetHistory("c:\\app\\chrome.exe", now - 3600);
    Check("chrome history has 2 points", history.Count == 2);

    // Prune older than 1h, but keep the idle app via the kept list.
    int deleted = db.PruneExceptKept(now - 3600, new[] { "c:\\old\\idle.exe" });
    Check("prune deleted nothing (idle is kept)", deleted == 0);
    Check("idle still present after kept-prune", db.GetTotalsSince(0).ContainsKey("c:\\old\\idle.exe"));

    // Prune older than 1h with NO kept list -> idle removed.
    db.PruneExceptKept(now - 3600, Array.Empty<string>());
    Check("idle removed after unkept-prune", !db.GetTotalsSince(0).ContainsKey("c:\\old\\idle.exe"));
    Check("chrome survives prune (recent)", db.GetTotalsSince(0).ContainsKey("c:\\app\\chrome.exe"));

    // Delete one app's records.
    db.DeleteApp("c:\\app\\steam.exe");
    Check("steam records deleted", !db.GetTotalsSince(0).ContainsKey("c:\\app\\steam.exe"));

    // Delete all.
    db.DeleteAllSamples();
    Check("all records deleted", db.GetTotalsSince(0).Count == 0);
    Check("db size reported > 0", db.GetDatabaseSizeBytes() > 0);
}

try { Directory.Delete(dir, true); } catch { /* ignore */ }

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures == 0 ? 0 : 1;
