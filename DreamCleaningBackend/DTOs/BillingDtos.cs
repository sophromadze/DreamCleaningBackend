namespace DreamCleaningBackend.DTOs
{
    // ── Saved cards ─────────────────────────────────────────────────────────────────────────────

    public class SavedCardDto
    {
        public int Id { get; set; }

        /// <summary>
        /// The Stripe pm id. Returned ONLY to the card's owner, whose browser needs it to confirm a
        /// payment with this card. Admin DTOs never carry it.
        /// </summary>
        public string? PaymentMethodId { get; set; }

        public string? Brand { get; set; }
        public string? Last4 { get; set; }
        public int? ExpMonth { get; set; }
        public int? ExpYear { get; set; }
        public string? Wallet { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsBackup { get; set; }
        public bool IsExpired { get; set; }

        /// <summary>active / blocked</summary>
        public string Status { get; set; } = "active";

        /// <summary>Why a card is blocked, in customer-safe words.</summary>
        public string? StatusMessage { get; set; }

        /// <summary>Usable for a charge right now (active and not expired).</summary>
        public bool IsUsable { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public class SetupIntentResponseDto
    {
        public string ClientSecret { get; set; } = string.Empty;
        public string SetupIntentId { get; set; } = string.Empty;
    }

    public class CompleteSetupIntentDto
    {
        public string SetupIntentId { get; set; } = string.Empty;
    }

    public class SetBackupCardDto
    {
        /// <summary>Null clears the Backup role.</summary>
        public int? CardId { get; set; }
    }

    public class RemoveCardDto
    {
        /// <summary>Required when removing the Primary while other usable cards exist.</summary>
        public int? NewPrimaryCardId { get; set; }
    }

    public class CardMutationResultDto
    {
        public string Message { get; set; } = string.Empty;
        public List<SavedCardDto> Cards { get; set; } = new();
        public bool AutoPayEnabled { get; set; }
    }

    /// <summary>The intent whose card the customer chose to save in the pre-payment modal.
    /// The choice is verified against Stripe's own record on that intent, never trusted from
    /// here — see BillingController.SaveCardFromPayment.</summary>
    public class SaveCardFromPaymentDto
    {
        public string PaymentIntentId { get; set; } = string.Empty;
    }

    // ── AutoPay ────────────────────────────────────────────────────────────────────────────────

    public class BillingConfigDto
    {
        public bool SavedCardsEnabled { get; set; }
        public bool AutoPayEnabled { get; set; }
    }

    public class AutoPayTermsDto
    {
        public string Scope { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    public class AutoPayArrangementDto
    {
        /// <summary>general / office / series / client</summary>
        public string Scope { get; set; } = string.Empty;
        public int? RecurringSeriesId { get; set; }
        public int? ContractClientId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Timing { get; set; } = string.Empty;

        public bool IsAuthorized { get; set; }

        /// <summary>Authorised AND the master switch is on AND a usable Primary exists.</summary>
        public bool IsEffective { get; set; }

        /// <summary>Why an authorised arrangement is not currently charging.</summary>
        public string? PausedReason { get; set; }

        public bool AllowBackupFallback { get; set; }
        public int? AuthorizationId { get; set; }
        public DateTime? AuthorizedAt { get; set; }
        public string? TermsVersion { get; set; }
    }

    public class AutoPayOverviewDto
    {
        public bool FeatureEnabled { get; set; }
        public bool AutoPayEnabled { get; set; }
        public DateTime? AutoPayEnabledAt { get; set; }
        public SavedCardDto? PrimaryCard { get; set; }
        public SavedCardDto? BackupCard { get; set; }
        public List<AutoPayArrangementDto> Arrangements { get; set; } = new();
    }

    public class EnableAutoPayDto
    {
        public bool AcceptTerms { get; set; }
        public string TermsVersion { get; set; } = string.Empty;
    }

    public class AuthorizeArrangementDto
    {
        /// <summary>series / office / client</summary>
        public string Scope { get; set; } = string.Empty;
        public int? RecurringSeriesId { get; set; }
        public int? ContractClientId { get; set; }
        public bool AllowBackupFallback { get; set; }
        public bool AcceptTerms { get; set; }
        public string TermsVersion { get; set; } = string.Empty;

        // Office scope only — each must be ticked separately.
        public bool SmsConsent { get; set; }
        public bool CancellationFeeConsent { get; set; }
        public bool TermsOfServiceConsent { get; set; }
    }

    // ── Charges, obligations, history, notices ─────────────────────────────────────────────────

    public class SavedCardChargeResponseDto
    {
        /// <summary>
        /// paid / paid_by_backup / failed / requires_action / in_progress / pending / blocked /
        /// nothing_due / not_authorized / unknown
        /// </summary>
        public string Result { get; set; } = string.Empty;
        public bool Charged { get; set; }
        public string Message { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string? PaymentIntentId { get; set; }

        /// <summary>On-session 3DS only: the browser completes the challenge with this.</summary>
        public string? ClientSecret { get; set; }
    }

    public class PayWithSavedCardDto
    {
        public int CardId { get; set; }
    }

    public class OutstandingObligationDto
    {
        /// <summary>order / invoice</summary>
        public string Type { get; set; } = string.Empty;
        public int Id { get; set; }
        public string Reference { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal AmountDue { get; set; }
        public DateTime? DueDate { get; set; }
        public string PayUrl { get; set; } = string.Empty;

        /// <summary>A payment is being processed — do not offer another.</summary>
        public bool PaymentInProgress { get; set; }

        public bool AutoPayFailed { get; set; }
    }

    public class BillingHistoryItemDto
    {
        public string Key { get; set; } = string.Empty;
        public DateTime Date { get; set; }

        /// <summary>order / invoice / attempt</summary>
        public string Kind { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Positive for money in, negative for refunds.</summary>
        public decimal Amount { get; set; }

        /// <summary>Paid / Pending / Failed / Refunded / Partially Refunded / Requires Action</summary>
        public string Status { get; set; } = string.Empty;
        public string? PaymentMethodLabel { get; set; }
        public int? OrderId { get; set; }
        public int? InvoiceId { get; set; }
        public string? ActionUrl { get; set; }
        public string? ActionLabel { get; set; }
    }

    public class BillingHistoryPageDto
    {
        public List<BillingHistoryItemDto> Items { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
    }

    public class BillingNotificationDto
    {
        public int Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? ActionUrl { get; set; }
        public string? ActionLabel { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsRead { get; set; }
        public bool IsResolved { get; set; }
    }

    // ── Admin ──────────────────────────────────────────────────────────────────────────────────

    public class AdminBillingAttemptDto
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Obligation { get; set; } = string.Empty;
        public int? OrderId { get; set; }
        public int? InvoiceId { get; set; }
        public string Trigger { get; set; } = string.Empty;
        public string CardRole { get; set; } = string.Empty;
        public string? CardLabel { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
    }

    public class AdminUserBillingDto
    {
        public bool FeatureEnabled { get; set; }
        public List<SavedCardDto> Cards { get; set; } = new();
        public AutoPayOverviewDto AutoPay { get; set; } = new();
        public List<OutstandingObligationDto> Outstanding { get; set; } = new();
        public List<AdminBillingAttemptDto> RecentAttempts { get; set; } = new();
        public List<BillingHistoryItemDto> RecentHistory { get; set; } = new();
        public List<BillingNotificationDto> OpenIssues { get; set; } = new();
    }

    public class AdminOrderSavedCardInfoDto
    {
        public bool FeatureEnabled { get; set; }
        public bool HasCard { get; set; }
        public string? Brand { get; set; }
        public string? Last4 { get; set; }
        public bool HasOfficeAuthorization { get; set; }
        public bool BackupAllowed { get; set; }
        public decimal AmountDue { get; set; }

        /// <summary>Why the charge button is unavailable, in words an admin can act on.</summary>
        public string? UnavailableReason { get; set; }
    }
}
