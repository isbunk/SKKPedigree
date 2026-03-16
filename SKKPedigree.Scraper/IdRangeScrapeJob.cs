using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;

namespace SKKPedigree.Scraper
{
    public enum FetchStatus { Success, Empty, Missing, Timeout, RateLimited, ServerError }

    /// <summary>
    /// Scrapes all dogs sequentially or concurrently with an adaptive rate limiter.
    ///
    /// Sequential mode: starts at 1 req per 2-second window and steps up by 2 each phase
    /// (100 requests) while hard-error rate stays below 3%. Backs off on 429.
    /// Concurrent mode: fixed rate, multiple dogs in-flight simultaneously.
    /// Distinguishes 404/missing from real server errors.
    /// </summary>
    public class IdRangeScrapeJob
    {
        private const string BaseUrl   = "https://hundar.skk.se/hunddata/";
        public  const int    MaxHundId = 3_862_000;

        private const int    WindowMs  = 2_000;  // sliding window duration (ms)
        private const int    PhaseSize = 100;    // requests between rate evaluations
        private const double UpThresh  = 0.03;   // step up if hard-errors < 3%
        private const double DnThresh  = 0.10;   // step down if hard-errors > 10%
        private const int    MaxRate   = 321;    // max req per window (= 160.5 req/s)
        private const int    BatchSave = 50;     // flush to DB every N dogs (sequential only)

        private readonly DogRepository _dogRepo;
        private readonly DogScraper    _parser;
        private readonly HttpClient    _http;
        private StreamWriter?          _log;
        private readonly object        _logLock = new();

        public IdRangeScrapeJob(DogRepository dogRepo)
        {
            _dogRepo = dogRepo;
            _parser  = new DogScraper(null!);
            _http    = CreateHttpClient();
        }

        /// <summary>
        /// Creates an independent HttpClient with its own connection pool and cookie jar.
        /// Each concurrent worker needs its own instance so their ASP.NET sessions are
        /// independent — otherwise the server serializes all PostBacks for the same session.
        /// </summary>
        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer  = 10,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression   = System.Net.DecompressionMethods.GZip
                                         | System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9,en;q=0.8");
            return client;
        }

        /// <summary>
        /// Runs an adaptive scrape across [startId..endId].
        /// Progress tuple: (CurrentId, TotalSaved, TotalMissing, TotalHardErrors, ReqPerWindow, IdsPerMin)
        /// concurrency=1 uses the sequential adaptive path; >1 fires that many dogs in parallel.
        /// </summary>
        public async Task RunAdaptiveAsync(
            int     startId,
            int     endId,
            string  logPath,
            string  progressPath,
            string  missingLogPath,
            IProgress<(int Id, int Saved, int Missing, int HardErrors, int ReqPerWindow, double IdsPerMin)>? progress,
            CancellationToken ct,
            bool    rescrape    = false,
            int     concurrency = 1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            _log = new StreamWriter(logPath, append: true) { AutoFlush = true };

            await using var missingLog = new StreamWriter(missingLogPath, append: true) { AutoFlush = true };

            // Resume: advance past already-done IDs, but never go below startId
            int resumeFrom = startId;
            if (File.Exists(progressPath) &&
                int.TryParse(File.ReadAllText(progressPath).Trim(), out int lastDone) &&
                lastDone + 1 > startId)
            {
                resumeFrom = lastDone + 1;
                Log($"Resuming from hundid {resumeFrom} (last done: {lastDone})");
            }

            Log($"=== Scrape started [{resumeFrom}..{endId}], concurrency={concurrency}, windowMs={WindowMs} ===");

            HashSet<int> scrapedHundIds;
            if (rescrape)
            {
                scrapedHundIds = new HashSet<int>();
                Log("Rescrape mode — all HundIds will be re-fetched.");
            }
            else
            {
                Log("Pre-loading scraped HundIds...");
                scrapedHundIds = await _dogRepo.GetScrapedHundIdsAsync();
                Log($"{scrapedHundIds.Count:N0} HundIds already in DB — will skip.");
            }

            int reqPerWindow = rescrape ? MaxRate : 321;

            try
            {
                if (concurrency > 1)
                {
                    await RunConcurrentAsync(resumeFrom, endId, progressPath, missingLog,
                        reqPerWindow, scrapedHundIds, progress, ct, concurrency);
                }
                else
                {
                    await RunSequentialAsync(resumeFrom, endId, progressPath, missingLog,
                        reqPerWindow, scrapedHundIds, progress, ct);
                }

                Log($"=== Run ended. {DateTime.Now:yyyy-MM-dd HH:mm} ===");
            }
            finally
            {
                _log?.Dispose();
                _log = null;
            }
        }

        // ── Sequential path (original adaptive logic) ────────────────────────

        private async Task RunSequentialAsync(
            int resumeFrom, int endId, string progressPath, StreamWriter missingLog,
            int reqPerWindow, HashSet<int> scrapedHundIds,
            IProgress<(int Id, int Saved, int Missing, int HardErrors, int ReqPerWindow, double IdsPerMin)>? progress,
            CancellationToken ct)
        {
            int phaseRequests  = 0;
            int phaseHardErr   = 0;
            int totalSaved     = 0;
            int totalMissing   = 0;
            int totalHardError = 0;

            var saveBatch = new List<DogRecord>(BatchSave);
            var rateWatch = Stopwatch.StartNew();
            int rateCount = 0;

            for (int id = resumeFrom; id <= endId; id++)
            {
                ct.ThrowIfCancellationRequested();

                if (scrapedHundIds.Contains(id))
                {
                    if (id % 25 == 0) File.WriteAllText(progressPath, id.ToString());
                    continue;
                }

                int delayMs = WindowMs / reqPerWindow;
                if (delayMs > 0) await Task.Delay(delayMs, ct);

                if (phaseRequests == PhaseSize)
                {
                    double errRate = (double)phaseHardErr / PhaseSize;
                    string msg;

                    if (errRate <= UpThresh && reqPerWindow < MaxRate)
                    {
                        reqPerWindow = Math.Min(MaxRate, reqPerWindow + 2);
                        double rps = 1000.0 / (WindowMs / reqPerWindow);
                        msg = $"stepped UP   -> {rps:F1} req/s (1 req every {WindowMs/reqPerWindow}ms)";
                    }
                    else if (errRate >= DnThresh && reqPerWindow > 2)
                    {
                        reqPerWindow = Math.Max(2, reqPerWindow - 2);
                        double rps = 1000.0 / (WindowMs / reqPerWindow);
                        msg = $"stepped DOWN -> {rps:F1} req/s (1 req every {WindowMs/reqPerWindow}ms)";
                    }
                    else
                    {
                        double rps = 1000.0 / (WindowMs / reqPerWindow);
                        msg = $"stable       -> {rps:F1} req/s (1 req every {WindowMs/reqPerWindow}ms)";
                    }

                    Log($"[PHASE] {phaseHardErr}/{PhaseSize} hard-errors ({errRate:P0}). {msg}");
                    phaseRequests = 0;
                    phaseHardErr  = 0;
                }

                var (status, dog) = await FetchAdaptiveAsync(id, ct);
                rateCount++;
                phaseRequests++;

                switch (status)
                {
                    case FetchStatus.Success when dog != null:
                        dog.HundId = id;
                        saveBatch.Add(dog);
                        totalSaved++;
                        if (saveBatch.Count >= BatchSave)
                        {
                            await _dogRepo.UpsertBatchAsync(saveBatch);
                            saveBatch.Clear();
                        }
                        break;

                    case FetchStatus.Missing:
                        totalMissing++;
                        await missingLog.WriteLineAsync(id.ToString());
                        break;

                    case FetchStatus.RateLimited:
                        Log($"*** 429 RATE LIMITED at id={id} (was {reqPerWindow} req/{WindowMs}ms). " +
                            $"Cooling 30s, resuming at {Math.Max(1, reqPerWindow - 1)} req/{WindowMs}ms.");
                        reqPerWindow = Math.Max(2, reqPerWindow - 2);
                        phaseHardErr++;
                        totalHardError++;
                        await Task.Delay(30_000, ct);
                        id--;  // retry same ID
                        continue;

                    case FetchStatus.Timeout:
                    case FetchStatus.ServerError:
                        phaseHardErr++;
                        totalHardError++;
                        break;
                }

                if (id % 25 == 0)
                    File.WriteAllText(progressPath, id.ToString());

                double idsPerMin = rateWatch.Elapsed.TotalSeconds > 2
                    ? rateCount / rateWatch.Elapsed.TotalSeconds * 60.0 : 0;
                progress?.Report((id, totalSaved, totalMissing, totalHardError, reqPerWindow, idsPerMin));
            }

            File.WriteAllText(progressPath, endId.ToString());
            if (saveBatch.Count > 0)
                await _dogRepo.UpsertBatchAsync(saveBatch);

            Log($"=== Sequential complete. {totalSaved} saved, {totalMissing} missing, " +
                $"{totalHardError} hard-errors. ===");
        }

        // ── Concurrent path ───────────────────────────────────────────────────

        private async Task RunConcurrentAsync(
            int resumeFrom, int endId, string progressPath, StreamWriter missingLog,
            int reqPerWindow, HashSet<int> scrapedHundIds,
            IProgress<(int Id, int Saved, int Missing, int HardErrors, int ReqPerWindow, double IdsPerMin)>? progress,
            CancellationToken ct, int concurrency)
        {
            Log($"Concurrent mode: {concurrency} independent sessions, {reqPerWindow * 0.5:F1} req/s stagger.");

            // One HttpClient per worker = one independent ASP.NET session per worker.
            var clientPool = new System.Collections.Concurrent.ConcurrentBag<HttpClient>();
            for (int i = 0; i < concurrency; i++)
                clientPool.Add(CreateHttpClient());

            var sem     = new SemaphoreSlim(concurrency, concurrency);
            var dbMutex = new SemaphoreSlim(1, 1);

            // Shared write batch — workers append here under dbMutex.
            // Flushed as a single transaction every BatchSave dogs: 1 fsync for N dogs
            // instead of N fsyncs, eliminating the DB write bottleneck.
            var writeBatch = new List<DogRecord>(BatchSave);

            int totalSaved     = 0;
            int totalMissing   = 0;
            int totalHardError = 0;
            int rateCount      = 0;

            var rateWatch = Stopwatch.StartNew();
            var tasks     = new List<Task>(endId - resumeFrom + 1);

            for (int id = resumeFrom; id <= endId; id++)
            {
                ct.ThrowIfCancellationRequested();

                if (scrapedHundIds.Contains(id))
                {
                    if (id % 25 == 0) File.WriteAllText(progressPath, id.ToString());
                    continue;
                }

                int delayMs = WindowMs / reqPerWindow;
                if (delayMs > 0) await Task.Delay(delayMs, ct);

                await sem.WaitAsync(ct);
                clientPool.TryTake(out var workerClient);
                int capturedId = id;

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var (status, dog) = await FetchWithClientAsync(capturedId, workerClient!, ct);
                        Interlocked.Increment(ref rateCount);

                        switch (status)
                        {
                            case FetchStatus.Success when dog != null:
                                dog.HundId = capturedId;
                                await dbMutex.WaitAsync(ct);
                                try
                                {
                                    writeBatch.Add(dog);
                                    if (writeBatch.Count >= BatchSave)
                                    {
                                        await _dogRepo.UpsertBatchAsync(writeBatch);
                                        Interlocked.Add(ref totalSaved, writeBatch.Count);
                                        writeBatch.Clear();
                                    }
                                }
                                finally { dbMutex.Release(); }
                                break;
                            case FetchStatus.Missing:
                                Interlocked.Increment(ref totalMissing);
                                await dbMutex.WaitAsync(ct);
                                try   { await missingLog.WriteLineAsync(capturedId.ToString()); }
                                finally { dbMutex.Release(); }
                                break;
                            case FetchStatus.RateLimited:
                                Log($"*** 429 at id={capturedId}");
                                Interlocked.Increment(ref totalHardError);
                                break;
                            case FetchStatus.Timeout:
                            case FetchStatus.ServerError:
                                Interlocked.Increment(ref totalHardError);
                                break;
                        }

                        if (capturedId % 25 == 0)
                            File.WriteAllText(progressPath, capturedId.ToString());

                        int snap = rateCount;
                        double idsPerMin = rateWatch.Elapsed.TotalSeconds > 2
                            ? snap / rateWatch.Elapsed.TotalSeconds * 60.0 : 0;
                        progress?.Report((capturedId, totalSaved, totalMissing, totalHardError,
                            reqPerWindow, idsPerMin));
                    }
                    finally
                    {
                        clientPool.Add(workerClient!);
                        sem.Release();
                    }
                }, ct));
            }

            // Flush remaining dogs in the batch
            await Task.WhenAll(tasks);
            if (writeBatch.Count > 0)
            {
                await _dogRepo.UpsertBatchAsync(writeBatch);
                Interlocked.Add(ref totalSaved, writeBatch.Count);
            }
            File.WriteAllText(progressPath, endId.ToString());
            while (clientPool.TryTake(out var c)) c.Dispose();

            Log($"=== Concurrent({concurrency}) complete. {totalSaved} saved, {totalMissing} missing, " +
                $"{totalHardError} hard-errors. ===");
        }

        // ── HTTP helpers ──────────────────────────────────────────────────────

        // Sequential path uses the shared _http; concurrent path uses a per-worker client.
        private Task<(FetchStatus, DogRecord?)> FetchAdaptiveAsync(int id, CancellationToken ct)
            => FetchWithClientAsync(id, _http, ct);

        private async Task<(FetchStatus, DogRecord?)> FetchWithClientAsync(int id, HttpClient http, CancellationToken ct)
        {
            try
            {
                using var resp = await http.GetAsync(
                    $"{BaseUrl}Hund.aspx?hundid={id}",
                    HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return (FetchStatus.Missing, null);

                if ((int)resp.StatusCode == 429)
                    return (FetchStatus.RateLimited, null);

                if ((int)resp.StatusCode >= 500)
                {
                    Log($"  5xx id={id}: {(int)resp.StatusCode}");
                    return (FetchStatus.ServerError, null);
                }

                var html = await resp.Content.ReadAsStringAsync(ct);

                if (html.Contains("bodyContent_lblRegnr", StringComparison.OrdinalIgnoreCase))
                {
                    var dog     = _parser.ParseFromHtml(html);
                    var pageUrl = $"{BaseUrl}Hund.aspx?hundid={id}";
                    dog.HealthRecords = await FetchPostBackSectionAsync(pageUrl, html, "btnVeterinar", DogScraper.ParseHealthFromPostBack, http, ct);
                    dog.Results       = await FetchPostBackSectionAsync(pageUrl, html, "btnTavling",   DogScraper.ParseCompetitionFromPostBack, http, ct);
                    dog.Titles        = await FetchPostBackSectionAsync(pageUrl, html, "btnTitlar",    DogScraper.ParseTitlesFromPostBack, http, ct);
                    var breeder       = await FetchPostBackSectionAsync(pageUrl, html, "btnUppfodare",
                        h => { var b = DogScraper.ParseBreederFromPostBack(h); return new List<(string?,string?,string?)> { b }; }, http, ct);
                    if (breeder.Count > 0)
                    {
                        dog.KennelName  = breeder[0].Item1;
                        dog.BreederName = breeder[0].Item2;
                        dog.BreederCity = breeder[0].Item3;
                    }
                    return (FetchStatus.Success, dog);
                }

                return (FetchStatus.Empty, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException)   { return (FetchStatus.Timeout, null); }
            catch (HttpRequestException ex)
            {
                Log($"  HTTP err id={id}: {ex.StatusCode} " +
                    ex.Message[..Math.Min(80, ex.Message.Length)]);
                return (FetchStatus.ServerError, null);
            }
            catch (Exception ex)
            {
                Log($"  err id={id}: {ex.GetType().Name}");
                return (FetchStatus.ServerError, null);
            }
        }

        private async Task<List<T>> FetchPostBackSectionAsync<T>(
            string pageUrl, string initialHtml, string buttonName,
            Func<string, List<T>> parser, HttpClient http, CancellationToken ct)
        {
            try
            {
                var vs  = ExtractHidden(initialHtml, "__VIEWSTATE");
                var vsg = ExtractHidden(initialHtml, "__VIEWSTATEGENERATOR");
                var ev  = ExtractHidden(initialHtml, "__EVENTVALIDATION");
                if (string.IsNullOrEmpty(vs)) return new List<T>();

                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__EVENTTARGET"]        = $"ctl00$bodyContent${buttonName}",
                    ["__EVENTARGUMENT"]      = "",
                    ["__VIEWSTATE"]          = vs,
                    ["__VIEWSTATEGENERATOR"] = vsg,
                    ["__EVENTVALIDATION"]    = ev,
                });

                using var postResp = await http.PostAsync(pageUrl, form, ct);
                if (!postResp.IsSuccessStatusCode) return new List<T>();

                var postHtml = await postResp.Content.ReadAsStringAsync(ct);
                return parser(postHtml);
            }
            catch (Exception) { return new List<T>(); }
        }

        private static string ExtractHidden(string html, string fieldId)
        {
            var marker = $"id=\"{fieldId}\" value=\"";
            var start  = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "";
            start += marker.Length;
            var end = html.IndexOf('"', start);
            return end < 0 ? "" : html[start..end];
        }

        private void Log(string msg)
        {
            if (_log == null) return;
            lock (_logLock)
                _log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
    }
}
