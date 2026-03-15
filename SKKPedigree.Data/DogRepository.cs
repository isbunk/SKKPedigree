using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SKKPedigree.Data.Models;

namespace SKKPedigree.Data
{
    public class DogRepository
    {
        private readonly Database _db;

        public DogRepository(Database db) => _db = db;

        public async Task UpsertAsync(DogRecord dog)
        {
            dog.ScrapedAt = DateTime.UtcNow.ToString("o");

            // Upsert litter record if we have enough info
            if (dog.LitterId != null)
            {
                await _db.Connection.ExecuteAsync(
                    @"INSERT OR REPLACE INTO Litter (Id, FatherId, MotherId, BirthYear)
                      VALUES (@Id, @FatherId, @MotherId, @BirthYear)",
                    new
                    {
                        Id = dog.LitterId,
                        FatherId = dog.FatherId,
                        MotherId = dog.MotherId,
                        BirthYear = string.IsNullOrEmpty(dog.BirthDate) ? (int?)null
                            : int.Parse(dog.BirthDate.Split('-')[0])
                    });
            }

            await _db.Connection.ExecuteAsync(
                @"INSERT OR REPLACE INTO Dog (Id, HundId, Name, Breed, Sex, BirthDate, FatherId, MotherId, LitterId, ScrapedAt, RawHtml)
                  VALUES (@Id, @HundId, @Name, @Breed, @Sex, @BirthDate, @FatherId, @MotherId, @LitterId, @ScrapedAt, @RawHtml)",
                new
                {
                    dog.Id,
                    dog.HundId,
                    dog.Name,
                    dog.Breed,
                    dog.Sex,
                    dog.BirthDate,
                    dog.FatherId,
                    dog.MotherId,
                    dog.LitterId,
                    dog.ScrapedAt,
                    dog.RawHtml
                });

            // Delete old records before re-inserting
            await _db.Connection.ExecuteAsync("DELETE FROM HealthRecord WHERE DogId = @DogId", new { DogId = dog.Id });
            foreach (var hr in dog.HealthRecords)
            {
                hr.DogId = dog.Id;
                await _db.Connection.ExecuteAsync(
                    @"INSERT INTO HealthRecord (DogId, TestType, Result, TestDate)
                      VALUES (@DogId, @TestType, @Result, @TestDate)", hr);
            }

            await _db.Connection.ExecuteAsync("DELETE FROM CompetitionResult WHERE DogId = @DogId", new { DogId = dog.Id });
            foreach (var cr in dog.Results)
            {
                cr.DogId = dog.Id;
                await _db.Connection.ExecuteAsync(
                    @"INSERT INTO CompetitionResult (DogId, EventDate, EventType, Result)
                      VALUES (@DogId, @EventDate, @EventType, @Result)", cr);
            }
        }

        public async Task<DogRecord?> GetByIdAsync(string id)
        {
            var dog = await _db.Connection.QueryFirstOrDefaultAsync<DogRecord>(
                "SELECT * FROM Dog WHERE Id = @id", new { id });
            if (dog == null) return null;

            dog.HealthRecords = (await _db.Connection.QueryAsync<HealthRecord>(
                "SELECT * FROM HealthRecord WHERE DogId = @id", new { id })).AsList();
            dog.Results = (await _db.Connection.QueryAsync<CompetitionResult>(
                "SELECT * FROM CompetitionResult WHERE DogId = @id", new { id })).AsList();
            return dog;
        }

        public async Task<List<DogRecord>> GetChildrenAsync(string parentId)
        {
            return (await _db.Connection.QueryAsync<DogRecord>(
                "SELECT * FROM Dog WHERE FatherId = @parentId OR MotherId = @parentId",
                new { parentId })).AsList();
        }

        public async Task<List<DogRecord>> GetSiblingsAsync(string dogId)
        {
            var dog = await _db.Connection.QueryFirstOrDefaultAsync<DogRecord>(
                "SELECT * FROM Dog WHERE Id = @dogId", new { dogId });

            if (dog?.LitterId == null) return new List<DogRecord>();

            return (await _db.Connection.QueryAsync<DogRecord>(
                "SELECT * FROM Dog WHERE LitterId = @LitterId AND Id != @dogId",
                new { dog.LitterId, dogId })).AsList();
        }

        public async Task<List<DogRecord>> SearchByNameAsync(string name)
        {
            return (await _db.Connection.QueryAsync<DogRecord>(
                "SELECT * FROM Dog WHERE Name LIKE @pattern OR Id LIKE @pattern ORDER BY Name",
                new { pattern = $"%{name}%" })).AsList();
        }

        /// <summary>
        /// Returns all ancestor IDs up to maxGenerations generations deep.
        /// </summary>
        public async Task<List<string>> GetAncestorIdsAsync(string dogId, int maxGenerations = 5)
        {
            var visited = new HashSet<string>();
            var queue = new Queue<(string id, int gen)>();
            queue.Enqueue((dogId, 0));

            while (queue.Count > 0)
            {
                var (currentId, gen) = queue.Dequeue();
                if (gen >= maxGenerations) continue;

                var dog = await _db.Connection.QueryFirstOrDefaultAsync<DogRecord>(
                    "SELECT Id, FatherId, MotherId FROM Dog WHERE Id = @id", new { id = currentId });

                if (dog == null) continue;

                if (!string.IsNullOrEmpty(dog.FatherId) && visited.Add(dog.FatherId))
                    queue.Enqueue((dog.FatherId, gen + 1));

                if (!string.IsNullOrEmpty(dog.MotherId) && visited.Add(dog.MotherId))
                    queue.Enqueue((dog.MotherId, gen + 1));
            }

            return visited.ToList();
        }

        public async Task<string?> GetScrapedAtAsync(string dogId)
        {
            return await _db.Connection.QueryFirstOrDefaultAsync<string>(
                "SELECT ScrapedAt FROM Dog WHERE Id = @dogId", new { dogId });
        }

        /// <summary>
        /// Returns the total count of dogs in the database.
        /// </summary>
        public async Task<int> GetCountAsync()
        {
            return await _db.Connection.QueryFirstAsync<int>("SELECT COUNT(*) FROM Dog");
        }

        /// <summary>
        /// Returns all stored dog IDs — used on startup to pre-seed the visited set
        /// so the full scraper doesn't re-scrape dogs that are already in the DB.
        /// Only loads IDs scraped less than 30 days ago as "fresh enough to skip".
        /// </summary>
        public async Task<HashSet<string>> GetRecentlyScrapedIdsAsync(TimeSpan maxAge)
        {
            var cutoff = (DateTime.UtcNow - maxAge).ToString("o");
            var ids = await _db.Connection.QueryAsync<string>(
                "SELECT Id FROM Dog WHERE ScrapedAt >= @cutoff", new { cutoff });
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Upserts multiple dogs in a single transaction.
        /// </summary>
        public async Task UpsertBatchAsync(IEnumerable<DogRecord> dogs)
        {
            using var tx = _db.Connection.BeginTransaction();
            try
            {
                foreach (var d in dogs)
                    await UpsertAsync(d);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Returns all stored HundIds — used to skip already-scraped IDs on resume.
        /// </summary>
        public async Task<HashSet<int>> GetScrapedHundIdsAsync()
        {
            var ids = await _db.Connection.QueryAsync<int>(
                "SELECT HundId FROM Dog WHERE HundId IS NOT NULL");
            return new HashSet<int>(ids);
        }

        /// <summary>
        /// Deletes dogs with empty or null names — records saved without real data.
        /// Returns the number of rows deleted.
        /// </summary>
        public async Task<int> DeleteIncompleteAsync()
        {
            return await _db.Connection.ExecuteAsync(
                "DELETE FROM Dog WHERE Name IS NULL OR TRIM(Name) = ''");
        }
    }
}
