using DreamCleaningBackend.Helpers;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    // A salary expense names a PERSON, and that person can be renamed, demoted or deleted while the
    // record of what they were paid has to stay put. These are the rules that make that work; they
    // are pure, so they are asserted without a database.
    public class SalaryExpenseRulesTests
    {
        [Fact]
        public void SalariesCategory_IsMatchedById_NotByName()
        {
            // The category is renameable ("Payroll"), so the Id is the contract. 4 is the seeded
            // value and must not move — every existing row's Category column already holds it.
            Assert.Equal(4, SalaryExpenseRules.SalariesCategoryId);
            Assert.True(SalaryExpenseRules.IsSalaryCategory(4));
            Assert.False(SalaryExpenseRules.IsSalaryCategory(0));
            Assert.False(SalaryExpenseRules.IsSalaryCategory(5));
        }

        [Fact]
        public void ADeletedStaffMembersRowKeepsTheNameItWasSavedWith()
        {
            // The whole point of the snapshot: no live name to resolve, so the stored one stands.
            Assert.Equal("Nino Beridze", SalaryExpenseRules.ResolveDisplayName("Nino Beridze", null));
            Assert.Equal("Nino Beridze", SalaryExpenseRules.ResolveDisplayName("Nino Beridze", "   "));
        }

        [Fact]
        public void ALiveStaffMemberIsShownUnderTheirCurrentName()
        {
            // Correcting a typo in an admin's surname fixes every salary row they appear on,
            // instead of leaving the old spelling frozen on the historic ones.
            Assert.Equal("Nino Beridze", SalaryExpenseRules.ResolveDisplayName("Nino Berdize", "Nino Beridze"));
        }

        [Fact]
        public void StaffRowsGroupByPerson_SoARenameDoesNotSplitThemInTwo()
        {
            var beforeRename = SalaryExpenseRules.GroupingKey(7, "Nino Berdize");
            var afterRename = SalaryExpenseRules.GroupingKey(7, "Nino Beridze");
            Assert.Equal(beforeRename, afterRename);
        }

        [Fact]
        public void TwoStaffMembersSharingAName_StayOnSeparateLines()
        {
            Assert.NotEqual(
                SalaryExpenseRules.GroupingKey(7, "Nino Beridze"),
                SalaryExpenseRules.GroupingKey(8, "Nino Beridze"));
        }

        [Fact]
        public void RowsWithNoStaffLink_StillGroupByNameCaseInsensitively()
        {
            // Unchanged behaviour for every other category — and for salaries typed by hand.
            Assert.Equal(
                SalaryExpenseRules.GroupingKey(null, "Office rent"),
                SalaryExpenseRules.GroupingKey(null, "  office RENT "));
        }

        [Fact]
        public void AStaffLinkedRowNeverCollidesWithATypedNameThatLooksLikeOne()
        {
            // Somebody typing "staff#7" by hand must not land in staff member 7's line.
            Assert.NotEqual(
                SalaryExpenseRules.GroupingKey(7, "Nino Beridze"),
                SalaryExpenseRules.GroupingKey(null, "staff#7"));
        }

        [Theory]
        [InlineData("Nino", "Beridze", "Nino Beridze")]
        [InlineData("  Nino  ", "  Beridze  ", "Nino Beridze")]
        [InlineData("Nino", "", "Nino")]
        [InlineData("", "Beridze", "Beridze")]
        [InlineData(null, null, "")]
        public void FormatStaffName_CollapsesToWhicheverHalfExists(string? first, string? last, string expected)
        {
            Assert.Equal(expected, SalaryExpenseRules.FormatStaffName(first, last));
        }
    }
}
