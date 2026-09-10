namespace TestSolution.Data
{
    /// <summary>
    /// Associates a staff role with a document type it requires.
    /// One role can require many document types, and a document type can be
    /// required by many roles. A staff member is expected to upload ALL
    /// documents required by their role.
    /// </summary>
    public class StaffRoleDocumentType
    {
        public int StaffRoleId { get; set; }

        public StaffRole StaffRole { get; set; } = null!;

        public int DocumentTypeId { get; set; }

        public DocumentType DocumentType { get; set; } = null!;
    }
}
