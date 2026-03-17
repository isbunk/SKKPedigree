# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Run

```bash
# Build entire solution
dotnet build SKKPedigree.sln

# Run the WPF desktop app
dotnet run --project SKKPedigree.App

# Run the console scraper
dotnet run --project SKKPedigree.Console

# Build in release mode
dotnet build SKKPedigree.sln -c Release
```

No test projects exist in this solution.

## Architecture

This is a **C# .NET 6.0** solution with 4 projects in a layered architecture:

```
SKKPedigree.Data  ←  SKKPedigree.Scraper  ←  SKKPedigree.App
                                          ←  SKKPedigree.Console
```

### SKKPedigree.Data
- **`Database.cs`** — SQLite connection (Microsoft.Data.Sqlite), lazy-initialized, with a migration system that safely adds columns to existing tables.
- **`DogRepository.cs`** — Core CRUD via Dapper. Key methods: `UpsertAsync`, `GetAncestorIdsAsync` (BFS traversal), `GetScrapedHundIdsAsync` (used for resume logic in bulk scrapes).
- **`RelationRepository.cs`** — Genealogical analysis: `FindCommonAncestorsAsync` and `CalculateInbreedingCoefficientAsync` (Wright's formula with memoization).
- Data stored in `%AppData%/SKKPedigree/pedigree.db` by default (configurable in `AppSettings`).

### SKKPedigree.Scraper
- **`SkkSession.cs`** — Playwright-based Chromium session. Handles ASP.NET WebForms state (`__VIEWSTATE`, `__doPostBack`) and a JavaScript fetch to the HundData API. Default 1500ms request delay between calls.
- **`DogScraper.cs`** — HtmlAgilityPack HTML parser. Extracts dog data from ASP.NET label elements by `id` attribute. Distinguishes search result pages from detail pages.
- **`IdRangeScrapeJob.cs`** — Adaptive rate-limited bulk scraper using plain HttpClient (not Playwright). Targets `hundar.skk.se/hunddata/` directly. Rate starts at 1 req/2s, scales up/down based on error rate (target: <3% errors). Max HundId: 3,862,000. Supports pause/resume via a progress file.
- **`FullScrapeJob.cs`** — Alphabet-seeded BFS crawl (Swedish letters a–z, å, ä, ö). Follows parent/sibling/child links, saves in batches, 30-day skip cache.

### SKKPedigree.App (WPF)
- **`App.xaml.cs`** — DI container setup, database migration on startup, one-time disclaimer flow, crash logging to `%AppData%/SKKPedigree/crash.log`.
- **`AppSettings.cs`** — Persisted JSON config in `%AppData%/SKKPedigree/settings.json`.
- **`MainWindow.xaml.cs`** — 5 tabs, each with an injected ViewModel. Search tab propagates selected dog to Pedigree tab.
- ViewModels follow MVVM with `ViewModelBase` (INotifyPropertyChanged) and `RelayCommand` (ICommand).
- Views: Search, Pedigree (family tree), Litter (siblings), Relation (inbreeding/common ancestors), FullScrape (bulk job progress).

### SKKPedigree.Console
- **`Program.cs`** — Thin entry point calling `IdRangeScrapeJob.RunAdaptiveAsync()`. Pre-cleans incomplete records and pre-loads already-scraped IDs before starting. Handles Ctrl+C for graceful pause.

## Key Conventions

- **HundId vs. registration number**: `HundId` is an integer URL parameter used internally on the SKK site; the registration number (e.g., `S12345/2020`) is the public identifier. Both are stored on `DogRecord`.
- **No raw HTML storage**: `RawHtml` was removed — all data is parsed at scrape time. Re-scraping requires a new network request.
- **Rate limiting ethics**: The scraper includes a disclaimer requiring user acknowledgment of copyright/personal-use restrictions before first run.
- **Resume pattern**: `IdRangeScrapeJob` writes a progress file after each batch and reads it on startup to skip completed ranges.
