using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HtmlAgilityPack;
using SKKPedigree.Data.Models;

namespace SKKPedigree.Scraper
{
    /// <summary>
    /// Fetches and parses dog pages from hundar.skk.se/hunddata/ using SkkSession.
    /// Uses HtmlAgilityPack for HTML parsing.
    /// </summary>
    public class DogScraper
    {
        private readonly SkkSession _session;

        public DogScraper(SkkSession session) => _session = session;

        public async Task<DogRecord> ScrapeByRegNumberAsync(string regNumber)
        {
            var html = await _session.GetDogPageHtmlAsync(regNumber);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Check for direct dog page vs search results list
            if (IsSearchResultPage(doc))
            {
                var firstLink = GetFirstSearchResultLink(doc);
                if (firstLink == null)
                    throw new InvalidOperationException($"No search results found for '{regNumber}'.");
                html = await _session.GetDogDetailHtmlAsync(firstLink);
                doc.LoadHtml(html);
            }

            return ParseHtml(html, doc);
        }

        public async Task<DogRecord> ScrapeByUrlAsync(string relativeUrl)
        {
            var html = await _session.GetDogDetailHtmlAsync(relativeUrl);
            return ParseHtml(html, null);
        }

        /// <summary>Parse a dog page from raw HTML without a browser session.</summary>
        public DogRecord ParseFromHtml(string html) => ParseHtml(html, null);

        private bool IsSearchResultPage(HtmlDocument doc)
        {
            // jsGrid search result has a div with id containing 'gridHundar'
            return doc.DocumentNode.SelectSingleNode(
                "//*[contains(@id,'gridHundar')]") != null;
        }

        private string? GetFirstSearchResultLink(HtmlDocument doc)
        {
            var anchor = doc.DocumentNode.SelectSingleNode(
                "//a[contains(@href,'Hund.aspx') and contains(@href,'hundid=')]");
            return anchor?.GetAttributeValue("href", null);
        }

        private DogRecord ParseHtml(string html, HtmlDocument? existingDoc)
        {
            var doc = existingDoc ?? new HtmlDocument();
            if (existingDoc == null) doc.LoadHtml(html);

            var record = new DogRecord
            {
                ScrapedAt = DateTime.UtcNow.ToString("o")
            };

            // Registration number — id="bodyContent_lblRegnr"
            record.Id = ExtractLabelText(doc, "bodyContent_lblRegnr", "lblRegnr") ?? "";

            // Name — id="bodyContent_lblHundnamn"
            record.Name = ExtractLabelText(doc, "bodyContent_lblHundnamn", "lblHundnamn", "lblNamn") ?? "Unknown";

            // Breed — id="bodyContent_lblRastext"
            record.Breed = ExtractLabelText(doc, "bodyContent_lblRastext", "lblRastext", "lblRas");

            // Sex — id="bodyContent_lblKon"  ("T"=Tik/female, "H"=Hane/male)
            var sexText = ExtractLabelText(doc, "bodyContent_lblKon", "lblKon");
            record.Sex = NormaliseSex(sexText);

            // Birth date — id="bodyContent_lblFodelsedatum"
            record.BirthDate = ExtractLabelText(doc, "bodyContent_lblFodelsedatum", "lblFodd", "lblFodelsedatum");

            // Father — reg# is link text of lnkFarregnr, name is lblFarnamn
            record.FatherId   = ExtractLabelText(doc, "bodyContent_lnkFarregnr", "lnkFarregnr")?.Trim();
            record.FatherName = ExtractLabelText(doc, "bodyContent_lblFarnamn",  "lblFarnamn")?.Trim();
            record.FatherUrl  = null; // parents discovered via seed search, no direct URL

            // Mother
            record.MotherId   = ExtractLabelText(doc, "bodyContent_lnkMorregnr", "lnkMorregnr")?.Trim();
            record.MotherName = ExtractLabelText(doc, "bodyContent_lblMornamn",  "lblMornamn")?.Trim();
            record.MotherUrl  = null;

            // Physical attributes (all in initial HTML, no PostBack needed)
            record.IdNumber    = Nullify(ExtractLabelText(doc, "bodyContent_lblIDnummer", "lblIDnummer"));
            record.Color      = Nullify(ExtractLabelText(doc, "bodyContent_lblFarg",     "lblFarg"));
            record.CoatType   = Nullify(ExtractLabelText(doc, "bodyContent_lblHarlag",   "lblHarlag"));
            record.Size       = Nullify(ExtractLabelText(doc, "bodyContent_lblStorlek",  "lblStorlek"));
            record.ChipNumber = Nullify(ExtractLabelText(doc, "bodyContent_lblChipnr",   "lblChipnr"));
            var deceasedText = ExtractLabelText(doc, "bodyContent_lblAvliden",   "lblAvliden");
            record.IsDeceased = !string.IsNullOrWhiteSpace(deceasedText) &&
                                deceasedText.Contains("avliden", StringComparison.OrdinalIgnoreCase);

            // Build litter ID
            if (!string.IsNullOrEmpty(record.FatherId) && !string.IsNullOrEmpty(record.MotherId))
            {
                var year = string.IsNullOrEmpty(record.BirthDate) ? "0000"
                    : record.BirthDate.Length >= 4 ? record.BirthDate[..4] : "0000";
                record.LitterId = $"{record.FatherId}_{record.MotherId}_{year}";
            }

            // Siblings
            record.SiblingUrls = ParseSiblingLinks(doc);

            // Health records
            record.HealthRecords = ParseHealthRecords(doc);

            // Competition results
            record.Results = ParseCompetitionResults(doc);

            return record;
        }

        /// <summary>
        /// Tries several label identifiers (id, partial id, label text) to extract a value.
        /// </summary>
        private static string? ExtractLabelText(HtmlDocument doc, params string[] hints)
        {
            foreach (var hint in hints)
            {
                // Try by element id
                var node = doc.GetElementbyId(hint);
                if (node != null)
                    return HtmlEntity.DeEntitize(node.InnerText.Trim());

                // Try by partial id (span, label)
                node = doc.DocumentNode.SelectSingleNode(
                    $"//span[contains(@id,'{hint}')] | //label[contains(@id,'{hint}')]");
                if (node != null)
                    return HtmlEntity.DeEntitize(node.InnerText.Trim());

                // Try by following sibling of label containing hint text
                node = doc.DocumentNode.SelectSingleNode(
                    $"//td[contains(normalize-space(text()),'{hint}')]/following-sibling::td");
                if (node != null)
                    return HtmlEntity.DeEntitize(node.InnerText.Trim());
            }
            return null;
        }

        // FindParentNode removed — parent data extracted directly by element ID in ParseHtml.

        private static List<string> ParseSiblingLinks(HtmlDocument doc)
        {
            // Siblings are loaded via PostBack button on the live site — not in the initial HTML.
            // Return empty; the full scrape discovers all dogs via seed searches anyway.
            return new List<string>();
        }

        private static List<HealthRecord> ParseHealthRecords(HtmlDocument doc)
        {
            // Health records are loaded via PostBack — not present in the initial GET.
            // Use ParseHealthFromPostBack() for PostBack responses.
            return new List<HealthRecord>();
        }

        private static List<CompetitionResult> ParseCompetitionResults(HtmlDocument doc)
        {
            // Competition results are loaded via PostBack — not present in the initial GET.
            // Use ParseCompetitionFromPostBack() for PostBack responses.
            return new List<CompetitionResult>();
        }

        /// <summary>
        /// Parses competition results from the PostBack response after clicking btnTavling.
        /// The outer table is bodyContent_ctl00_tblTavling; inner rows use CSS classes
        /// "tabellrubrik" (event header) and "tabelltext" (result detail lines).
        /// Each event header becomes one CompetitionResult; detail lines are concatenated into Result.
        /// </summary>
        public static List<CompetitionResult> ParseCompetitionFromPostBack(string html)
        {
            var results = new List<CompetitionResult>();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var innerTable = doc.GetElementbyId("bodyContent_ctl00_tblTavling");
            if (innerTable == null) return results;

            // Find all inner tables (one per event block)
            var eventTables = innerTable.SelectNodes(".//table");
            if (eventTables == null) return results;

            foreach (var tbl in eventTables)
            {
                var rows = tbl.SelectNodes(".//tr");
                if (rows == null) continue;

                CompetitionResult? current = null;
                var details = new System.Text.StringBuilder();

                foreach (var row in rows)
                {
                    var cls = row.GetAttributeValue("class", "");
                    var cells = row.SelectNodes(".//td");
                    if (cells == null) continue;

                    if (cls.Contains("tabellrubrik"))
                    {
                        // Save previous event
                        if (current != null)
                        {
                            current.Result = details.ToString().Trim();
                            results.Add(current);
                        }
                        // New event: col0=date/id, col1=location, col2=event type, col3=organiser
                        current = new CompetitionResult
                        {
                            EventDate  = cells.Count > 0 ? HtmlEntity.DeEntitize(cells[0].InnerText.Trim()) : null,
                            Location   = cells.Count > 1 ? HtmlEntity.DeEntitize(cells[1].InnerText.Trim()) : null,
                            EventType  = cells.Count > 2 ? HtmlEntity.DeEntitize(cells[2].InnerText.Trim()) : null,
                            Organiser  = cells.Count > 3 ? HtmlEntity.DeEntitize(cells[3].InnerText.Trim()) : null,
                        };
                        details.Clear();
                    }
                    else if (cls.Contains("tabelltext") && current != null)
                    {
                        // Detail line: col1=label, col2=value
                        var label = cells.Count > 1 ? HtmlEntity.DeEntitize(cells[1].InnerText.Trim()) : "";
                        var value = cells.Count > 2 ? HtmlEntity.DeEntitize(cells[2].InnerText.Trim()) : "";
                        var combined = $"{label}{(string.IsNullOrWhiteSpace(value) ? "" : " " + value)}".Trim();
                        if (!string.IsNullOrWhiteSpace(combined))
                            details.Append($" | {combined}");
                    }
                }

                if (current != null)
                {
                    current.Result = details.ToString().Trim();
                    results.Add(current);
                }
            }
            return results;
        }

        /// <summary>
        /// Parses health/vet records from the PostBack response after clicking btnVeterinar.
        /// The response places content in the #ankare div under a table-responsive wrapper —
        /// there is no separate named divVeterinar. We locate the table-responsive div
        /// that follows the "Veterinärdata" h4 heading.
        /// </summary>
        public static List<HealthRecord> ParseHealthFromPostBack(string html)
        {
            var records = new List<HealthRecord>();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var ankare = doc.GetElementbyId("ankare");
            if (ankare == null) return records;

            var tables = ankare.SelectNodes(".//table");
            if (tables == null) return records;

            // Target the inner grid: bodyContent_ctl00_gridVeterinar
            // Columns: col0=ExaminationDate, col1=Vet/Clinic, col2=Result
            var grid = doc.GetElementbyId("bodyContent_ctl00_gridVeterinar");
            var searchIn = grid != null ? new[] { grid } : tables.Cast<HtmlNode>().ToArray();

            foreach (var table in searchIn)
            {
                var rows = table.SelectNodes(".//tr[not(.//th)]"); // skip header row
                if (rows == null) continue;
                foreach (var row in rows)
                {
                    var cells = row.SelectNodes(".//td");
                    if (cells == null || cells.Count < 2) continue;
                    var testDate  = HtmlEntity.DeEntitize(cells[0].InnerText.Trim());
                    var vetClinic = cells.Count > 1 ? HtmlEntity.DeEntitize(cells[1].InnerText.Trim()) : null;
                    var result    = cells.Count > 2 ? HtmlEntity.DeEntitize(cells[2].InnerText.Trim()) : null;
                    if (string.IsNullOrWhiteSpace(testDate) && string.IsNullOrWhiteSpace(result)) continue;
                    // TestType = first word (e.g. "HD ua" → "HD"), Grade = remainder (e.g. "A/A", "ua")
                    var parts    = result?.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var testType = parts?.Length >= 1 ? parts[0].ToUpperInvariant() : null;
                    var grade    = parts?.Length >= 2 ? parts[1].Trim().ToUpperInvariant() : null;
                    records.Add(new HealthRecord
                    {
                        TestType  = testType,
                        Grade     = grade,
                        TestDate  = string.IsNullOrWhiteSpace(testDate) || testDate == "0" ? null : testDate,
                        VetClinic = string.IsNullOrWhiteSpace(vetClinic) ? null : vetClinic,
                        Result    = result,
                    });
                }
            }
            return records;
        }

        /// <summary>
        /// Parses championship titles from the PostBack response after clicking btnTitlar.
        /// The response contains a single-column table with one title per row (e.g. "SE UCH", "NORD UCH").
        /// </summary>
        public static List<string> ParseTitlesFromPostBack(string html)
        {
            var titles = new List<string>();
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var ankare = doc.GetElementbyId("ankare");
            if (ankare == null) return titles;

            foreach (var td in ankare.SelectNodes(".//td") ?? Enumerable.Empty<HtmlNode>())
            {
                var text = HtmlEntity.DeEntitize(td.InnerText.Trim());
                if (!string.IsNullOrWhiteSpace(text) && text != "Text")
                    titles.Add(text);
            }
            return titles;
        }

        /// <summary>
        /// Parses breeder info from the PostBack response after clicking btnUppfodare.
        /// Returns (KennelName, BreederName, BreederCity). Any field may be null if not present.
        /// The page has two sections:
        ///   - "Aktuella kenneluppgifter": current kennel name
        ///   - "Uppfödare vid registreringstillfället": breeder name + city at registration time
        /// </summary>
        public static (string? KennelName, string? BreederName, string? BreederCity) ParseBreederFromPostBack(string html)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            string? kennelName  = null;
            string? breederName = null;
            string? breederCity = null;

            // Kennel name: first <td> after a <td> containing "Kennel:"
            var kennelLabel = doc.DocumentNode.SelectSingleNode(
                "//td[normalize-space(text())='Kennel:']/following-sibling::td[1]");
            if (kennelLabel != null)
            {
                var raw = HtmlEntity.DeEntitize(kennelLabel.InnerText.Trim());
                if (!string.IsNullOrWhiteSpace(raw) && raw != "Namn saknas")
                    kennelName = raw;
            }

            // Breeder at registration: rows with "Namn:" and "Ort:" labels
            var nameCell = doc.DocumentNode.SelectSingleNode(
                "//td[normalize-space(.)='Namn:\u00a0\u00a0']/following-sibling::td[1] | " +
                "//td[starts-with(normalize-space(text()),'Namn')]/following-sibling::td[1]");
            if (nameCell != null)
            {
                var raw = HtmlEntity.DeEntitize(nameCell.InnerText.Trim());
                if (!string.IsNullOrWhiteSpace(raw)) breederName = raw;
            }

            var cityCell = doc.DocumentNode.SelectSingleNode(
                "//td[normalize-space(.)='Ort:\u00a0\u00a0']/following-sibling::td[1] | " +
                "//td[starts-with(normalize-space(text()),'Ort')]/following-sibling::td[1]");
            if (cityCell != null)
            {
                var raw = HtmlEntity.DeEntitize(cityCell.InnerText.Trim());
                if (!string.IsNullOrWhiteSpace(raw)) breederCity = raw;
            }

            return (kennelName, breederName, breederCity);
        }

        /// <summary>
        /// Extracts a registration number from urls like HundVisa.aspx?id=XXXXX
        /// (the actual reg number is on the detail page, so we return the SKK internal id as a placeholder).
        /// </summary>
        private static string? ExtractRegFromUrl(string? url)
        {
            if (url == null) return null;
            var idx = url.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var raw = url[(idx + 3)..];
            var end = raw.IndexOfAny(new[] { '&', '?' });
            return end >= 0 ? "skk_" + raw[..end] : "skk_" + raw;
        }

        private static string? Nullify(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string? NormaliseSex(string? raw)
        {
            if (raw == null) return null;
            var lower = raw.Trim().ToLowerInvariant();
            if (lower.StartsWith("h") || lower == "male" || lower == "m") return "M";  // Hane
            if (lower.StartsWith("t") || lower == "female" || lower == "f") return "F"; // Tik
            return raw.Trim();
        }
    }
}
