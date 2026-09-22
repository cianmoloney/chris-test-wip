using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data
{
    /// <summary>
    /// A staff role (e.g. Driver, Carpenter, Electrician). Each role defines the
    /// set of document types a staff member with that role must upload.
    /// </summary>
    public class StaffRole
    {
        public int Id { get; set; }

        [Required, MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        public ICollection<Staff> StaffMembers { get; set; } = new List<Staff>();

        public ICollection<StaffRoleDocumentType> RequiredDocumentTypes { get; set; } = new List<StaffRoleDocumentType>();

        public ICollection<StaffRoleTermsDocument> RequiredTerms { get; set; } = new List<StaffRoleTermsDocument>();
    }
}
