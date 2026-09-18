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
        /// Company officer title. Read by the CONTRACTS module only, where it overrides the normal
        /// role hierarchy (a titled CEO/CTO outranks an untitled SuperAdmin there, and an untitled
        /// SuperAdmin falls back to manager-level). Inert everywhere else in the app.
        ///
        /// Who may change it is deliberately not a plain role check — see
        /// <see cref="DreamCleaningBackend.Helpers.Contracts.OrgTitlePolicy"/>: any SuperAdmin may
        /// assign titles while no CTO exists (bootstrap), and once one does, only that CTO may.
        /// </summary>
        public OrgTitle OrgTitle { get; set; } = OrgTitle.None;

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

        /// <summary>
        /// THE REFRESH TOKEN THIS ACCOUNT HELD IMMEDIATELY BEFORE THE LAST RENEWAL (2026-09).
        ///
        /// A refresh token is single-use: AuthService.RefreshToken writes a new one on every
        /// renewal, so presenting the old one a second time is refused. That is right against a
        /// STOLEN token and wrong against the browser, which presents the same one twice for
        /// reasons nobody chose - a panel fires six requests at once and they 401 together, the
        /// admin has the site open in two tabs, a 60-second poll lands in the same instant as a
        /// click. One renewal wins and the rest were told "Invalid refresh token", which the
        /// frontend answered by logging the person out - destroying the session that had just
        /// been renewed successfully.
        ///
        /// So the previous token stays acceptable for AuthService.RefreshTokenReplayGrace after it
        /// is replaced. Inside that window a duplicate presentation does NOT rotate anything: it
        /// hands back the tokens the winning call already issued, so every racing caller ends up
        /// on the same current session. Outside it the token is dead, which is the single-use
        /// property that actually matters.
        /// </summary>
        public string? PreviousRefreshToken { get; set; }

        /// <summary>When <see cref="PreviousRefreshToken"/> stops being accepted. Null = no replay window is open.</summary>
        public DateTime? PreviousRefreshTokenExpiryTime { get; set; }

        /// <summary>
        /// Bumped whenever every session this account holds must end NOW - a role change, today.
        /// The value is stamped into each JWT as the "tv" claim and re-checked on every request
        /// (TokenVersionService), so an access token issued before the bump is refused even though
        /// it is still signed and inside its 7-day lifetime. That is what logs out an OFFLINE
        /// user: the SignalR notice only reaches somebody who has the page open.
        /// Legacy tokens carry no claim and read as 0, which matches the column default, so
        /// deploying this does not sign the whole customer base out.
        /// </summary>
        public int TokenVersion { get; set; }

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

        /// <summary>
        /// Marks a CUSTOMER account as a business rather than a household. Set by staff from the
        /// Users tab; it is not something a customer can claim for themselves.
        ///
        /// Its only job today is to open the self-service "My Contracts" area: that menu item and
        /// portal are shown when this is true AND the account actually owns a contract (a
        /// ContractClient carrying this user's id). It grants nothing on its own — a business flag
        /// with no contract behind it shows nothing — and it does not change pricing, booking or
        /// any other customer-facing behaviour.
        /// </summary>
        public bool IsBusiness { get; set; } = false;

        // Email verification
        public bool IsEmailVerified { get; set; } = false;
        /// <summary>True when user signed in with Apple "Hide My Email" and must provide a real email before using the platform.</summary>
        public bool RequiresRealEmail { get; set; } = false;
        public string? EmailVerificationToken { get; set; }
        public DateTime? EmailVerificationTokenExpiry { get; set; }
        /// <summary>Hash of the token that was used to verify (so re-clicking the same link returns success).</summary>
        public string? LastEmailVerificationTokenHash { get; set; }

        /// <summary>When the one-per-account welcome email was sent; null when it has not been.
        /// This column is the claim that keeps it to exactly one — see Helpers/WelcomeEmailPolicy.
        /// Null on every pre-existing row on purpose: nothing re-sends to an old account, because
        /// the four paths that send it only fire when an account FIRST gains a usable address.</summary>
        public DateTime? WelcomeEmailSentAt { get; set; }

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

        /// <summary>
        /// LIFETIME mode (2026-09). The loyalty discount above is normally ONE-TIME: it applies to
        /// the next eligible order and is consumed. With this set it is not consumed — it stays on
        /// the account, at the same percentage, for every future eligible order until an admin
        /// changes or clears it.
        ///
        /// Three consequences, all of them load-bearing and all in <c>LoyaltyDiscountService</c>
        /// and <c>LoyaltyReengagementService</c>:
        ///
        ///  • <b>It is never consumed.</b> <c>ApplyToOrderAsync</c> stamps LastUsedAt and leaves
        ///    the percentage alone, so a recurring series keeps getting it. Consequently there is
        ///    nothing for <c>ReverseFromOrderAsync</c> to restore on a cancellation either.
        ///  • <b>It TURNS OFF the inactivity automation for this customer.</b> No 60-day 10%, no
        ///    90-day upgrade to 15%, no overwrite. A lifetime discount is a standing commercial
        ///    decision, and letting the win-back worker raise, lower or replace it would mean an
        ///    admin's agreement quietly expired because somebody did not book for three months.
        ///    Clearing the discount hands the customer straight back to the normal automation.
        ///  • <b>It still does not STACK.</b> It goes through the same
        ///    <c>ResolveStacking</c> gate as a one-time discount, so the best single discount
        ///    wins per order. Losing that round does not remove it from the account — it is a
        ///    standing entitlement, not a coupon that got spent.
        ///
        /// Implies <see cref="LoyaltyDiscountIsManualOverride"/> in every practical sense (only an
        /// admin can set it) but is kept separate: "an admin picked this number" and "this number
        /// survives being used" are different facts, and folding them together would make clearing
        /// one clear the other.
        /// </summary>
        public bool LoyaltyDiscountIsLifetime { get; set; } = false;

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