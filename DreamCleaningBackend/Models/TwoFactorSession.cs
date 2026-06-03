using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DreamCleaningBackend.Models
{
    // Short-lived (~15 min) server-side state representing a 2FA challenge in progress.
    // Created after password verification succeeds for a staff user without a trusted
    // device. Deleted after successful login or expiry. Replaces the need for a
    // "partial JWT" carried between steps — keeps secrets server-side.
    public class TwoFactorSession
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public int UserId { get; set; }

        [ForeignKey("UserId")]
        public virtual User User { get; set; } = null!;

        // SHA-256 of the 6-digit code sent by email. Hashed so a DB read can't reveal it.
        [Required]
        [StringLength(128)]
        public string EmailCodeHash { get; set; } = string.Empty;

        public DateTime CodeSentAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }

        // Marks step 1 complete. Step 2 (PIN / WebAuthn) cannot run until this is set.
        public DateTime? EmailVerifiedAt { get; set; }

        // Brute-force protection: each wrong attempt increments. Hard cap is enforced in
        // TwoFactorService (after N wrong codes the session is killed and user must restart).
        public int EmailCodeAttempts { get; set; } = 0;

        // The client is free to ask to resend; we cap so spammers can't drain the SMTP quota.
        public int EmailCodeResends { get; set; } = 0;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
