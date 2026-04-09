namespace DreamCleaningBackend.DTOs
{
    public class UserDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public string? Phone { get; set; }
        public bool FirstTimeOrder { get; set; }
        public int? SubscriptionId { get; set; }
        public string? AuthProvider { get; set; }
        public string Role { get; set; }
        /// <summary>True when user has Apple relay email and must verify a real email before using the platform.</summary>
        public bool RequiresRealEmail { get; set; }
        /// <summary>True when user has a password set. When false, show "Set password" in profile.</summary>
        public bool HasPassword { get; set; }
        /// <summary>True when user's email has been verified.</summary>
        public bool IsEmailVerified { get; set; }
    }
}
