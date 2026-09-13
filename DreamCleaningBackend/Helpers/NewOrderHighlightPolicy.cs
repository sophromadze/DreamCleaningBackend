using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "does this order still show the green new-order highlight?".
    ///
    /// The highlight is PER ADMIN (2026-09). It used to be one shared row in
    /// OrderReminderAcknowledgments — whoever opened the order first cleared the green for
    /// everybody, so an admin who had never seen a booking had no way of knowing it existed.
    /// The acknowledgment row already recorded WHO acknowledged; the query simply never
    /// filtered on it. Each admin now gets their own row and clears only their own green.
    ///
    /// Per-admin state on its own would have been a data problem: every order nobody had ever
    /// acknowledged would have turned green for every admin at once, including years of
    /// history. The 24-hour window is what bounds it — "new" means "placed in the last day",
    /// measured from <see cref="Order.CreatedAt"/>, so nothing older than the window can go
    /// green no matter what the acknowledgment table holds. That also means an order nobody
    /// opens stops being green on its own rather than nagging forever.
    ///
    /// EF cannot translate these helpers inside a bigger Where, so the unviewed-orders query
    /// writes the cutoff comparison out by hand against <see cref="CutoffUtc"/>.
    /// </summary>
    public static class NewOrderHighlightPolicy
    {
        /// <summary>How long an order counts as "new", measured from CreatedAt.</summary>
        public const int WindowHours = 24;

        /// <summary>Orders created before this instant can never be highlighted.</summary>
        public static DateTime CutoffUtc(DateTime nowUtc) => nowUtc.AddHours(-WindowHours);

        /// <summary>True while the order is still inside the new-order window.</summary>
        public static bool IsWithinWindow(DateTime createdAtUtc, DateTime nowUtc) =>
            createdAtUtc >= CutoffUtc(nowUtc);

        /// <summary>
        /// Who the highlight is for. Moderators are deliberately excluded (owner's call,
        /// 2026-09): they hold Permission.View and so would otherwise have been handed the
        /// indicator as a side effect of it being permission-gated rather than role-gated.
        /// The indicator says "somebody needs to action this booking", which is not their job.
        /// </summary>
        public static bool ShowsIndicator(UserRole role) =>
            role == UserRole.Admin || role == UserRole.SuperAdmin;
    }
}
