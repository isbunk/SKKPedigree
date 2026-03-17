using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using SKKPedigree.Data;

namespace SKKPedigree.Console
{
    /// <summary>
    /// Background task that periodically moves completed dog ranges from the working DB
    /// into a separate archive DB, keeping the working DB small and insert-fast.
    ///
    /// Uses SQLite WAL mode — the archiver and scraper contend for the write lock normally;
    /// busy_timeout ensures the archiver waits for the scraper's batch to commit.
    /// </summary>
    public static class ArchiveWorker
    {
        private const int MinDogsToArchive = 5_000;   // skip tiny batches
        private const int BufferIds        = 20_000;  // always leave this many recent IDs in working DB

        private static StreamWriter? _log;
        private static readonly object _logLock = new();

        private static void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [Archive] {msg}";
            System.Console.WriteLine(line);
            if (_log == null) return;
            lock (_logLock)
                _log.WriteLine(line);
        }

        public static async Task RunAsync(
            string        workingDbPath,
            string        archiveDbPath,
            Func<int>     getCurrentHundId,
            string?       logPath         = null,
            int           intervalMinutes = 5,
            CancellationToken ct = default)
        {
            if (logPath != null)
                _log = new StreamWriter(
                    new System.IO.FileStream(logPath, System.IO.FileMode.Append,
                        System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite))
                    { AutoFlush = true };

            // Ensure archive DB has the full schema before we start writing to it.
            using (var archDb = new Database(archiveDbPath))
                await archDb.RunMigrationsAsync();

            Log($"Started — archiving every {intervalMinutes} min to {Path.GetFileName(archiveDbPath)}");

            while (!ct.IsCancellationRequested)
            {
                try   { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct); }
                catch (OperationCanceledException) { break; }

                int threshold = getCurrentHundId() - BufferIds;
                if (threshold <= 0) continue;

                try   { await ArchiveBatchAsync(workingDbPath, archiveDbPath, threshold, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Log($"Error: {ex.Message}");
                }
            }

            Log("Stopped.");
            _log?.Dispose();
            _log = null;
        }

        private static async Task ArchiveBatchAsync(
            string workingPath, string archivePath, int threshold, CancellationToken ct)
        {
            using var conn = new SqliteConnection($"Data Source={workingPath}");
            await conn.OpenAsync(ct);

            // Wait up to 30s for the scraper's write lock rather than failing immediately.
            await conn.ExecuteAsync("PRAGMA busy_timeout = 30000; PRAGMA cache_size = -51200;");

            int count = await conn.QueryFirstAsync<int>(
                "SELECT COUNT(*) FROM Dog WHERE HundId IS NOT NULL AND HundId <= @t",
                new { t = threshold });

            if (count < MinDogsToArchive)
            {
                Log($" {count:N0} dogs below HundId {threshold:N0} — waiting for more.");
                return;
            }

            Log($" Moving {count:N0} dogs (HundId ≤ {threshold:N0}) to archive...");
            var sw = Stopwatch.StartNew();

            // Use ATTACH so the copy+delete is a single atomic transaction.
            // Escape single quotes in path (Windows paths are safe, but defensive).
            var escapedArchive = archivePath.Replace("'", "''");
            await conn.ExecuteAsync($"ATTACH DATABASE '{escapedArchive}' AS arch");

            try
            {
                using var tx = conn.BeginTransaction();

                // ── Copy to archive ────────────────────────────────────────────
                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO arch.Dog SELECT * FROM Dog " +
                    "WHERE HundId IS NOT NULL AND HundId <= @t", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO arch.Litter SELECT l.* FROM Litter l " +
                    "INNER JOIN Dog d ON l.Id = d.LitterId " +
                    "WHERE d.HundId IS NOT NULL AND d.HundId <= @t", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO arch.HealthRecord SELECT hr.* FROM HealthRecord hr " +
                    "INNER JOIN Dog d ON hr.DogId = d.Id " +
                    "WHERE d.HundId IS NOT NULL AND d.HundId <= @t", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO arch.CompetitionResult SELECT cr.* FROM CompetitionResult cr " +
                    "INNER JOIN Dog d ON cr.DogId = d.Id " +
                    "WHERE d.HundId IS NOT NULL AND d.HundId <= @t", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "INSERT OR REPLACE INTO arch.Title SELECT ti.* FROM Title ti " +
                    "INNER JOIN Dog d ON ti.DogId = d.Id " +
                    "WHERE d.HundId IS NOT NULL AND d.HundId <= @t", new { t = threshold }, tx);

                // ── Delete from working (child tables first — FK order) ────────
                await conn.ExecuteAsync(
                    "DELETE FROM Title WHERE DogId IN " +
                    "(SELECT Id FROM Dog WHERE HundId IS NOT NULL AND HundId <= @t)", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "DELETE FROM CompetitionResult WHERE DogId IN " +
                    "(SELECT Id FROM Dog WHERE HundId IS NOT NULL AND HundId <= @t)", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "DELETE FROM HealthRecord WHERE DogId IN " +
                    "(SELECT Id FROM Dog WHERE HundId IS NOT NULL AND HundId <= @t)", new { t = threshold }, tx);

                await conn.ExecuteAsync(
                    "DELETE FROM Dog WHERE HundId IS NOT NULL AND HundId <= @t", new { t = threshold }, tx);

                tx.Commit();

                Log($" Done in {sw.Elapsed.TotalSeconds:F1}s. " +
                    $"{count:N0} dogs archived, working DB trimmed.");
            }
            finally
            {
                await conn.ExecuteAsync("DETACH DATABASE arch");
            }
        }
    }
}
