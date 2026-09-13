using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <summary>
    /// ONE-TIME RETROACTIVE PASS for the cleaner portal: every account that already existed when
    /// the portal shipped and whose email is on a cleaner record becomes a Cleaner-role account,
    /// linked to that record.
    ///
    /// Without it the feature only works for people who register from today onward, and every
    /// cleaner who already had a customer account would have to be found and promoted by hand.
    ///
    /// Four deliberate constraints, each of which is a bug if dropped:
    ///
    ///  • ONLY <c>Role = 0</c> (Customer) is touched. A SuperAdmin, Admin or Moderator whose
    ///    address also sits on a cleaner row - an owner who cleans, an admin who was a cleaner
    ///    first - must NEVER be demoted into a read-only portal by a migration. That is an outage
    ///    of the admin panel with no visible cause, and it is exactly the kind of thing a
    ///    backfill is tempted to do quietly. Mirrors
    ///    Helpers/CleanerAccountLink.CanAutoAssignCleanerRole, which enforces the same rule at
    ///    runtime.
    ///  • Soft-deleted accounts (<c>IsDeleted</c>, from the account-merge flow) are skipped -
    ///    they are the losing half of a merge and nobody signs in with them.
    ///  • No-email placeholder addresses (<c>@no-email.invalid</c>, see NoEmailHelper) can never
    ///    match. They are generated per account, so a match would mean a cleaner row is carrying
    ///    a placeholder, and treating that as an identity would attach the wrong person.
    ///  • The LINK is written as well as the role. Cleaner.UserId is what the portal resolves
    ///    through; leaving it null would make every backfilled account fall through to the email
    ///    fallback forever, and that fallback breaks the moment somebody corrects a typo.
    ///
    /// The email match is case-insensitive by collation (MySQL's default is case-insensitive) and
    /// LOWER() on both sides regardless, so a mixed-case address recorded years ago still matches.
    ///
    /// Contested emails: if two cleaner rows share an address (a duplicate nobody has merged),
    /// the LOWEST cleaner id wins - the same tie-break LeadCustomerMatcher uses, chosen because
    /// it is stable across re-runs rather than because either row is more correct. The unique
    /// index on Cleaners.UserId makes the alternative impossible anyway.
    ///
    /// Raw SQL because it is a set-based UPDATE over a join. Both statements are re-runnable.
    /// </summary>
    public partial class BackfillCleanerRoleFromCleanerEmails : Migration
    {
        // UserRole.Cleaner / UserRole.Customer as stored. Written as literals because a migration
        // is a historical record: it must keep doing what it did on the day it ran, even if the
        // enum is renumbered later.
        private const int CleanerRole = 4;
        private const int CustomerRole = 0;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1 - promote. Runs first so step 2 can link only rows that are actually cleaners.
            migrationBuilder.Sql($@"
                UPDATE `Users` u
                SET u.`Role` = {CleanerRole},
                    u.`UpdatedAt` = UTC_TIMESTAMP()
                WHERE u.`Role` = {CustomerRole}
                  AND u.`IsDeleted` = 0
                  AND u.`Email` IS NOT NULL
                  AND u.`Email` NOT LIKE '%@no-email.invalid'
                  AND EXISTS (
                        SELECT 1 FROM `Cleaners` c
                        WHERE c.`Email` IS NOT NULL
                          AND LOWER(c.`Email`) = LOWER(u.`Email`)
                  );
            ");

            // Step 2 - link. Only unclaimed cleaner rows, and only the lowest-id row per address,
            // so the unique index on Cleaners.UserId cannot be violated by a duplicated cleaner.
            migrationBuilder.Sql($@"
                UPDATE `Cleaners` c
                INNER JOIN `Users` u
                    ON u.`Role` = {CleanerRole}
                   AND u.`IsDeleted` = 0
                   AND LOWER(u.`Email`) = LOWER(c.`Email`)
                SET c.`UserId` = u.`Id`,
                    c.`UpdatedAt` = UTC_TIMESTAMP()
                WHERE c.`UserId` IS NULL
                  AND c.`Email` IS NOT NULL
                  AND c.`Id` = (
                        SELECT MIN(c2.`Id`) FROM (SELECT `Id`, `Email` FROM `Cleaners`) c2
                        WHERE LOWER(c2.`Email`) = LOWER(c.`Email`)
                  );
            ");
        }

        /// <summary>
        /// Reverses only what this migration can be sure it did: accounts that are on the Cleaner
        /// role AND still linked to a cleaner record go back to Customer, and the link is dropped.
        ///
        /// A Cleaner-role account with no link is deliberately LEFT ALONE - it was either promoted
        /// by an admin from the Cleaners tab or unlinked there on purpose, and demoting it
        /// here would undo somebody's deliberate decision along with the backfill.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                UPDATE `Users` u
                INNER JOIN `Cleaners` c ON c.`UserId` = u.`Id`
                SET u.`Role` = {CustomerRole},
                    u.`UpdatedAt` = UTC_TIMESTAMP()
                WHERE u.`Role` = {CleanerRole};
            ");

            migrationBuilder.Sql(@"
                UPDATE `Cleaners`
                SET `UserId` = NULL,
                    `UpdatedAt` = UTC_TIMESTAMP()
                WHERE `UserId` IS NOT NULL;
            ");
        }
    }
}
