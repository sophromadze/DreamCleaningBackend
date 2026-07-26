namespace DreamCleaningBackend.Services.Interfaces
{
    /// <summary>The customer's saved card as the UI needs it. PaymentMethodId is included so
    /// the OWNER's browser can confirm a PaymentIntent with it ("Pay with card ending ####") —
    /// it is only ever returned to the authenticated owner or an admin.</summary>
    public class SavedCardDto
    {
        public string PaymentMethodId { get; set; } = "";
        public string? Brand { get; set; }
        public string? Last4 { get; set; }
    }

    /// <summary>
    /// Card on file: one saved Stripe PaymentMethod per user, charged only by explicit
    /// customer/admin action — never on a schedule, never silently.
    /// </summary>
    public interface ICardOnFileService
    {
        /// <summary>The user's saved card, or null. Reads the User columns only (no Stripe call).</summary>
        Task<SavedCardDto?> GetSavedCardAsync(int userId);

        /// <summary>
        /// Saves the card that paid the given PaymentIntent as the user's card on file
        /// (checkout "save this card" checkbox — the PI was created with setup_future_usage).
        /// Best-effort and never throws: a card-save problem must never break a paid booking.
        /// Detaches the previously saved card when it is being replaced.
        /// </summary>
        Task<bool> TrySaveCardFromPaymentIntentAsync(int userId, string paymentIntentId);

        /// <summary>
        /// Saves a card collected through the profile SetupIntent flow. Verifies the
        /// PaymentMethod belongs to THIS user's Stripe Customer (so nobody can point their
        /// account at an arbitrary pm id), persists it, and detaches the replaced card.
        /// Throws InvalidOperationException with a customer-readable message on failure.
        /// </summary>
        Task<SavedCardDto> SaveCardFromSetupAsync(int userId, string paymentMethodId);

        /// <summary>Removes the saved card: detaches it in Stripe (best-effort) and clears the
        /// User columns. Idempotent — removing when nothing is saved is a no-op.</summary>
        Task RemoveSavedCardAsync(int userId);
    }
}
