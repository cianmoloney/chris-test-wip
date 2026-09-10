namespace TestSolution.Data
{
    /// <summary>
    /// Records that a staff member accepted a specific terms document version at a
    /// point in time. A staff member may accept many terms over their role and the
    /// same terms version may be accepted by many staff members (many:many with history).
    /// </summary>
    public class StaffTermsAcceptance
    {
        public int Id { get; set; }

        public int StaffId { get; set; }

        public Staff Staff { get; set; } = null!;

        public int TermsDocumentVersionId { get; set; }

        public TermsDocumentVersion TermsDocumentVersion { get; set; } = null!;

        public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
