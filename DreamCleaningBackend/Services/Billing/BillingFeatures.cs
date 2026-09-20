namespace DreamCleaningBackend.Services.Billing
{
    /// <summary>
    /// SERVER-SIDE rollout switches for the saved-card and AutoPay features (2026-09).
    ///
    /// The old frontend constant <c>CARD_ON_FILE_ENABLED</c> hid the screens and nothing else —
    /// every endpoint behind them stayed reachable. These two keys are enforced by the endpoints
    /// and the background sweeps themselves, and the frontend only reads them (via
    /// <c>GET api/billing/config</c>) to decide what to draw. Both default to OFF when absent, so
    /// an environment that has not opted in behaves exactly as before.
    ///
    ///  • <c>Billing:SavedCardsEnabled</c> — adding / managing cards, paying with a saved card,
    ///    saving a card from a payment, and the admin "Charge saved card" button.
    ///  • <c>Billing:AutoPayEnabled</c> — enabling AutoPay and every automatic charge. With it
    ///    off, the recurring sweep sends its ordinary payment request exactly as it always did.
    ///
    /// Security checks never depend on these: an endpoint that is switched on still verifies
    /// ownership, authorisation and amounts on its own.
    /// </summary>
    public class BillingFeatures
    {
        private readonly IConfiguration _configuration;

        public BillingFeatures(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public bool SavedCardsEnabled => _configuration.GetValue("Billing:SavedCardsEnabled", false);

        /// <summary>AutoPay requires saved cards; one without the other is meaningless.</summary>
        public bool AutoPayEnabled => SavedCardsEnabled && _configuration.GetValue("Billing:AutoPayEnabled", false);
    }
}
