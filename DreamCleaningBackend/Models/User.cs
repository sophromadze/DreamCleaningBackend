using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    public class User
    {
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string FirstName { get; set; }

        [Required]
        [StringLength(50)]
        public string LastName { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(100)]
        public string Email { get; set; }

        // Nullable for OAuth users (Google/Apple)
        public string? PasswordHash { get; set; }
        public string? PasswordSalt { get; set; }

        // for google avatar
        [StringLength(500)]
        public string? ProfilePictureUrl { get; set; }

        [StringLength(20)]
        public string? Phone { get; set; }

        // User role - defaults to Customer (0)
        public UserRole Role { get; set; } = UserRole.Customer;

        // Refresh token for JWT
        public string? RefreshToken { get; set; }
        public DateTime? RefreshTokenExpiryTime { get; set; }

        // OAuth provider info
        public string? AuthProvider { get; set; } // "Local", "Google", "Apple"
        public string? ExternalAuthId { get; set; } // ID from OAuth provider

        // Subscription
        public int? SubscriptionId { get; set; }
        public virtual Subscription? Subscription { get; set; }
        public DateTime? SubscriptionStartDate { get; set; }
        public DateTime? SubscriptionExpiryDate { get; set; }
        public DateTime? LastOrderDate { get; set; }

        // First time order discount
        public bool FirstTimeOrder { get; set; } = true;
        // Add this line to your existing User.cs
        public virtual ICollection<UserSpecialOffer> UserSpecialOffers { get; set; } = new List<UserSpecialOffer>();

        // User's apartments
        public virtual ICollection<Apartment> Apartments { get; set; } = new List<Apartment>();
        public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

        // Audit fields
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool IsActive { get; set; } = true;

        // Email verification
        public bool IsEmailVerified { get; set; } = false;
        public string? EmailVerificationToken { get; set; }
        public DateTime? EmailVerificationTokenExpiry { get; set; }

        // Password recovery
        public string? PasswordResetToken { get; set; }
        public DateTime? PasswordResetTokenExpiry { get; set; }

        // Email change verification
        public string? PendingEmail { get; set; }
        public string? EmailChangeToken { get; set; }
        public DateTime? EmailChangeTokenExpiry { get; set; }
    }
}