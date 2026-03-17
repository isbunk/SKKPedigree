-- Merge all shard DBs (and their archives) into pedigree.db
--
-- Usage:
--   sqlite3 "%APPDATA%\SKKPedigree\pedigree.db" < MergeShards.sql
--
-- Dog / Litter use INSERT OR REPLACE (unique by registration number / litter Id).
-- Child tables (HealthRecord, CompetitionResult, Title) use INSERT + new auto-increment
-- IDs to avoid cross-shard ID collisions.

PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA cache_size   = -204800;

-- ── Helper: attach, merge, detach ────────────────────────────────────────────

-- Replace the paths below with the actual shard DB files you want to merge.
-- Run this block once per shard (archive + working pair).

-- ── Shard 2 archive ──────────────────────────────────────────────────────────
ATTACH DATABASE 'shard2_archive.db' AS src;

INSERT OR REPLACE INTO main.Dog    SELECT * FROM src.Dog;
INSERT OR REPLACE INTO main.Litter SELECT * FROM src.Litter;

-- Child tables: drop the source Id and let SQLite assign new ones.
INSERT INTO main.HealthRecord     (DogId, TestType, Grade, Result, TestDate, VetClinic)
    SELECT DogId, TestType, Grade, Result, TestDate, VetClinic FROM src.HealthRecord;

INSERT INTO main.CompetitionResult (DogId, EventDate, Location, EventType, Organiser, Result)
    SELECT DogId, EventDate, Location, EventType, Organiser, Result FROM src.CompetitionResult;

INSERT INTO main.Title (DogId, Title)
    SELECT DogId, Title FROM src.Title;

DETACH DATABASE src;

-- ── Shard 2 working (remaining dogs not yet archived) ────────────────────────
ATTACH DATABASE 'shard2.db' AS src;

INSERT OR REPLACE INTO main.Dog    SELECT * FROM src.Dog;
INSERT OR REPLACE INTO main.Litter SELECT * FROM src.Litter;

INSERT INTO main.HealthRecord     (DogId, TestType, Grade, Result, TestDate, VetClinic)
    SELECT DogId, TestType, Grade, Result, TestDate, VetClinic FROM src.HealthRecord;

INSERT INTO main.CompetitionResult (DogId, EventDate, Location, EventType, Organiser, Result)
    SELECT DogId, EventDate, Location, EventType, Organiser, Result FROM src.CompetitionResult;

INSERT INTO main.Title (DogId, Title)
    SELECT DogId, Title FROM src.Title;

DETACH DATABASE src;

-- ── Add more shards here ──────────────────────────────────────────────────────
-- Copy and paste the block above, replacing 'shard2' with 'shard3', etc.

SELECT 'Merge complete. Total dogs: ' || COUNT(*) FROM main.Dog;
