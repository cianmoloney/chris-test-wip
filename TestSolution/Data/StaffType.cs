using System.ComponentModel.DataAnnotations;

namespace TestSolution.Data
{
    /// <summary>
    /// The kind of staff member (e.g. Permanent, External/contractor). The prefix is
    /// used to build the human-readable StaffId (P... or E...).
    /// </summary>
    public class StaffType
    {
        public int Id { get; set; }

        [Required, MaxLength(64)]
        public string Name { get; set; } = string.Empty;

        [Required, MaxLength(1)]
        public string Prefix { get; set; } = string.Empty;

        public ICollection<Staff> StaffMembers { get; set; } = new List<Staff>();
    }
}
