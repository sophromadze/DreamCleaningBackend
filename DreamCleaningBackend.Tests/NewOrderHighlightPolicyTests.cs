using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE GREEN NEW-ORDER HIGHLIGHT IS PER ADMIN, AND IT EXPIRES.
    ///
    /// The two halves are load-bearing together, not separately. Per-admin state alone would
    /// have turned every order no individual admin had personally clicked green for them —
    /// which, on a system that had been tracking one shared acknowledgment row per order, is
    /// the entire order history. The 24-hour window from CreatedAt is what bounds it, and it
    /// is also what stops an order nobody opens nagging forever.
    ///
    /// "Now" is passed in everywhere so none of this depends on the clock.
    /// </summary>
    public class NewOrderHighlightPolicyTests
    {
        private static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

        // ── The window ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void AnOrderPlacedMomentsAgo_IsInsideTheWindow()
        {
            Assert.True(NewOrderHighlightPolicy.IsWithinWindow(Now.AddMinutes(-1), Now));
        }

        /// <summary>
        /// The boundary is inclusive at exactly 24h old and excludes anything past it. Stated
        /// explicitly because the controller writes the comparison out by hand — EF cannot
        /// translate the helper inside a bigger Where — so the two must agree on the edge.
        /// </summary>
        [Fact]
        public void TheBoundaryIsExactlyTwentyFourHours()
        {
            var exactly = Now.AddHours(-NewOrderHighlightPolicy.WindowHours);

            Assert.True(NewOrderHighlightPolicy.IsWithinWindow(exactly, Now));
            Assert.False(NewOrderHighlightPolicy.IsWithinWindow(exactly.AddSeconds(-1), Now));
            Assert.Equal(exactly, NewOrderHighlightPolicy.CutoffUtc(Now));
        }

        /// <summary>
        /// The case the window exists for: an order nobody ever opened. Under the old shared
        /// acknowledgment there was no row for it and it stayed green indefinitely; per-admin
        /// it would have stayed green indefinitely for EVERY admin. It ages out instead.
        /// </summary>
        [Fact]
        public void AnOrderNobodyEverOpened_StopsBeingNewOnceTheWindowPasses()
        {
            Assert.False(NewOrderHighlightPolicy.IsWithinWindow(Now.AddDays(-3), Now));
            Assert.False(NewOrderHighlightPolicy.IsWithinWindow(Now.AddYears(-1), Now));
        }

        /// <summary>
        /// The window is anchored to CreatedAt, NOT to the first admin's view. One admin opening
        /// an order must not extend or restart anybody else's green — the policy never sees a
        /// view timestamp at all, which is what makes that impossible to get wrong later.
        /// </summary>
        [Fact]
        public void TheWindowIsAnchoredToCreation_NotToWhenSomebodyFirstLooked()
        {
            var createdAt = Now.AddHours(-23);

            // Same answer however long ago the first view was — there is no view input.
            Assert.True(NewOrderHighlightPolicy.IsWithinWindow(createdAt, Now));
            Assert.False(NewOrderHighlightPolicy.IsWithinWindow(createdAt, Now.AddHours(2)));
        }

        // ── The audience ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void AdminsAndSuperAdmins_SeeTheIndicator()
        {
            Assert.True(NewOrderHighlightPolicy.ShowsIndicator(UserRole.Admin));
            Assert.True(NewOrderHighlightPolicy.ShowsIndicator(UserRole.SuperAdmin));
        }

        /// <summary>
        /// Moderators hold Permission.View, which is what the endpoint is gated on, so they
        /// would have picked the indicator up as a side effect of the gate rather than by
        /// anyone deciding they should have it. The audience is a ROLE question (owner's call,
        /// 2026-09) and is answered here rather than by the permission attribute.
        /// </summary>
        [Fact]
        public void Moderators_DoNot()
        {
            Assert.False(NewOrderHighlightPolicy.ShowsIndicator(UserRole.Moderator));
            Assert.False(NewOrderHighlightPolicy.ShowsIndicator(UserRole.Customer));
        }

        /// <summary>
        /// The frontend mirrors this constant as NEW_ORDER_WINDOW_MS. Pinned so a change on one
        /// side fails here rather than showing two different windows to the same admin.
        /// </summary>
        [Fact]
        public void TheWindowIsTwentyFourHours()
        {
            Assert.Equal(24, NewOrderHighlightPolicy.WindowHours);
        }
    }
}
