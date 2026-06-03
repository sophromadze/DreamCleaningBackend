using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // A device the staff user has previously passed 2FA on. While the row exists and
    // RevokedAt is null, login from this device skips the 2FA challenge.
    //
    // SECURITY: TokenHash is SHA-256 of the random token sent to the client (stored in
    // localStorage as `tf_device_token`). We hash so a DB leak doesn't yield usable tokens.
    public class TrustedDevice
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        [Required]
        [StringLength(128)]
        public string TokenHash { get; set; } = string.Empty;

        // Human-friendly label parsed from the User-Agent (e.g. "Chrome on macOS").
        [StringLength(200)]
        public string? DeviceName { get; set; }

        [StringLength(100)]
        public string? Browser { get; set; }

        [StringLength(100)]
        public string? OperatingSystem { get; set; }

        [StringLength(45)]
        public string? IpAddress { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

        // Null = trusted; set = revoked (by user, by SuperAdmin, or via password change).
        public DateTime? RevokedAt { get; set; }
    }
}
