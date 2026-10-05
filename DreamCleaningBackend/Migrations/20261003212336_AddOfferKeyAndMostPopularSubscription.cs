using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOfferKeyAndMostPopularSubscription : Migration
    {
        /// <summary>
        /// Exact production names (GET api/special-offers/public and api/booking/subscriptions,
        /// 2026-10-04). Matched BINARY - case, spacing and punctuation must be identical - so a
        /// database whose names differ (a local dev DB, Sweep It Real) simply gets nothing here and
        /// the code falls back to its legacy rule until an admin sets the key / ticks the badge.
        /// </summary>
        private const string FirstTimeOfferName = "First Time Customer";
        private const string FirstTimeOfferKey = "first-time";
        private const string MostPopularSubscriptionName = "Monthly";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMostPopular",
                table: "Subscriptions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OfferKey",
                table: "SpecialOffers",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SpecialOffers_OfferKey",
                table: "SpecialOffers",
                column: "OfferKey",
                unique: true);

            FillFirstTimeOfferKey(migrationBuilder);
            FillMostPopularSubscription(migrationBuilder);
        }

        /// <summary>
        /// Keys the first-time offer: ONE row named exactly "First Time Customer" whose key IS NULL -
        /// active first, then the lowest Id - and only while no row holds "first-time" yet (a re-run,
        /// or a key an admin already set, is left alone; the unique index could not take a second
        /// one anyway). A missing name matches nothing and nothing fails.
        /// </summary>
        private static void FillFirstTimeOfferKey(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_OfferKeyPick`;");
            migrationBuilder.Sql(
                "CREATE TEMPORARY TABLE `_OfferKeyPick` AS " +
                "SELECT `Id` FROM `SpecialOffers` " +
                $"WHERE `OfferKey` IS NULL AND BINARY `Name` = BINARY '{FirstTimeOfferName}' " +
                "ORDER BY `IsActive` DESC, `Id` LIMIT 1;");
            migrationBuilder.Sql(
                "DELETE FROM `_OfferKeyPick` WHERE EXISTS (" +
                $"SELECT 1 FROM `SpecialOffers` WHERE BINARY `OfferKey` = BINARY '{FirstTimeOfferKey}');");
            migrationBuilder.Sql(
                "UPDATE `SpecialOffers` so JOIN `_OfferKeyPick` p ON p.`Id` = so.`Id` " +
                $"SET so.`OfferKey` = '{FirstTimeOfferKey}';");
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_OfferKeyPick`;");
        }

        /// <summary>
        /// Moves the booking page's hard-coded "Most popular" badge (subscription.name === 'Monthly')
        /// onto the flag: ONE plan named exactly "Monthly" - active first, then the lowest Id - and
        /// only while no plan holds the badge yet (at most one may). A missing name matches nothing.
        /// </summary>
        private static void FillMostPopularSubscription(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_MostPopularPick`;");
            migrationBuilder.Sql(
                "CREATE TEMPORARY TABLE `_MostPopularPick` AS " +
                "SELECT `Id` FROM `Subscriptions` " +
                $"WHERE BINARY `Name` = BINARY '{MostPopularSubscriptionName}' " +
                "ORDER BY `IsActive` DESC, `Id` LIMIT 1;");
            migrationBuilder.Sql(
                "DELETE FROM `_MostPopularPick` WHERE EXISTS (" +
                "SELECT 1 FROM `Subscriptions` WHERE `IsMostPopular` = 1);");
            migrationBuilder.Sql(
                "UPDATE `Subscriptions` s JOIN `_MostPopularPick` p ON p.`Id` = s.`Id` " +
                "SET s.`IsMostPopular` = 1;");
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_MostPopularPick`;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpecialOffers_OfferKey",
                table: "SpecialOffers");

            migrationBuilder.DropColumn(
                name: "IsMostPopular",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "OfferKey",
                table: "SpecialOffers");
        }
    }
}
