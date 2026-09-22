using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data
{
    /// <summary>
    /// A staff member who registered via a secure registration link.
    /// </summary>
    public class Staff
    {
        public bool IsArchived { get; set; }
        public int Id { get; set; }

        /// <summary>
        /// Human-readable identifier, prefixed by staff type
        /// (e.g. "P42" for permanent, "E42" for external).
        /// </summary>
        [MaxLength(16)]
        public string? StaffId { get; set; }

        /// <summary>
        /// Sequential number used as the numeric part of <see cref="StaffId"/>.
        /// Auto-incremented by one for every registered staff member via a SQL sequence.
        /// </summary>
        public int StaffNumber { get; set; }

        public int? StaffTypeId { get; set; }

        public StaffType? StaffType { get; set; }

        [Required, MaxLength(128)]
        public string FirstName { get; set; } = string.Empty;

        [Required, MaxLength(128)]
        public string LastName { get; set; } = string.Empty;

        [Required, EmailAddress, MaxLength(256)]
        public string Email { get; set; } = string.Empty;

        [Phone, MaxLength(32)]
        public string? PhoneNumber { get; set; }

        /// <summary>The staff member's role (e.g. Driver, Carpenter, Electrician).
        /// For now each staff member has exactly one role.</summary>
        public int? StaffRoleId { get; set; }

        public StaffRole? StaffRole { get; set; }

        public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

        public ICollection<DocumentEntry> Documents { get; set; } = new List<DocumentEntry>();

        public ICollection<StaffTermsAcceptance> TermsAcceptances { get; set; } = new List<StaffTermsAcceptance>();
    }
}
