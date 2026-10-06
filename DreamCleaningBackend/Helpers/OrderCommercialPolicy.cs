namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "does a NEW order start out Regular or Commercial?" (2026-10).
    ///
    /// An order is commercial when it is billed or performed for a business: the customer's
    /// account carries the staff-set business flag (<c>User.IsBusiness</c>, which also links the
    /// account to its commercial client), or the order itself is billed to a commercial client
    /// or performed under a commercial contract. An admin booking in Admin Mode may override the
    /// default either way - that choice is the only client input this rule ever honours, and the
    /// caller is responsible for passing it ONLY for an admin request.
    ///
    /// The rule runs once, when the order is created. Afterwards <c>Order.IsCommercial</c> is
    /// changed only by an admin's explicit move, so a customer being flagged (or un-flagged) as a
    /// business later never silently re-files orders that already exist. The migration that
    /// introduced the column backfilled existing orders with this same rule (minus the admin
    /// choice, which did not exist yet).
    /// </summary>
    public static class OrderCommercialPolicy
    {
        public static bool ResolveForNewOrder(
            bool customerIsBusiness,
            int? contractClientId,
            int? contractId,
            bool? adminChoice)
        {
            if (adminChoice.HasValue) return adminChoice.Value;
            return customerIsBusiness || contractClientId.HasValue || contractId.HasValue;
        }
    }
}
