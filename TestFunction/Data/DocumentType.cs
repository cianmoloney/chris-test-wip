using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data
{
    /// <summary>
    /// A kind of document a staff member can hold (e.g. Safe Pass, Forklift Licence).
    /// Staff roles reference document types to define which documents are required.
    /// </summary>
    public class DocumentType
    {
        public int Id { get; set; }

        [Required, MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        public ICollection<DocumentEntry> Documents { get; set; } = new List<DocumentEntry>();

        public ICollection<StaffRoleDocumentType> RequiredByRoles { get; set; } = new List<StaffRoleDocumentType>();
    }
}
