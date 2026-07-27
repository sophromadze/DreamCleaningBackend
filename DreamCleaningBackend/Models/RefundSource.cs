namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// Where an OrderRefund row came from. Persisted as int — Crm = 0 so every row that existed
    /// before this enum was introduced reads as Crm without a data migration.
    /// </summary>
    public enum RefundSource
    {
        /// <summary>Issued by an admin through the CRM refund flow. Has a RefundedByUserId.</summary>
        Crm = 0,

        /// <summary>
        /// Discovered by reconciling against Stripe — a refund issued in the Stripe Dashboard
        /// (or before the CRM refund feature existed). No admin, and no customer email is sent
        /// automatically, because the refund already happened and the customer may have been
        /// told elsewhere. An admin can send the confirmation manually from the refund history.
        /// </summary>
        Stripe = 1
    }
}
