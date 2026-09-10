using System.ComponentModel.DataAnnotations;

namespace TestSolution.Data
{
    /// <summary>
    /// An employee account used to sign in to the application. The password
    /// is stored as a hash, never in plain text.
    /// </summary>
    public class User
    {
        public int Id { get; set; }

        [Required, MaxLength(256), EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required, MaxLength(512)]
        public string PasswordHash { get; set; } = string.Empty;

        [MaxLength(32)]
        public string? Phone { get; set; }

        public int RoleId { get; set; }

        public Role? Role { get; set; }

        public DateTime DateCreated { get; set; } = DateTime.UtcNow;

        public bool IsEnabled { get; set; } = true;

        public bool MfaEnabled { get; set; }
    }
}
