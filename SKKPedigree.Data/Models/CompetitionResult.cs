namespace SKKPedigree.Data.Models
{
    public class CompetitionResult
    {
        public long Id { get; set; }
        public string DogId { get; set; } = "";
        public string? EventDate { get; set; }
        public string? Location { get; set; }
        public string? EventType { get; set; }
        public string? Organiser { get; set; }
        public string? Result { get; set; }
    }
}
