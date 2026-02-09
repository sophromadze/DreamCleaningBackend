namespace DreamCleaningBackend.Models
{
    /// <summary>Pending real-email verification for users who signed in with Apple "Hide My Email".</summary>
    public class RealEmailVerification
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public virtual User User { get; set; } = null!;
        public string RequestedEmail { get; set; } = null!;
        public string VerificationCode { get; set; } = null!;
        public DateTime ExpiresAt { get; set; }
        public int Attempts { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
