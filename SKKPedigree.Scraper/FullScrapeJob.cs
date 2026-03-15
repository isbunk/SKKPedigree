using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;

namespace SKKPedigree.Scraper
{
    public class FullScrapeProgress
    {
        public int ScrapedTotal { get; set; }
        public int QueuedCount { get; set; }
        public int SkippedCount { get; set; }
        public int ErrorCount { get; set; }
        public string CurrentUrl { get; set; } = "";
        public string CurrentDogName { get; set; } = "";
        public string Status { get; set; } = "Idle";
        public string LogLine { get; set; } = "";
    }

    public class FullScrapeOptions
    {
        /// <summary>How many dogs to scrape before saving a batch to the DB.</summary>
        public int BatchSize { get; set; } = 10;

        /// <summary>Dogs scraped within this window are skipped.</summary>
        public TimeSpan SkipIfScrapedWithin { get; set; } = TimeSpan.FromDays(30);

        /// <summary>Delay between requests (ms). Must be ≥ 1500 to be polite.</summary>
        public int RequestDelayMs { get; set; } = 1500;

        public bool Headless { get; set; } = true;

        /// <summary>Path to the log file. Default: %APPDATA%\SKKPedigree\scrape_log.txt</summary>
        public string LogFilePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SKKPedigree", "scrape_log.txt");

        /// <summary>
        /// Optional breed dropdown ID to restrict the scrape to one breed.
        /// Empty = all breeds. Example: "244" = Rottweiler.
        /// </summary>
        public string BreedId { get; set; } = "";
    }

    /// <summary>
    /// Crawls the entire SKK Hunddata database by:
    ///   1. Searching with each letter of the Swedish alphabet to discover dog URLs.
    ///   2. BFS-crawling every discovered parent/sibling/child link.
    ///   3. Saving to SQLite in batches of <see cref="FullScrapeOptions.BatchSize"/>.
    ///   4. Writing a persistent log file and reporting progress.
    ///
    /// Can be resumed: dogs already in the DB (scraped within SkipIfScrapedWithin)
    /// are skipped automatically on the next run.
    /// </summary>
    public class FullScrapeJob
    {
        // Swedish alphabet — used as the first-level seed and for recursive subdivision
        private static readonly string[] SwAlpha = new[]
        {
            "a","b","c","d","e","f","g","h","i","j","k","l","m",
            "n","o","p","q","r","s","t","u","v","w","x","y","z",
            "å","ä","ö"
        };

        private readonly SkkSession _session;
        private readonly DogScraper _scraper;
        private readonly DogRepository _dogRepo;

        private StreamWriter? _logWriter;
        private FullScrapeOptions _opts = new();
        private IProgress<FullScrapeProgress>? _progress;
        private FullScrapeProgress _state = new();

        // Tracks URLs already enqueued or visited this session
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);

        // Pending dog-detail URLs to fetch
        private readonly Queue<string> _queue = new();

        // Accumulation buffer — flushed every BatchSize records
        private readonly List<DogRecord> _batch = new();

        public FullScrapeJob(SkkSession session, DogScraper scraper, DogRepository dogRepo)
        {
            _session = session;
            _scraper = scraper;
            _dogRepo = dogRepo;
        }

        public async Task RunAsync(
            FullScrapeOptions options,
            IProgress<FullScrapeProgress>? progress,
            CancellationToken ct)
        {
            _opts = options;
            _progress = progress;
            _state = new FullScrapeProgress { Status = "Starting" };

            Directory.CreateDirectory(Path.GetDirectoryName(options.LogFilePath)!);
            _logWriter = new StreamWriter(options.LogFilePath, append: true)
            {
                AutoFlush = true
            };

            Log($"=== Full scrape started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            try
            {
                // Pre-seed visited set from DB so already-scraped dogs are skipped
                Log("Loading previously scraped IDs from database…");
                var existing = await _dogRepo.GetRecentlyScrapedIdsAsync(options.SkipIfScrapedWithin);
                _state.SkippedCount = existing.Count;
                Log($"  → {existing.Count} dogs already in DB (will be skipped if URL matches).");

                // Initialise browser session
                Log("Initialising browser session…");
                await _session.InitAsync(options.Headless);

                // Phase 1: Recursive seed discovery with prefix subdivision
                var breedLabel = string.IsNullOrEmpty(options.BreedId) ? "all breeds" : $"breed {options.BreedId}";
                Log($"Phase 1: Recursive seed discovery ({breedLabel})…");
                _state.Status = "Discovering";
                Report("Discovering dog URLs via recursive prefix search…");

                // Breadth-first prefix queue: start with single letters, subdivide when > 300
                var prefixQueue = new Queue<string>(SwAlpha);
                int discoveryQueries = 0;

                while (prefixQueue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var prefix = prefixQueue.Dequeue();
                    Report($"Prefix '{prefix}' | found {_visited.Count} so far");

                    int total;
                    List<int> ids;
                    try
                    {
                        (total, ids) = await _session.SearchApiAsync(prefix, options.BreedId);
                        discoveryQueries++;
                    }
                    catch (Exception ex)
                    {
                        Log($"  ERROR on prefix '{prefix}': {ex.Message}");
                        await TryResetSessionAsync(options.Headless);
                        continue;
                    }

                    int newLinks = 0;
                    foreach (var id in ids)
                    {
                        var url = $"Hund.aspx?hundid={id}";
                        if (_visited.Add(url))
                        {
                            _queue.Enqueue(url);
                            newLinks++;
                        }
                    }

                    if (total > 300 && prefix.Length < 6)
                    {
                        // Results were capped — subdivide this prefix with each letter
                        foreach (var ext in SwAlpha)
                            prefixQueue.Enqueue(prefix + ext);
                        Log($"  '{prefix}': total={total} > 300, subdividing → {SwAlpha.Length} sub-prefixes added (queue: {prefixQueue.Count})");
                    }
                    else
                    {
                        Log($"  '{prefix}': total={total}, {newLinks} new IDs. Dog queue: {_queue.Count}");
                    }
                }

                _state.QueuedCount = _queue.Count;
                Log($"Phase 1 complete. {_queue.Count} unique dog URLs queued ({discoveryQueries} API calls).");

                // Phase 2: BFS crawl every queued URL
                Log("Phase 2: Scraping all queued dogs and following parent/sibling links…");
                _state.Status = "Scraping";

                while (_queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    var url = _queue.Dequeue();
                    _state.QueuedCount = _queue.Count;
                    _state.CurrentUrl = url;

                    DogRecord dog;
                    try
                    {
                        Report($"Scraping: {url}");
                        dog = await _scraper.ScrapeByUrlAsync(url);
                    }
                    catch (Exception ex)
                    {
                        _state.ErrorCount++;
                        Log($"ERROR scraping {url}: {ex.Message}");
                        Report($"ERROR: {ex.Message}");
                        await TryResetSessionAsync(options.Headless);
                        continue;
                    }

                    _state.CurrentDogName = dog.Name;
                    Log($"  [{_state.ScrapedTotal + 1}] {dog.Name} ({dog.Id}) — {dog.Breed ?? "?"} {dog.Sex ?? "?"}");
                    Report($"Scraped: {dog.Name} ({dog.Id})");

                    _batch.Add(dog);

                    // Queue parents and siblings that haven't been visited
                    Enqueue(dog.FatherUrl);
                    Enqueue(dog.MotherUrl);
                    foreach (var s in dog.SiblingUrls) Enqueue(s);

                    // Flush batch every BatchSize records
                    if (_batch.Count >= options.BatchSize)
                        await FlushBatchAsync();
                }

                // Final flush for any remaining records
                if (_batch.Count > 0)
                    await FlushBatchAsync();

                _state.Status = "Done";
                var dbTotal = await _dogRepo.GetCountAsync();
                Log($"=== Full scrape complete. {_state.ScrapedTotal} scraped this session. " +
                    $"{_state.ErrorCount} errors. DB total: {dbTotal} dogs. " +
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                Report($"Done. Scraped {_state.ScrapedTotal} dogs. DB total: {dbTotal}.");
            }
            catch (OperationCanceledException)
            {
                if (_batch.Count > 0)
                    await FlushBatchAsync();
                _state.Status = "Cancelled";
                Log($"=== Scrape CANCELLED by user. {_state.ScrapedTotal} scraped this session. ===");
                Report("Cancelled.");
            }
            catch (Exception ex)
            {
                _state.Status = "Error";
                Log($"=== FATAL ERROR: {ex} ===");
                Report($"Fatal error: {ex.Message}");
            }
            finally
            {
                _logWriter?.Dispose();
                _logWriter = null;
            }
        }

        private void Enqueue(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && _visited.Add(url))
            {
                _queue.Enqueue(url);
                _state.QueuedCount = _queue.Count;
            }
        }

        private async Task FlushBatchAsync()
        {
            if (_batch.Count == 0) return;
            Log($"  Saving batch of {_batch.Count} dogs to database…");
            try
            {
                await _dogRepo.UpsertBatchAsync(_batch);
                _state.ScrapedTotal += _batch.Count;
                Log($"  ✓ Saved. Total so far: {_state.ScrapedTotal}");
            }
            catch (Exception ex)
            {
                Log($"  ERROR saving batch: {ex.Message} — trying individually…");
                foreach (var d in _batch)
                {
                    try { await _dogRepo.UpsertAsync(d); _state.ScrapedTotal++; }
                    catch (Exception innerEx) { Log($"    SKIP {d.Id}: {innerEx.Message}"); }
                }
            }
            _batch.Clear();
        }

        private async Task TryResetSessionAsync(bool headless)
        {
            Log("  Resetting browser session after error…");
            try { await _session.ResetAsync(headless); }
            catch (Exception ex) { Log($"  Session reset failed: {ex.Message}"); }
        }

        private void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logWriter?.WriteLine(line);
        }

        private void Report(string logLine)
        {
            _state.LogLine = logLine;
            _progress?.Report(new FullScrapeProgress
            {
                ScrapedTotal = _state.ScrapedTotal,
                QueuedCount = _state.QueuedCount,
                SkippedCount = _state.SkippedCount,
                ErrorCount = _state.ErrorCount,
                CurrentUrl = _state.CurrentUrl,
                CurrentDogName = _state.CurrentDogName,
                Status = _state.Status,
                LogLine = logLine
            });
        }
    }
}
