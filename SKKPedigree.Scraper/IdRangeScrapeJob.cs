using System;
using System.Collections.Generic;
using System.IO;
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
    /// Scrapes all dogs sequentially with an adaptive rate limiter.
    ///
    /// Starts at 1 request per 2-second window and steps up by 1 each phase
    /// (100 requests) while hard-error rate stays below 3%. Backs off on 429.
    /// Distinguishes 404/missing from real server errors.
    /// </summary>
    public class IdRangeScrapeJob
    {
        private const string BaseUrl   = "https://hundar.skk.se/hunddata/";
        public  const int    MaxHundId = 3_862_000;

        private const int    WindowMs  = 2_000;  // sliding window duration (ms)
        private const int    PhaseSize = 20;     // requests between rate evaluations (smaller = faster ramp)
        private const double UpThresh  = 0.03;   // step up if hard-errors < 3%
        private const double DnThresh  = 0.10;   // step down if hard-errors > 10%
        private const int    MaxRate   = 30;     // max req per window
        private const int    BatchSave = 50;     // flush to DB every N dogs

        private readonly DogRepository _dogRepo;
        private readonly DogScraper    _parser;
        private readonly HttpClient    _http;
        private StreamWriter?          _log;

        public IdRangeScrapeJob(DogRepository dogRepo)
        {
            _dogRepo = dogRepo;
            _parser  = new DogScraper(null!);

            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer  = 35,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression   = System.Net.DecompressionMethods.GZip
                                         | System.Net.DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            _http.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _http.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9,en;q=0.8");
        }

        /// <summary>
        /// Runs an adaptive sequential scrape across [startId..endId].
        /// Progress tuple: (CurrentId, TotalSaved, TotalMissing, TotalHardErrors, ReqPerWindow, IdsPerMin)
        /// </summary>
        public async Task RunAdaptiveAsync(
            int     startId,
            int     endId,
            string  logPath,
            string  progressPath,
            string  missingLogPath,
            IProgress<(int Id, int Saved, int Missing, int HardErrors, int ReqPerWindow, double IdsPerMin)>? progress,
            CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            _log = new StreamWriter(logPath, append: true) { AutoFlush = true };

            await using var missingLog = new StreamWriter(missingLogPath, append: true) { AutoFlush = true };

            // Resume
            int resumeFrom = startId;
            if (File.Exists(progressPath) &&
                int.TryParse(File.ReadAllText(progressPath).Trim(), out int lastDone))
            {
                resumeFrom = lastDone + 1;
                Log($"Resuming from hundid {resumeFrom} (last done: {lastDone})");
            }

            Log($"=== Adaptive scrape started [{resumeFrom}..{endId}], windowMs={WindowMs} ===");

            // Pre-load scraped HundIds so we can skip already-saved dogs instantly
            Log("Pre-loading scraped HundIds...");
            var scrapedHundIds = await _dogRepo.GetScrapedHundIdsAsync();
            Log($"{scrapedHundIds.Count:N0} HundIds already in DB — will skip.");

            int  reqPerWindow   = 1;  // requests allowed per WindowMs — delay = WindowMs/reqPerWindow between each

            int  phaseRequests  = 0;
            int  phaseHardErr   = 0;

            int  totalSaved     = 0;
            int  totalMissing   = 0;
            int  totalHardError = 0;

            var  saveBatch      = new List<DogRecord>(BatchSave);
            var  rateWatch      = System.Diagnostics.Stopwatch.StartNew();
            int  rateCount      = 0;

            for (int id = resumeFrom; id <= endId; id++)
            {
                ct.ThrowIfCancellationRequested();

                // Skip IDs already stored in the database
                if (scrapedHundIds.Contains(id))
                {
                    if (id % 25 == 0)
                        File.WriteAllText(progressPath, id.ToString());
                    continue;
                }

                // Evenly-spaced delay: spread requests across the window rather than bursting
                int delayMs = WindowMs / reqPerWindow;
                if (delayMs > 0)
                    await Task.Delay(delayMs, ct);

                // Phase evaluation: adjust rate
                if (phaseRequests == PhaseSize)
                {
                    double errRate = (double)phaseHardErr / PhaseSize;
                    string msg;

                    if (errRate <= UpThresh && reqPerWindow < MaxRate)
                    {
                        reqPerWindow++;
                        double rps = 1000.0 / (WindowMs / reqPerWindow);
                        msg = $"stepped UP   -> {rps:F1} req/s (1 req every {WindowMs/reqPerWindow}ms)";
                    }
                    else if (errRate >= DnThresh && reqPerWindow > 1)
                    {
                        reqPerWindow--;
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

                // Fetch
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
                        reqPerWindow = Math.Max(1, reqPerWindow - 1);
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

                    // FetchStatus.Empty — valid page, no dog; skip
                }

                // Persist progress every 25 IDs
                if (id % 25 == 0)
                    File.WriteAllText(progressPath, id.ToString());

                // Report
                double idsPerMin = rateWatch.Elapsed.TotalSeconds > 2
                    ? rateCount / rateWatch.Elapsed.TotalSeconds * 60.0 : 0;
                progress?.Report((id, totalSaved, totalMissing, totalHardError, reqPerWindow, idsPerMin));
            }

            // Final flush
            File.WriteAllText(progressPath, endId.ToString());
            if (saveBatch.Count > 0)
                await _dogRepo.UpsertBatchAsync(saveBatch);

            Log($"=== Complete. {totalSaved} saved, {totalMissing} missing, " +
                $"{totalHardError} hard-errors. {DateTime.Now:yyyy-MM-dd HH:mm} ===");
            _log?.Dispose();
        }

        private async Task<(FetchStatus, DogRecord?)> FetchAdaptiveAsync(int id, CancellationToken ct)
        {
            try
            {
                using var resp = await _http.GetAsync(
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
                    return (FetchStatus.Success, _parser.ParseFromHtml(html));

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

        private void Log(string msg) =>
            _log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }
}
