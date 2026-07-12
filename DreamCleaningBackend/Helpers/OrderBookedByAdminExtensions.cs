using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for the "booked by admin" flag of an order.
    /// BookedByAdminUserId is authoritative — create-for-user stamps it regardless of
    /// payment method — but the column only exists since 2026-07, so legacy orders fall
    /// back to the manual-payment recorder IF it was stamped at creation (create-for-user
    /// sets ManualPaymentRecordedAt with the same UtcNow as OrderDate). Admins also stamp
    /// the recorder when editing an existing order's payment method to cash/Zelle, and
    /// that must NOT flip a customer booking to admin-booked — hence the creation window.
    /// Only valid for in-memory Order instances (not EF IQueryable projections).
    /// </summary>
    public static class OrderBookedByAdminExtensions
    {
        public static bool IsBookedByAdmin(this Order order) =>
            order.BookedByAdminUserId != null ||
            (order.ManualPaymentRecordedByUserId != null &&
             order.ManualPaymentRecordedAt != null &&
             order.ManualPaymentRecordedAt <= order.OrderDate.AddMinutes(5));
    }
}
