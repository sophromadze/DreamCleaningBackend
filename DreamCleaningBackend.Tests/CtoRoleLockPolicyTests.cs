using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE CTO'S SUPERADMIN ROLE IS NOT REMOVABLE BY ANYBODY.
    ///
    /// The rule takes no requester argument at all, which is the point being asserted here: there
    /// is no caller — not another SuperAdmin, not the CEO, not the CTO themselves — for whom it
    /// answers differently. See <see cref="CtoRoleLockPolicy"/> for why the way out is the officer
    /// title rather than an override.
    /// </summary>
    public class CtoRoleLockPolicyTests
    {
        [Theory]
        [InlineData(UserRole.Customer)]
        [InlineData(UserRole.Admin)]
        [InlineData(UserRole.Moderator)]
        [InlineData(UserRole.Cleaner)]
        public void ACtoCannotBeMovedOffSuperAdmin_WhateverTheTargetRole(UserRole newRole)
        {
            Assert.True(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.CTO, UserRole.SuperAdmin, newRole));
        }

        [Fact]
        public void ResavingSuperAdminIsAllowed()
        {
            // The Users-tab edit form posts the role on every save, changed or not. Refusing an
            // unchanged value would make an ordinary name or phone edit on the CTO's own account
            // impossible.
            Assert.False(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.CTO, UserRole.SuperAdmin, UserRole.SuperAdmin));
        }

        [Fact]
        public void TheLockIsTheTitle_NotTheRole()
        {
            // An untitled SuperAdmin, and a CEO, are both demotable exactly as before. Only CTO
            // carries the lock, because only CTO governs who may hold a title at all.
            Assert.False(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.None, UserRole.SuperAdmin, UserRole.Admin));
            Assert.False(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.CEO, UserRole.SuperAdmin, UserRole.Admin));
        }

        [Fact]
        public void ACtoOnTheAdminRoleIsNotCovered()
        {
            // There is no SuperAdmin to protect, and locking an ordinary Admin's role would be a
            // surprise nobody asked for. Deliberately out of scope.
            Assert.False(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.CTO, UserRole.Admin, UserRole.Customer));
        }

        [Fact]
        public void ClearingTheTitleIsWhatUnlocksTheRole()
        {
            // The documented way out, in one place, so a future "SuperAdmin override" branch has
            // something to fail against: the same demotion that is refused while the title is held
            // is permitted the moment it is not.
            Assert.True(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.CTO, UserRole.SuperAdmin, UserRole.Admin));
            Assert.False(CtoRoleLockPolicy.IsRoleChangeLocked(
                OrgTitle.None, UserRole.SuperAdmin, UserRole.Admin));
        }
    }
}
