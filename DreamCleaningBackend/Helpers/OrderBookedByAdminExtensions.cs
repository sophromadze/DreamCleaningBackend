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
            IsBookedByAdmin(
                order.BookedByAdminUserId,
                order.ManualPaymentRecordedByUserId,
                order.ManualPaymentRecordedAt,
                order.OrderDate);

        /// <summary>Same rule against the raw fields — for callers that project just these columns
        /// (e.g. the CRM Ads breakdown) instead of materializing full Order instances.</summary>
        public static bool IsBookedByAdmin(
            int? bookedByAdminUserId,
            int? manualPaymentRecordedByUserId,
            DateTime? manualPaymentRecordedAt,
            DateTime orderDate) =>
            bookedByAdminUserId != null ||
            (manualPaymentRecordedByUserId != null &&
             manualPaymentRecordedAt != null &&
             manualPaymentRecordedAt <= orderDate.AddMinutes(5));
    }
}
