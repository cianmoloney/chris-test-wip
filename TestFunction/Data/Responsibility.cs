using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data
{
    /// <summary>
    /// A capability granted to a role (e.g. "Generate links"). When enabled
    /// the feature is visible to users in that role, otherwise it is hidden.
    /// </summary>
    public class Responsibility
    {
        public int Id { get; set; }

        public int RoleId { get; set; }

        public Role? Role { get; set; }

        [Required, MaxLength(128)]
        public string Name { get; set; } = string.Empty;

        public bool IsEnabled { get; set; } = true;
    }
}
