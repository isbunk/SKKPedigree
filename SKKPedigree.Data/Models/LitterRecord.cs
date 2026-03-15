namespace SKKPedigree.Data.Models
{
    public class LitterRecord
    {
        public string Id { get; set; } = "";   // FatherId + "_" + MotherId + "_" + BirthYear
        public string? FatherId { get; set; }
        public string? MotherId { get; set; }
        public int? BirthYear { get; set; }
    }
}
