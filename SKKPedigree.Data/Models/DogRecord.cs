using System.Collections.Generic;

namespace SKKPedigree.Data.Models
{
    public class DogRecord
    {
        public string Id { get; set; } = "";           // SKK registration number
        public int? HundId { get; set; }               // integer URL param (hundid=X)
        public string Name { get; set; } = "";
        public string? Breed { get; set; }
        public string? Sex { get; set; }               // "M" or "F"
        public string? BirthDate { get; set; }         // ISO date string
        public string? FatherId { get; set; }
        public string? FatherName { get; set; }
        public string? FatherUrl { get; set; }         // relative URL for scraping father
        public string? MotherId { get; set; }
        public string? MotherName { get; set; }
        public string? MotherUrl { get; set; }
        public string? LitterId { get; set; }
        public string? KennelName { get; set; }
        public string? BreederName { get; set; }
        public string? BreederCity { get; set; }
        public string? IdNumber { get; set; }    // stud book / reg ID (lblIDnummer)
        public string? Color { get; set; }       // coat color (lblFarg)
        public string? CoatType { get; set; }    // coat type (lblHarlag)
        public string? Size { get; set; }        // size category (lblStorlek)
        public string? ChipNumber { get; set; }  // microchip (lblChipnr)
        public bool IsDeceased { get; set; }     // "Hunden avliden" flag
        public string ScrapedAt { get; set; } = "";

        // Navigation / scraping helpers (not stored directly in Dog table)
        public List<string> SiblingUrls { get; set; } = new();
        public List<HealthRecord> HealthRecords { get; set; } = new();
        public List<CompetitionResult> Results { get; set; } = new();
        public List<string> Titles { get; set; } = new();
    }
}
