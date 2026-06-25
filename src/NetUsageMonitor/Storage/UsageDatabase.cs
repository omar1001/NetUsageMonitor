using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace NetUsageMonitor.Storage;

/// <summary>Aggregated traffic totals for one app group.</summary>
public readonly record struct UsageTotal(string GroupKey, long Sent, long Received);

/// <summary>One point of a per-app history series.</summary>
public readonly record struct HistoryPoint(long UnixSeconds, long Sent, long Received);

/// <summary>A sample row to persist for one app group in a flush.</summary>
public readonly record struct SampleRow(string GroupKey, string DisplayName, string? ExePath, long Sent, long Received);

/// <summary>
/// SQLite-backed store for network-usage samples. A single connection is guarded by a lock; access
/// frequency is low (a batched write every few seconds, light reads for the UI) so contention is
/// negligible. Only byte counts are stored — never any packet contents.
/// </summary>
public sealed class UsageDatabase : IDisposable
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, long> _appIdCache = new(StringComparer.OrdinalIgnoreCase);
    private SqliteConnection _connection = null!;
    private string _databasePath = string.Empty;

    public string DatabasePath => _databasePath;

    public void Open(string databasePath)
    {
        lock (_lock)
        {
            CloseInternal();

            _databasePath = databasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString());
            _connection.Open();

            Execute("PRAGMA journal_mode=WAL;");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute("PRAGMA temp_store=MEMORY;");

            Execute(@"
                CREATE TABLE IF NOT EXISTS apps (
                    id           INTEGER PRIMARY KEY,
                    group_key    TEXT NOT NULL UNIQUE,
                    display_name TEXT NOT NULL,
                    exe_path     TEXT
                );
                CREATE TABLE IF NOT EXISTS samples (
                    ts      INTEGER NOT NULL,
                    app_id  INTEGER NOT NULL,
                    sent    INTEGER NOT NULL,
                    recv    INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts);
                CREATE INDEX IF NOT EXISTS idx_samples_app_ts ON samples(app_id, ts);");

            _appIdCache.Clear();
        }
    }

    // ---- Writing ------------------------------------------------------------

    public void WriteSamples(long unixSeconds, IReadOnlyCollection<SampleRow> rows)
    {
        if (rows.Count == 0) return;

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();

            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO samples (ts, app_id, sent, recv) VALUES ($ts, $app, $sent, $recv);";
            var pTs = insert.Parameters.Add("$ts", SqliteType.Integer);
            var pApp = insert.Parameters.Add("$app", SqliteType.Integer);
            var pSent = insert.Parameters.Add("$sent", SqliteType.Integer);
            var pRecv = insert.Parameters.Add("$recv", SqliteType.Integer);

            foreach (var row in rows)
            {
                long appId = GetOrCreateAppId(row.GroupKey, row.DisplayName, row.ExePath, tx);
                pTs.Value = unixSeconds;
                pApp.Value = appId;
                pSent.Value = row.Sent;
                pRecv.Value = row.Received;
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private long GetOrCreateAppId(string groupKey, string displayName, string? exePath, SqliteTransaction tx)
    {
        if (_appIdCache.TryGetValue(groupKey, out long cached))
            return cached;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO apps (group_key, display_name, exe_path) VALUES ($k, $n, $p)
            ON CONFLICT(group_key) DO UPDATE SET display_name=excluded.display_name, exe_path=excluded.exe_path
            RETURNING id;";
        cmd.Parameters.AddWithValue("$k", groupKey);
        cmd.Parameters.AddWithValue("$n", displayName);
        cmd.Parameters.AddWithValue("$p", (object?)exePath ?? DBNull.Value);
        long id = Convert.ToInt64(cmd.ExecuteScalar());
        _appIdCache[groupKey] = id;
        return id;
    }

    // ---- Reading ------------------------------------------------------------

    /// <summary>Per-app totals for samples at or after <paramref name="sinceUnixSeconds"/> (0 = all time).</summary>
    public Dictionary<string, UsageTotal> GetTotalsSince(long sinceUnixSeconds)
    {
        var result = new Dictionary<string, UsageTotal>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT a.group_key, SUM(s.sent), SUM(s.recv)
                FROM samples s JOIN apps a ON a.id = s.app_id
                WHERE s.ts >= $since
                GROUP BY a.group_key;";
            cmd.Parameters.AddWithValue("$since", sinceUnixSeconds);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                long sent = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                long recv = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                result[key] = new UsageTotal(key, sent, recv);
            }
        }
        return result;
    }

    /// <summary>History points for one app since a timestamp, ordered by time.</summary>
    public List<HistoryPoint> GetHistory(string groupKey, long sinceUnixSeconds)
    {
        var points = new List<HistoryPoint>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT s.ts, SUM(s.sent), SUM(s.recv)
                FROM samples s JOIN apps a ON a.id = s.app_id
                WHERE a.group_key = $k AND s.ts >= $since
                GROUP BY s.ts ORDER BY s.ts;";
            cmd.Parameters.AddWithValue("$k", groupKey);
            cmd.Parameters.AddWithValue("$since", sinceUnixSeconds);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                points.Add(new HistoryPoint(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        return points;
    }

    // ---- Retention & deletion ----------------------------------------------

    /// <summary>Deletes samples older than the cutoff except those belonging to kept app groups.</summary>
    public int PruneExceptKept(long cutoffUnixSeconds, IReadOnlyCollection<string> keptKeys)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            if (keptKeys.Count == 0)
            {
                cmd.CommandText = "DELETE FROM samples WHERE ts < $cut;";
            }
            else
            {
                var names = new List<string>(keptKeys.Count);
                int i = 0;
                foreach (var key in keptKeys)
                {
                    string p = "$k" + i++;
                    names.Add(p);
                    cmd.Parameters.AddWithValue(p, key);
                }
                cmd.CommandText =
                    "DELETE FROM samples WHERE ts < $cut AND app_id NOT IN " +
                    "(SELECT id FROM apps WHERE group_key IN (" + string.Join(",", names) + "));";
            }
            cmd.Parameters.AddWithValue("$cut", cutoffUnixSeconds);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Deletes all recorded samples (keeps the app catalog).</summary>
    public void DeleteAllSamples()
    {
        lock (_lock)
        {
            Execute("DELETE FROM samples;");
        }
    }

    /// <summary>Deletes all samples for a single app group.</summary>
    public void DeleteApp(string groupKey)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE app_id IN (SELECT id FROM apps WHERE group_key = $k);";
            cmd.Parameters.AddWithValue("$k", groupKey);
            cmd.ExecuteNonQuery();
        }
    }

    public long GetDatabaseSizeBytes()
    {
        try { return new FileInfo(_databasePath).Length; }
        catch { return 0; }
    }

    // ---- Helpers ------------------------------------------------------------

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void CloseInternal()
    {
        if (_connection is not null)
        {
            try { _connection.Close(); _connection.Dispose(); }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseInternal();
        }
    }
}
