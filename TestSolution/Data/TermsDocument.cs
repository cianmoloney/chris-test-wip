using System.ComponentModel.DataAnnotations;

namespace TestSolution.Data
{
    /// <summary>
    /// The core terms concept. The actual text lives in
    /// <see cref="TermsDocumentVersion"/> (1:many), so a terms document can
    /// evolve over versions and be offered in multiple languages.
    /// </summary>
    public class TermsDocument
    {
        public int Id { get; set; }

        [Required, MaxLength(256)]
        public string Title { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public ICollection<TermsDocumentVersion> Versions { get; set; } = new List<TermsDocumentVersion>();
    }
}
