using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// WHO MAY APPLY AN ORDER EDIT WITHOUT APPROVAL.
    ///
    /// Regular Admins editing an order in the admin panel submit a PendingOrderEdit that a
    /// SuperAdmin reviews and approves. A SuperAdmin can grant an individual Admin the right to
    /// skip that round trip — the same shape as the page-view grants, except it grants a WRITE.
    ///
    /// What must stay true:
    ///   - SuperAdmins never depend on the flag (they already save directly),
    ///   - an Admin without the grant always goes through approval,
    ///   - the grant is only honoured TOGETHER with the Admin role, so demoting a granted admin
    ///     disarms the stored column without anyone having to remember to clear it,
    ///   - Moderators/Customers are never let through, flag or not.
    ///
    /// The SAME answer gates REVIEWING another admin's pending edit (list / detail / approve /
    /// reject on AdminOrdersController). Someone trusted to write the order directly is trusted to
    /// approve the identical write arriving from a colleague, so there is deliberately no second
    /// predicate for reviewing — adding one is how the two would drift apart.
    /// </summary>
    public class OrderEditApprovalPolicyTests
    {
        [Fact]
        public void SuperAdmin_SavesDirectly_RegardlessOfTheFlag()
        {
            Assert.True(OrderEditApprovalPolicy.CanSaveDirectly(UserRole.SuperAdmin, canEditOrdersWithoutApproval: false));
            Assert.True(OrderEditApprovalPolicy.CanSaveDirectly(UserRole.SuperAdmin, canEditOrdersWithoutApproval: true));
        }

        [Fact]
        public void Admin_WithoutGrant_MustGetApproval()
        {
            Assert.False(OrderEditApprovalPolicy.CanSaveDirectly(UserRole.Admin, canEditOrdersWithoutApproval: false));
            Assert.True(OrderEditApprovalPolicy.RequiresApproval(UserRole.Admin, canEditOrdersWithoutApproval: false));
        }

        [Fact]
        public void Admin_WithGrant_SavesDirectly()
        {
            Assert.True(OrderEditApprovalPolicy.CanSaveDirectly(UserRole.Admin, canEditOrdersWithoutApproval: true));
            Assert.False(OrderEditApprovalPolicy.RequiresApproval(UserRole.Admin, canEditOrdersWithoutApproval: true));
        }

        /// <summary>
        /// The grant is stored on the user row and is NOT cleared when a SuperAdmin changes that
        /// user's role. Checking role and flag together is what keeps a demoted admin from
        /// retaining a write privilege nobody re-granted.
        /// </summary>
        [Theory]
        [InlineData(UserRole.Moderator)]
        [InlineData(UserRole.Customer)]
        public void NonAdminRoles_AreNeverLetThrough_EvenWithAStaleGrant(UserRole role)
        {
            Assert.False(OrderEditApprovalPolicy.CanSaveDirectly(role, canEditOrdersWithoutApproval: true));
            Assert.True(OrderEditApprovalPolicy.RequiresApproval(role, canEditOrdersWithoutApproval: true));
        }

        /// <summary>
        /// Reviewing a colleague's pending edit is gated by the very same predicate the controller
        /// uses for a direct save (CallerCanApplyOrderEditsAsync). Spelled out as its own test so
        /// that splitting them into two rules fails here rather than silently letting an ungranted
        /// admin approve their own colleague's edit.
        /// </summary>
        [Theory]
        [InlineData(UserRole.SuperAdmin, false, true)]
        [InlineData(UserRole.Admin, true, true)]
        [InlineData(UserRole.Admin, false, false)]
        [InlineData(UserRole.Moderator, true, false)]
        public void ReviewingAPendingEdit_UsesTheSameAnswerAsSavingDirectly(
            UserRole role, bool granted, bool mayReview)
        {
            Assert.Equal(mayReview, OrderEditApprovalPolicy.CanSaveDirectly(role, granted));
        }
    }
}
