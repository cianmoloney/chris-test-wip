namespace TestFunction.Data;

public class StaffRoleTermsDocument
{
    public int StaffRoleId { get; set; }
    public StaffRole StaffRole { get; set; } = null!;
    public int TermsDocumentId { get; set; }
    public TermsDocument TermsDocument { get; set; } = null!;
}