namespace DreamCleaningBackend.Models
{
    // Tracks how an order was paid. Default `Normal` (0) preserves the pre-existing
    // Stripe / IsPaid flow exactly — no behavioral change for existing data. Anything
    // else means the order was paid outside Stripe and IsPaid stays false.
    public enum PaymentMethod
    {
        Normal = 0,   // Stripe — uses existing IsPaid flow (default)
        Cash = 1,
        Zelle = 2,
        Check = 3,
        Other = 4
    }
}
