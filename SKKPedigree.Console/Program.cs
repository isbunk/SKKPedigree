using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SKKPedigree.Data;
using SKKPedigree.Scraper;

// CLI args: [--db <name>] [--start <hundid>] [--end <hundid>]
// Example: dotnet run --project SKKPedigree.Console -- --db shard2 --start 800001 --end 1600000
string shardName  = "pedigree";
int    argStart   = 1;
int    argEnd     = IdRangeScrapeJob.MaxHundId;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--db"    && i + 1 < args.Length) shardName = args[++i];
    if (args[i] == "--start" && i + 1 < args.Length) argStart  = int.Parse(args[++i]);
    if (args[i] == "--end"   && i + 1 < args.Length) argEnd    = int.Parse(args[++i]);
}

var appData = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SKKPedigree");
Directory.CreateDirectory(appData);

var dbPath       = Path.Combine(appData, shardName + ".db");
var logPath      = Path.Combine(appData, shardName + "_log.txt");
var progressPath = Path.Combine(appData, shardName + "_progress.txt");
var missingPath  = Path.Combine(appData, shardName + "_missing.txt");

Console.WriteLine("=== SKK Scraper (concurrency=20) ===");
Console.WriteLine($"Shard:    {shardName}  [{argStart:N0} – {argEnd:N0}]");
Console.WriteLine($"DB:       {dbPath}");
Console.WriteLine($"Progress: {progressPath}");
Console.WriteLine($"Ctrl+C to pause — progress is saved automatically.");
Console.WriteLine();

using var db = new Database(dbPath);
await db.RunMigrationsAsync();
var dogRepo = new DogRepository(db);

int before  = await dogRepo.GetCountAsync();
int deleted = await dogRepo.DeleteIncompleteAsync();
Console.WriteLine($"Dogs in DB : {before - deleted:N0}");
if (File.Exists(progressPath))
    Console.WriteLine($"Progress   : resuming from last checkpoint");
else
    Console.WriteLine($"Progress   : starting from HundId 1");
Console.WriteLine();

var cts        = new CancellationTokenSource();
var wallClock  = Stopwatch.StartNew();
var lastReport = Stopwatch.StartNew();
int lastId     = 0;
int lastSaved  = 0;

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n\nPaused — rerun to resume.");
};

Console.WriteLine("Time     | HundId    | Saved    | Missing  | Errors | dogs/sec");
Console.WriteLine(new string('─', 65));

var job = new IdRangeScrapeJob(dogRepo);

// Archive worker — runs in the background, moves completed ranges out of the working DB
// every 5 minutes so it stays small and insert-fast.
var archivePath = Path.Combine(appData, shardName + "_archive.db");
_ = SKKPedigree.Console.ArchiveWorker.RunAsync(dbPath, archivePath, () => lastId, logPath: logPath, ct: cts.Token);

var progress = new Progress<(int Id, int Saved, int Missing, int HardErrors, int ReqPerWindow, double IdsPerMin)>(p =>
{
    lastId    = p.Id;
    lastSaved = p.Saved;

    if (lastReport.Elapsed.TotalSeconds >= 10)
    {
        lastReport.Restart();
        Console.WriteLine(
            $"{wallClock.Elapsed:hh\\:mm\\:ss} | {p.Id,9:N0} | {p.Saved,8:N0} | {p.Missing,8:N0} | {p.HardErrors,6} | {p.IdsPerMin / 60.0:F1}");
    }
});

try
{
    await job.RunAdaptiveAsync(
        startId:        argStart,
        endId:          argEnd,
        logPath:        logPath,
        progressPath:   progressPath,
        missingLogPath: missingPath,
        progress:       progress,
        ct:             cts.Token,
        rescrape:       true,
        concurrency:    20);

    Console.WriteLine("\nComplete! All HundIds processed.");
}
catch (OperationCanceledException)
{
    Console.WriteLine($"\nPaused at HundId {lastId:N0}.");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\nFATAL: {ex.Message}");
    Console.ResetColor();
}

double finalDps = wallClock.Elapsed.TotalSeconds > 0 ? lastSaved / wallClock.Elapsed.TotalSeconds : 0;
Console.WriteLine($"Session: {lastSaved:N0} dogs in {wallClock.Elapsed:hh\\:mm\\:ss} ({finalDps:F1} dogs/sec)");
