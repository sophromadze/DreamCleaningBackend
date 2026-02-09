using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>Admin-only notes per user. Stored in a separate table so the User entity is unchanged.</summary>
    public class AdminUserNote
    {
        public int UserId { get; set; }
        public virtual User User { get; set; } = null!;

        [StringLength(2000)]
        public string? Notes { get; set; }
    }
}
