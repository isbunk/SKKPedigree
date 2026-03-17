using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SKKPedigree.Data
{
    public class Database : IDisposable
    {
        private readonly string _dbPath;
        private SqliteConnection? _connection;

        public Database(string? dbPath = null)
        {
            _dbPath = dbPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SKKPedigree", "pedigree.db");
        }

        public SqliteConnection Connection
        {
            get
            {
                if (_connection == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
                    _connection = new SqliteConnection($"Data Source={_dbPath}");
                    _connection.Open();
                    // 200 MB page cache — critical for write performance on large DBs.
                    // Default is -2000 (8 MB), giving ~0% cache hit rate at 3+ GB.
                    using var pragma = _connection.CreateCommand();
                    pragma.CommandText = "PRAGMA cache_size = -204800; PRAGMA synchronous = NORMAL;";
                    pragma.ExecuteNonQuery();
                }
                return _connection;
            }
        }

        public async Task RunMigrationsAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();
            await using (var pc = new SqliteCommand("PRAGMA cache_size = -204800; PRAGMA synchronous = NORMAL;", conn))
                await pc.ExecuteNonQueryAsync();

            var sql = @"
CREATE TABLE IF NOT EXISTS Dog (
    Id          TEXT PRIMARY KEY,
    HundId      INTEGER,
    Name        TEXT NOT NULL,
    Breed       TEXT,
    Sex         TEXT,
    BirthDate   TEXT,
    FatherId    TEXT,
    MotherId    TEXT,
    LitterId    TEXT,
    ScrapedAt   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Litter (
    Id          TEXT PRIMARY KEY,
    FatherId    TEXT,
    MotherId    TEXT,
    BirthYear   INTEGER
);

CREATE TABLE IF NOT EXISTS HealthRecord (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    DogId       TEXT NOT NULL,
    TestType    TEXT,
    Result      TEXT,
    TestDate    TEXT,
    FOREIGN KEY (DogId) REFERENCES Dog(Id)
);

CREATE TABLE IF NOT EXISTS CompetitionResult (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    DogId       TEXT NOT NULL,
    EventDate   TEXT,
    EventType   TEXT,
    Result      TEXT,
    FOREIGN KEY (DogId) REFERENCES Dog(Id)
);

CREATE TABLE IF NOT EXISTS Title (
    Id      INTEGER PRIMARY KEY AUTOINCREMENT,
    DogId   TEXT NOT NULL,
    Title   TEXT NOT NULL,
    FOREIGN KEY (DogId) REFERENCES Dog(Id)
);

CREATE INDEX IF NOT EXISTS idx_dog_father ON Dog(FatherId);
CREATE INDEX IF NOT EXISTS idx_dog_mother ON Dog(MotherId);
CREATE INDEX IF NOT EXISTS idx_dog_litter ON Dog(LitterId);
CREATE INDEX IF NOT EXISTS idx_title_dog  ON Title(DogId);
";
            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();

            // Safe migrations: add columns to existing databases
            var dogColumns = new[] {
                "ALTER TABLE Dog ADD COLUMN HundId INTEGER",
                "ALTER TABLE Dog ADD COLUMN KennelName TEXT",
                "ALTER TABLE Dog ADD COLUMN BreederName TEXT",
                "ALTER TABLE Dog ADD COLUMN BreederCity TEXT",
                "ALTER TABLE Dog ADD COLUMN IdNumber TEXT",
                "ALTER TABLE Dog ADD COLUMN Color TEXT",
                "ALTER TABLE Dog ADD COLUMN CoatType TEXT",
                "ALTER TABLE Dog ADD COLUMN Size TEXT",
                "ALTER TABLE Dog ADD COLUMN ChipNumber TEXT",
                "ALTER TABLE Dog ADD COLUMN IsDeceased INTEGER NOT NULL DEFAULT 0",
            };
            var healthColumns = new[] {
                "ALTER TABLE HealthRecord ADD COLUMN Grade TEXT",
                "ALTER TABLE HealthRecord ADD COLUMN VetClinic TEXT",
            };
            var competitionColumns = new[] {
                "ALTER TABLE CompetitionResult ADD COLUMN Location TEXT",
                "ALTER TABLE CompetitionResult ADD COLUMN Organiser TEXT",
            };

            foreach (var ddl in dogColumns.Concat(healthColumns).Concat(competitionColumns))
            {
                try
                {
                    await using var alter = new SqliteCommand(ddl, conn);
                    await alter.ExecuteNonQueryAsync();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column")) { }
            }

            // Create unique index on HundId after ensuring the column exists
            await using var idxCmd = new SqliteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_dog_hundid ON Dog(HundId)", conn);
            await idxCmd.ExecuteNonQueryAsync();

            // Drop RawHtml — it's 50-100KB per dog and no longer needed (all data now parsed at scrape time)
            try
            {
                await using var dropCol = new SqliteCommand("ALTER TABLE Dog DROP COLUMN RawHtml", conn);
                await dropCol.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { /* column already gone or SQLite version too old */ }
        }

        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}
