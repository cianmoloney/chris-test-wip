using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data
{
    /// <summary>
    /// A user role (HR, Admin, Foreman). Each role owns a set of
    /// responsibilities that can be individually enabled or disabled.
    /// </summary>
    public class Role
    {
        public int Id { get; set; }

        [Required, MaxLength(64)]
        public string Name { get; set; } = string.Empty;

        public ICollection<User> Users { get; set; } = new List<User>();

        public ICollection<Responsibility> Responsibilities { get; set; } = new List<Responsibility>();
    }
}
