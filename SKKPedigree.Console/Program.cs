using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SKKPedigree.Data;
using SKKPedigree.Scraper;

var appData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SKKPedigree");
Directory.CreateDirectory(appData);

var dbPath       = Path.Combine(appData, "pedigree.db");
var logPath      = Path.Combine(appData, "scrape_adaptive.txt");
var progressPath = Path.Combine(appData, "scrape_progress.txt");
var missingPath  = Path.Combine(appData, "scrape_missing.txt");

Console.WriteLine("=== SKK Adaptive Scraper ===");
Console.WriteLine($"DB:       {dbPath}");
Console.WriteLine($"Log:      {logPath}");
Console.WriteLine($"Progress: {progressPath}");
Console.WriteLine($"Missing:  {missingPath}");
Console.WriteLine();

// ── DB setup + cleanup ─────────────────────────────────────────────────────
using var db = new Database(dbPath);
await db.RunMigrationsAsync();
var dogRepo = new DogRepository(db);

int before   = await dogRepo.GetCountAsync();
int deleted  = await dogRepo.DeleteIncompleteAsync();
int after    = before - deleted;
int hundIds  = (await dogRepo.GetScrapedHundIdsAsync()).Count;
Console.WriteLine($"Dogs in DB before cleanup : {before:N0}");
if (deleted > 0)
    Console.WriteLine($"Deleted incomplete records: {deleted:N0}");
Console.WriteLine($"Valid dogs remaining      : {after:N0}");
Console.WriteLine($"HundIds stored (will skip): {hundIds:N0}");
Console.WriteLine();

// ── Resume from saved progress (delete the file to restart from ID 1) ──────
if (!File.Exists(progressPath))
    Console.WriteLine("No progress file — starting from hundid 1.");
else
    Console.WriteLine($"Progress file found — will resume from last checkpoint.");
Console.WriteLine();

// ── Rate display legend ────────────────────────────────────────────────────
Console.WriteLine("Columns: ID | saved | missing(404) | hard-errors | rate(req/2s) | speed(IDs/min)");
Console.WriteLine("Rate climbs by +1 req/2s every 100 requests while errors < 3%.");
Console.WriteLine("Ctrl+C to pause; rerun to resume from saved progress.");
Console.WriteLine(new string('─', 80));
Console.WriteLine();

// ── Run ────────────────────────────────────────────────────────────────────
var job = new IdRangeScrapeJob(dogRepo);

var progress = new Progress<(int Id, int Saved, int Missing, int HardErrors, int ReqPerWindow, double IdsPerMin)>(p =>
{
    Console.Write(
        $"\r  ID {p.Id,9:N0} | " +
        $"saved {p.Saved,7:N0} | " +
        $"missing {p.Missing,7:N0} | " +
        $"err {p.HardErrors,4} | " +
        $"{p.ReqPerWindow * 0.5:F1} req/s | " +
        $"{p.IdsPerMin,5:F0} IDs/min   ");
});

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n\nPaused — progress saved. Rerun to resume.");
};

try
{
    await job.RunAdaptiveAsync(
        startId:      1,
        endId:        IdRangeScrapeJob.MaxHundId,
        logPath:      logPath,
        progressPath: progressPath,
        missingLogPath: missingPath,
        progress:     progress,
        ct:           cts.Token);

    Console.WriteLine("\n\nComplete! All IDs processed.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nPaused.");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nFATAL: {ex.Message}");
    Console.ResetColor();
}
