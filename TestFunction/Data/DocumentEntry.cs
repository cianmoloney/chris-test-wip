using System.ComponentModel.DataAnnotations;
using TestShared;

namespace TestFunction.Data
{
    public class DocumentEntry
    {
        public int Id { get; set; }

        /// <summary>Display name of the document (editable by the admin).</summary>
        [MaxLength(512)]
        public string Name { get; set; } = string.Empty;

        /// <summary>Immutable blob name of the uploaded file (e.g. "staff/1/mydoc.pdf"). Used for dedup and downloads.</summary>
        [MaxLength(512)]
        public string? BlobName { get; set; }
        [MaxLength(63)] public string? ContainerName { get; set; }
        public bool ScanPassed { get; set; }
        public DateTimeOffset? ProcessingCompletedAt { get; set; }
        public DateTimeOffset? LastProcessingAttempt { get; set; }
        [MaxLength(512)] public string? Issue { get; set; }
        public bool IsArchived { get; set; }
        [Timestamp] public byte[] RowVersion { get; set; } = [];

        /// <summary>Holder name extracted from the document.</summary>
        [MaxLength(256)]
        public string? ExtractedName { get; set; }

        /// <summary>Email address extracted from the document.</summary>
        [MaxLength(256)]
        public string? Email { get; set; }

        /// <summary>Phone number extracted from the document.</summary>
        [MaxLength(64)]
        public string? Phone { get; set; }

        [MaxLength(128)]
        public string? DocumentType { get; set; }

        /// <summary>The kind of document (e.g. Safe Pass, Forklift). Used to
        /// check role requirements.</summary>
        public int? DocumentTypeId { get; set; }

        public DocumentType? Type { get; set; }

        [MaxLength(128)]
        public string? DocumentNumber { get; set; }

        public DateTimeOffset? StartDate { get; set; }

        public DateTimeOffset? ExpiryDate { get; set; }

        public bool IsValid { get; set; }

        /// <summary>Review state of the document (pending, validated, rejected, or parse failure).</summary>
        public DocumentStatus Status { get; set; } = DocumentStatus.PendingReview;

        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        public int? StaffId { get; set; }

        public Staff? Staff { get; set; }
    }
}
