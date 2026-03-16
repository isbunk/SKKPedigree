namespace SKKPedigree.Data.Models
{
    public class HealthRecord
    {
        public long Id { get; set; }
        public string DogId { get; set; } = "";
        public string? TestType { get; set; }
        public string? Grade { get; set; }
        public string? Result { get; set; }
        public string? TestDate { get; set; }
        public string? VetClinic { get; set; }
    }
}
