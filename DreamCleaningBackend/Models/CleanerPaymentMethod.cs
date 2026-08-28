namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// How a cleaner is paid their wages. Distinct from <see cref="PaymentMethod"/>, which is how
    /// a CUSTOMER paid us — the two enums travel in opposite directions and must not be shared.
    ///
    /// Nullable everywhere it is used: the cleaners dashboard collects it when it is known and
    /// leaves it blank otherwise, and the Outgoing Payments page only shows it as a hint for
    /// whoever is sending the money.
    /// </summary>
    public enum CleanerPaymentMethod
    {
        Zelle = 1,
        Cash = 2,
        Check = 3,
        Other = 4
    }
}
