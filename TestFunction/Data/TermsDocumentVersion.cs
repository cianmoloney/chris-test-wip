using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data
{
    /// <summary>
    /// A specific version of a terms document, in a specific language.
    /// Staff accept terms against a version, so there is always a record of
    /// exactly what text was agreed to.
    /// </summary>
    public class TermsDocumentVersion
    {
        public int Id { get; set; }

        public int TermsDocumentId { get; set; }

        public TermsDocument TermsDocument { get; set; } = null!;

        [Required]
        public string Content { get; set; } = string.Empty;

        /// <summary>Two-letter ISO language code, e.g. "en", "pl", "uk".</summary>
        [Required, MaxLength(8)]
        public string Language { get; set; } = "en";

        public int Version { get; set; } = 1;

        public bool IsActive { get; set; } = true;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public ICollection<StaffTermsAcceptance> Acceptances { get; set; } = new List<StaffTermsAcceptance>();
    }
}
