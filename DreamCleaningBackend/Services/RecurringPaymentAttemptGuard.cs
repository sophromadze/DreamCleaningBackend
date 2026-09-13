using DreamCleaningBackend.Data;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stripe;

namespace DreamCleaningBackend.Services;

/// <summary>Serializes individual/combined payment preparation for the same customer across processes.</summary>
public static class RecurringPaymentAttemptGuard
{
    public static async Task<IDbContextTransaction?> LockAsync(ApplicationDbContext db, int userId)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction != null) return null;
        var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        try {
            await db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Id = {userId} FOR UPDATE")
                .AsNoTracking().ToListAsync();
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public static bool IsSubmitted(string? status) => status is "processing" or "requires_capture" or "succeeded";

    /// <summary>The old client secret must become unusable BEFORE a replacement is issued.</summary>
    public static async Task CancelOpenAsync(IStripeService stripe, string intentId)
    {
        var intent = await stripe.GetPaymentIntentAsync(intentId);
        if (IsSubmitted(intent.Status)) throw new CombinedPaymentException("A payment for this cleaning is already being processed or has succeeded. Refresh the page.");
        if (intent.Status == "canceled") return;
        if (intent.Status is not ("requires_payment_method" or "requires_confirmation" or "requires_action"))
            throw new CombinedPaymentException("Could not verify the previous payment. Please try again.");
        // Stripe arbitrates a simultaneous confirmation: a cancellation failure never releases the orders.
        var canceled = await stripe.CancelPaymentIntentAsync(intentId);
        if (canceled.Status != "canceled") throw new CombinedPaymentException("The previous payment could not be safely replaced. Refresh the page.");
    }
}
