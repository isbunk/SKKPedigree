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
                RawHtml = html,
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
            var records = new List<HealthRecord>();
            // Health records are typically in a table whose id contains "Halsa" or "HD"
            var table = doc.DocumentNode.SelectSingleNode(
                "//table[contains(@id,'Halsa') or contains(@id,'Health') or contains(@id,'HD')]");
            if (table == null) return records;

            var rows = table.SelectNodes(".//tr[position()>1]"); // skip header
            if (rows == null) return records;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes(".//td");
                if (cells == null || cells.Count < 2) continue;
                records.Add(new HealthRecord
                {
                    TestType = cells.Count > 0 ? HtmlEntity.DeEntitize(cells[0].InnerText.Trim()) : null,
                    TestDate = cells.Count > 1 ? HtmlEntity.DeEntitize(cells[1].InnerText.Trim()) : null,
                    Result = cells.Count > 2 ? HtmlEntity.DeEntitize(cells[2].InnerText.Trim()) : null
                });
            }
            return records;
        }

        private static List<CompetitionResult> ParseCompetitionResults(HtmlDocument doc)
        {
            var results = new List<CompetitionResult>();
            // Competition results table
            var table = doc.DocumentNode.SelectSingleNode(
                "//table[contains(@id,'Tavl') or contains(@id,'Merits') or contains(@id,'Competition')]");
            if (table == null) return results;

            var rows = table.SelectNodes(".//tr[position()>1]");
            if (rows == null) return results;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes(".//td");
                if (cells == null || cells.Count < 2) continue;
                results.Add(new CompetitionResult
                {
                    EventDate = cells.Count > 0 ? HtmlEntity.DeEntitize(cells[0].InnerText.Trim()) : null,
                    EventType = cells.Count > 1 ? HtmlEntity.DeEntitize(cells[1].InnerText.Trim()) : null,
                    Result = cells.Count > 2 ? HtmlEntity.DeEntitize(cells[2].InnerText.Trim()) : null
                });
            }
            return results;
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
