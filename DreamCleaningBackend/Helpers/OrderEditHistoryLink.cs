using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// "WHICH UPDATE-HISTORY ROW DID THIS ORDER EDIT WRITE?" — so undoing the edit from the Audits
    /// tab can take the row with it (2026-09).
    ///
    /// An admin order save writes two records in the same request: the
    /// <see cref="OrderUpdateHistory"/> row (what the Update History panel and every "still owed"
    /// surface read) and, just after it, the Order audit row. Undo used to revert the ORDER and
    /// leave the history row behind — order #386: tips +$270, undone, and the panel kept showing
    /// an "Unpaid +$270.00" top-up for a price the order no longer had.
    ///
    /// There is no foreign key between the two (the audit table is generic), so the link is the
    /// edit's own fingerprint: same order, the row's Original/New totals equal the audit row's
    /// before/after Total, and written moments before the audit row. When several rows qualify
    /// the one nearest in time wins, so two same-priced edits minutes apart can never both go.
    /// </summary>
    public static class OrderEditHistoryLink
    {
        /// <summary>The history row is written BEFORE the audit row (same request), so the window
        /// is mostly backwards. Generous because a slow save still has to match.</summary>
        public static readonly TimeSpan WindowBefore = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan WindowAfter = TimeSpan.FromMinutes(1);

        public static bool Matches(OrderUpdateHistory row, int orderId, DateTime auditCreatedAt,
            decimal totalBefore, decimal totalAfter) =>
            row.OrderId == orderId
            && row.UpdatedAt >= auditCreatedAt - WindowBefore
            && row.UpdatedAt <= auditCreatedAt + WindowAfter
            && row.OriginalTotal == totalBefore
            && row.NewTotal == totalAfter;

        /// <summary>The row written by the edit the audit row describes, or null when none.</summary>
        public static OrderUpdateHistory? Find(IEnumerable<OrderUpdateHistory> rows, int orderId,
            DateTime auditCreatedAt, decimal totalBefore, decimal totalAfter) =>
            rows.Where(r => Matches(r, orderId, auditCreatedAt, totalBefore, totalAfter))
                .OrderBy(r => Math.Abs((r.UpdatedAt - auditCreatedAt).Ticks))
                .FirstOrDefault();

        /// <summary>
        /// True when the row represents money the customer actually handed over. Such an edit is
        /// NOT undone silently: reverting the price would leave a real payment attached to nothing,
        /// so the admin has to refund it first. A row stamped paid only because it owed nothing
        /// (a decrease, or a duration-only edit) is not money and does not block.
        /// </summary>
        public static bool HasCollectedMoney(OrderUpdateHistory row) =>
            row.IsPaid && row.AdditionalAmount > OrderAdditionalCharge.MinimumCollectableAmount;

        /// <summary>
        /// The row a REDO re-creates: the same shape the admin save writes, stamped at the
        /// original edit's time so it sits where it always sat in the history.
        /// </summary>
        public static OrderUpdateHistory Rebuild(Order before, Order after, int updatedByUserId, DateTime editedAt)
        {
            var additional = after.Total - before.Total;
            if (Math.Abs(additional) < 0.01m) additional = 0m;
            var collectable = OrderAdditionalCharge.Collectable(additional);

            return new OrderUpdateHistory
            {
                OrderId = after.Id,
                UpdatedByUserId = updatedByUserId,
                UpdatedAt = editedAt,
                OriginalSubTotal = before.SubTotal,
                OriginalTax = before.Tax,
                OriginalTips = before.Tips,
                OriginalCompanyDevelopmentTips = before.CompanyDevelopmentTips,
                OriginalTotal = before.Total,
                NewSubTotal = after.SubTotal,
                NewTax = after.Tax,
                NewTips = after.Tips,
                NewCompanyDevelopmentTips = after.CompanyDevelopmentTips,
                NewTotal = after.Total,
                AdditionalAmount = collectable,
                IsPaid = collectable <= OrderAdditionalCharge.MinimumCollectableAmount
            };
        }
    }
}
