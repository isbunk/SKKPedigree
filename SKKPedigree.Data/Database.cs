using System;
using System.IO;
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
                }
                return _connection;
            }
        }

        public async Task RunMigrationsAsync()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

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
    ScrapedAt   TEXT NOT NULL,
    RawHtml     TEXT
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

CREATE INDEX IF NOT EXISTS idx_dog_father ON Dog(FatherId);
CREATE INDEX IF NOT EXISTS idx_dog_mother ON Dog(MotherId);
CREATE INDEX IF NOT EXISTS idx_dog_litter ON Dog(LitterId);
";
            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();

            // Safe migration: add HundId column to existing databases that predate it
            try
            {
                await using var alter = new SqliteCommand(
                    "ALTER TABLE Dog ADD COLUMN HundId INTEGER", conn);
                await alter.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
            {
                // Column already exists — nothing to do
            }

            // Create unique index on HundId after ensuring the column exists
            await using var idxCmd = new SqliteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_dog_hundid ON Dog(HundId)", conn);
            await idxCmd.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}
