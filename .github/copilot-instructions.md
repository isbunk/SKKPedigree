# GitHub Copilot Workspace Instructions

## Overview
This workspace is a C# .NET 6.0 solution for scraping, storing, and analyzing Swedish Kennel Club (SKK) dog pedigree data. It consists of four main projects in a layered architecture:

- **SKKPedigree.Data**: SQLite database access and migrations, Dapper-based repositories, genealogical analysis.
- **SKKPedigree.Scraper**: Playwright-based browser automation, HTML parsing, adaptive bulk scraping, and job management.
- **SKKPedigree.App**: WPF desktop app (MVVM), user interface for search, pedigree, litter, relation, and bulk scrape views.
- **SKKPedigree.Console**: Console entry point for bulk scraping jobs.

## Build & Run
- **Build all**: `dotnet build SKKPedigree.sln`
- **Run WPF app**: `dotnet run --project SKKPedigree.App`
- **Run console scraper**: `dotnet run --project SKKPedigree.Console`
- **Release build**: `dotnet build SKKPedigree.sln -c Release`
- **No test projects** are present.

## Key Conventions & Patterns
- **Database**: Data is stored in `%AppData%/SKKPedigree/pedigree.db` (configurable).
- **Dog identity**: `HundId` (internal integer) vs. registration number (public string, e.g., `S12345/2020`). Both are stored.
- **No raw HTML**: All data is parsed at scrape time; re-scraping requires a new request.
- **Rate limiting**: Scraper enforces ethical scraping with a user disclaimer and adaptive rate limiting (<3% error target).
- **Resume support**: Bulk scrapes write/read progress files to support pause/resume.
- **MVVM**: WPF app uses `ViewModelBase` (INotifyPropertyChanged) and `RelayCommand` (ICommand).
- **Dependency Injection**: All services, repositories, and view models are registered via DI in `App.xaml.cs`.
- **Crash logging**: Unhandled exceptions are logged to `%AppData%/SKKPedigree/crash.log`.

## Project Structure
- **SKKPedigree.Data**: `Database.cs`, `DogRepository.cs`, `RelationRepository.cs`
- **SKKPedigree.Scraper**: `SkkSession.cs`, `DogScraper.cs`, `IdRangeScrapeJob.cs`, `FullScrapeJob.cs`
- **SKKPedigree.App**: `App.xaml.cs`, `AppSettings.cs`, `MainWindow.xaml.cs`, `ViewModels/`, `Views/`
- **SKKPedigree.Console**: `Program.cs`

## Potential Pitfalls
- **No tests**: Manual validation required.
- **Playwright**: Requires Chromium; ensure Playwright dependencies are installed.
- **Bulk scraping**: May be rate-limited or blocked by SKK; always respect ethical scraping guidelines.
- **Data schema**: Migrations only add columns; destructive changes require manual intervention.

## Example Prompts
- "Scrape all dogs with registration prefix 'SE' and store in the database."
- "Show the pedigree tree for a given registration number."
- "Resume a bulk scrape from the last checkpoint."
- "Calculate the inbreeding coefficient for two dogs."

## Next Steps / Customizations
- **/create-skill agent-customization**: Add custom agent instructions for Playwright scraping or database migrations.
- **/create-instruction frontend**: Add UI-specific guidance for WPF/MVVM patterns.
- **/create-instruction scraper**: Add scraping-specific error handling and retry logic.

---
This file is auto-generated for GitHub Copilot and compatible agents. For more details, see `CLAUDE.md`.
