using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers;

/// <summary>Commercial agreements do not consume or automatically apply residential loyalty.</summary>
public static class ResidentialLoyaltyPolicy
{
    public static bool AppliesTo(Order order) =>
        order.ContractClientId == null && order.PaymentMethod != PaymentMethod.Invoice;
}
