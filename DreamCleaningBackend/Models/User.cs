using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using DreamCleaningBackend.Helpers;

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

        private string? _phone;
        [StringLength(20)]
        public string? Phone
        {
            get => _phone;
            set => _phone = PhoneHelper.NormalizeToDigits(value);
        }

        // User role - defaults to Customer (0)
        public UserRole Role { get; set; } = UserRole.Customer;

        /// <summary>
        /// JSON array of restricted-admin-page keys this user (Admin role only) has been granted
        /// read-only access to by a SuperAdmin (e.g. ["statistics","expenses"]). Null/empty = none.
        /// Keys are defined in <see cref="DreamCleaningBackend.Services.AdminViewablePages"/>.
        /// </summary>
        public string? ViewablePages { get; set; }

        /// <summary>
        /// SuperAdmin grant: when true, this (Admin-role) user saves order edits directly instead of
        /// submitting them for SuperAdmin approval. Inert for every other role - the rule itself lives
        /// in <see cref="DreamCleaningBackend.Helpers.OrderEditApprovalPolicy"/>.
        /// </summary>
        public bool CanEditOrdersWithoutApproval { get; set; }

        /// <summary>
        /// Manager vs Administrator, for Admin-role staff only. Drives the per-order bonus split and
        /// nothing else — no permission anywhere reads it. See <see cref="Models.AdminPosition"/>.
        /// </summary>
        public AdminPosition AdminPosition { get; set; } = AdminPosition.Administrator;

        /// <summary>
        /// The Manager this Administrator reports to. When an administrator books an order, their
        /// manager earns the manager-side bonus for it. Null = this administrator has no manager
        /// (nobody earns the manager side), and always null on a Manager row — a manager does not
        /// report to another manager. Self-referencing FK, Restrict on delete like every other
        /// User → User link here.
        /// </summary>
        public int? ManagerId { get; set; }

        [ForeignKey("ManagerId")]
        public virtual User? Manager { get; set; }

        // Refresh token for JWT
        public string? RefreshToken { get; set; }
        public DateTime? RefreshTokenExpiryTime { get; set; }

        // OAuth provider info
        public string? AuthProvider { get; set; } // "Local", "Google", "Apple"
        public string? ExternalAuthId { get; set; } // ID from OAuth provider (Google ID when linked)
        /// <summary>Apple Sign In subject (sub). NotMapped until migration AddAppleUserIdToUsers is applied to the database.</summary>
        [NotMapped]
        [StringLength(255)]
        public string? AppleUserId { get; set; }

        // ─── Card on file ───
        // One saved card per user, used ONLY for explicit customer/admin-triggered charges —
        // there is no automatic billing. Brand/last4 are display-only copies so the UI never
        // needs a Stripe round-trip ("Visa ending 4242").

        /// <summary>Stripe Customer id (cus_...), created lazily the first time this user saves a card.</summary>
        [StringLength(100)]
        public string? StripeCustomerId { get; set; }

        /// <summary>The saved card (Stripe PaymentMethod id, pm_...). Null = no card on file.</summary>
        [StringLength(100)]
        public string? DefaultPaymentMethodId { get; set; }

        [StringLength(20)]
        public string? SavedCardBrand { get; set; }

        [StringLength(4)]
        public string? SavedCardLast4 { get; set; }

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

        // ── Admin-only "problem customer" flag (single source of truth) ──
        // Flagging an order sets this on the order's owner; every order of a flagged user
        // renders the tint derived from here. None = no flag. Internal only — never surfaced
        // to the customer.
        public CustomerFlagLevel Flag { get; set; } = CustomerFlagLevel.None;
        [StringLength(500)]
        public string? FlagReason { get; set; }
        public DateTime? FlaggedAt { get; set; }
        public int? FlaggedByUserId { get; set; }

        /// <summary>
        /// True for admin-created customers who have no email address at all (e.g. elderly
        /// customers who pay by cash, Zelle, check, ...). Email holds a generated non-routable
        /// placeholder (see NoEmailHelper) that must never be displayed or mailed to. These
        /// accounts cannot log in; admins manage everything for them. Setting a real email via
        /// admin edit clears this flag.
        /// </summary>
        public bool IsNoEmailUser { get; set; } = false;

        // Email verification
        public bool IsEmailVerified { get; set; } = false;
        /// <summary>True when user signed in with Apple "Hide My Email" and must provide a real email before using the platform.</summary>
        public bool RequiresRealEmail { get; set; } = false;
        public string? EmailVerificationToken { get; set; }
        public DateTime? EmailVerificationTokenExpiry { get; set; }
        /// <summary>Hash of the token that was used to verify (so re-clicking the same link returns success).</summary>
        public string? LastEmailVerificationTokenHash { get; set; }

        // Password recovery
        public string? PasswordResetToken { get; set; }
        public DateTime? PasswordResetTokenExpiry { get; set; }

        // Email change verification
        public string? PendingEmail { get; set; }
        public string? EmailChangeToken { get; set; }
        public DateTime? EmailChangeTokenExpiry { get; set; }

        /// <summary>When true, user can receive emails and (in future) SMS from the company. Used for both marketing and transactional communications.</summary>
        public bool CanReceiveCommunications { get; set; } = true;

        /// <summary>When true, user can receive emails (marketing and transactional) from the company.</summary>
        public bool CanReceiveEmails { get; set; } = true;

        /// <summary>When true, user can receive SMS/messages (e.g. RingCentral) from the company.</summary>
        public bool CanReceiveMessages { get; set; } = true;

        /// <summary>Hex color chosen by the admin for the shift calendar (e.g. "#4f46e5").</summary>
        [StringLength(10)]
        public string? ShiftColor { get; set; }

        // Soft delete (for merged accounts)
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }
        [StringLength(500)]
        public string? DeletedReason { get; set; }

        // Login OTP (for admin-created users without a password)
        [StringLength(6)]
        public string? LoginOtpCode { get; set; }
        public DateTime? LoginOtpExpiry { get; set; }
        public int LoginOtpAttempts { get; set; } = 0;

        // Bubble Rewards
        [StringLength(20)]
        public string? ReferralCode { get; set; }
        public int? ReferredByUserId { get; set; }
        public virtual User? ReferredBy { get; set; }
        public int BubblePoints { get; set; } = 0;
        public decimal BubbleCredits { get; set; } = 0;
        public decimal TotalSpentAmount { get; set; } = 0;
        public int ConsecutiveOrderCount { get; set; } = 0;
        public DateTime? LastCompletedOrderDate { get; set; }
        public bool ReviewBonusGranted { get; set; } = false;
        public bool WelcomeBonusGranted { get; set; } = false;

        // Loyalty Discount (re-engagement system). The percentage sits dormant on the account
        // until applied to an order; when consumed it resets to 0 and IsManualOverride flips
        // back to false. ManualOverride freezes the value against background-service changes
        // (auto activation / auto upgrade) but does not block reminder sends.
        [Column(TypeName = "decimal(5,2)")]
        public decimal LoyaltyDiscountPercentage { get; set; } = 0;

        public DateTime? LoyaltyDiscountActivatedAt { get; set; }
        public DateTime? LoyaltyDiscountLastUsedAt { get; set; }
        public bool LoyaltyDiscountIsManualOverride { get; set; } = false;

        // ─── Two-factor authentication (staff only — Admin / SuperAdmin / Moderator) ───
        // Stored as "base64(salt)$base64(hash)". HMAC-SHA512 uses a 128-byte default key
        // and produces a 64-byte digest → ~261 chars base64-encoded with the separator,
        // so we sized this at 500 to leave headroom. Length 4–12 digits PIN (user-chosen).
        // Customers ignore these fields entirely.
        [StringLength(500)]
        public string? TwoFactorPinHash { get; set; }
        public DateTime? TwoFactorPinSetAt { get; set; }

        // Rate-limit state: after too many wrong PIN attempts we lock the PIN flow for a
        // cooldown window (caller falls back to email-code-only or just waits it out).
        public int TwoFactorPinFailedAttempts { get; set; } = 0;
        public DateTime? TwoFactorPinLockedUntil { get; set; }
    }
}