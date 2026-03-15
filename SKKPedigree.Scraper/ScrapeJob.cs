using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SKKPedigree.Data;
using SKKPedigree.Data.Models;

namespace SKKPedigree.Scraper
{
    public class ScrapeProgress
    {
        public int ScrapedCount { get; set; }
        public int QueuedCount { get; set; }
        public string CurrentDogName { get; set; } = "";
        public string Status { get; set; } = "Idle"; // "Scraping", "Saving", "Done", "Error"
    }

    /// <summary>
    /// Orchestrates recursive scraping of a dog's ancestors up to a configurable depth.
    /// Skips dogs already in the DB whose ScrapedAt is less than 7 days old.
    /// </summary>
    public class ScrapeJob
    {
        private readonly DogScraper _scraper;
        private readonly DogRepository _dogRepo;
        private readonly int _maxGenerations;
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromDays(7);

        public ScrapeJob(DogScraper scraper, DogRepository dogRepo, int maxGenerations = 4)
        {
            _scraper = scraper;
            _dogRepo = dogRepo;
            _maxGenerations = maxGenerations;
        }

        /// <summary>
        /// Starts a recursive scrape beginning with the given registration number.
        /// </summary>
        public async Task RunAsync(
            string startRegNumber,
            IProgress<ScrapeProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var progress_state = new ScrapeProgress();
            var queue = new Queue<(string regOrUrl, int depth, bool isUrl)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            queue.Enqueue((startRegNumber, 0, false));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (regOrUrl, depth, isUrl) = queue.Dequeue();
                if (!visited.Add(regOrUrl)) continue;

                progress_state.QueuedCount = queue.Count;
                progress_state.Status = "Scraping";

                // Check if we have a fresh copy in the DB
                if (!isUrl)
                {
                    var existing = await _dogRepo.GetByIdAsync(regOrUrl);
                    if (existing != null && !IsStale(existing.ScrapedAt))
                    {
                        // Still enqueue parents for recursive descent
                        if (depth < _maxGenerations)
                            EnqueueParents(existing, depth, queue, visited);
                        continue;
                    }
                }

                DogRecord dog;
                try
                {
                    dog = isUrl
                        ? await _scraper.ScrapeByUrlAsync(regOrUrl)
                        : await _scraper.ScrapeByRegNumberAsync(regOrUrl);
                }
                catch (Exception ex)
                {
                    progress_state.Status = $"Error: {ex.Message}";
                    progress?.Report(Clone(progress_state));
                    continue;
                }

                progress_state.CurrentDogName = dog.Name;
                progress_state.Status = "Saving";
                progress?.Report(Clone(progress_state));

                await _dogRepo.UpsertAsync(dog);
                progress_state.ScrapedCount++;

                // Recursively scrape parents up to maxGenerations
                if (depth < _maxGenerations)
                    EnqueueParents(dog, depth, queue, visited);

                progress_state.Status = "Scraping";
                progress?.Report(Clone(progress_state));
            }

            progress_state.Status = "Done";
            progress?.Report(Clone(progress_state));
        }

        private static void EnqueueParents(
            DogRecord dog, int depth,
            Queue<(string, int, bool)> queue,
            HashSet<string> visited)
        {
            if (!string.IsNullOrEmpty(dog.FatherUrl) && visited.Add(dog.FatherUrl))
                queue.Enqueue((dog.FatherUrl, depth + 1, true));
            else if (!string.IsNullOrEmpty(dog.FatherId) && visited.Add(dog.FatherId))
                queue.Enqueue((dog.FatherId, depth + 1, false));

            if (!string.IsNullOrEmpty(dog.MotherUrl) && visited.Add(dog.MotherUrl))
                queue.Enqueue((dog.MotherUrl, depth + 1, true));
            else if (!string.IsNullOrEmpty(dog.MotherId) && visited.Add(dog.MotherId))
                queue.Enqueue((dog.MotherId, depth + 1, false));
        }

        private bool IsStale(string scrapedAt)
        {
            if (!DateTime.TryParse(scrapedAt, out var dt)) return true;
            return DateTime.UtcNow - dt > _cacheExpiry;
        }

        private static ScrapeProgress Clone(ScrapeProgress p) => new()
        {
            ScrapedCount = p.ScrapedCount,
            QueuedCount = p.QueuedCount,
            CurrentDogName = p.CurrentDogName,
            Status = p.Status
        };
    }
}
