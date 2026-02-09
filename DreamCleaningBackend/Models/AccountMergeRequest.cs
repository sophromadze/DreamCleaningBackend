namespace DreamCleaningBackend.Models
{
    /// <summary>Pending account merge when Apple user verifies an email that already belongs to another account.</summary>
    public class AccountMergeRequest
    {
        public int Id { get; set; }
        public int NewAccountId { get; set; }
        public virtual User NewAccount { get; set; } = null!;
        public int OldAccountId { get; set; }
        public virtual User OldAccount { get; set; } = null!;
        /// <summary>6-digit code sent to verified real email for merge confirmation.</summary>
        public string VerificationCode { get; set; } = null!;
        public string VerifiedRealEmail { get; set; } = null!;
        public AccountMergeRequestStatus Status { get; set; } = AccountMergeRequestStatus.Pending;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public enum AccountMergeRequestStatus
    {
        Pending = 0,
        Verified = 1,
        Merged = 2,
        Expired = 3,
        Cancelled = 4
    }
}
