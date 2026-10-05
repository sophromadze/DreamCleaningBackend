using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddExtraServiceKey : Migration
    {
        /// <summary>
        /// Exact production extra names (GET api/booking/service-types, 2026-10-03) and the key each
        /// one gets. Matched BINARY - case, spacing and punctuation must be identical - so a database
        /// whose names differ (a local dev DB, Sweep It Real) simply gets no key for that row and the
        /// code falls back to its legacy name match until an admin sets one.
        /// </summary>
        private static readonly (string Name, string Key)[] KnownExtras =
        {
            ("Deep Cleaning", "deep-cleaning"),
            ("Same Day Service", "same-day"),
            ("Extra Cleaners", "extra-cleaners"),
            ("Extra Minutes", "extra-minutes"),
            ("Cleaning Supplies", "cleaning-supplies"),
            ("Cleaning Essentials", "cleaning-essentials"),
            ("Vacuum Cleaner", "vacuum-cleaner"),
            ("Pets", "pets"),
            ("Fridge", "fridge"),
            ("Oven", "oven"),
            ("Dishes", "dishes"),
            ("Kitchen Cabinets", "kitchen-cabinets"),
            ("Closets", "closets"),
            ("Baseboards", "baseboards"),
            ("Windows", "windows"),
            ("Walls", "walls"),
            ("Stairs", "stairs"),
            ("Folding / Organizing", "folding-organizing"),
            ("Laundry", "laundry"),
            ("Couches", "couches"),
            ("Ceiling Fan", "ceiling-fan"),
            ("Chandelier", "chandelier"),
            ("Balcony", "balcony"),
            ("Home Office", "home-office"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtraServiceKey",
                table: "ExtraServices",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraServices_ServiceTypeId_ExtraServiceKey",
                table: "ExtraServices",
                columns: new[] { "ServiceTypeId", "ExtraServiceKey" });

            // AFTER the composite index exists: the ServiceTypeId foreign key needs an index that
            // starts with its column, and MariaDB refuses to drop the old one while it is the only one.
            migrationBuilder.DropIndex(
                name: "IX_ExtraServices_ServiceTypeId",
                table: "ExtraServices");

            FillKnownExtraServiceKeys(migrationBuilder);
        }

        /// <summary>
        /// Gives the known extras their keys, under the same rule the admin form enforces
        /// (ExtraServiceKeyPolicy): no service type may SEE two rows with one key. A type sees its own
        /// rows plus the universal ones (IsAvailableForAll AND no ServiceTypeId), so:
        ///   - only rows whose key IS NULL are touched (idempotent; never overwrites an admin's key);
        ///   - per key, ONE row per scope (the universal scope, or one service type) is chosen -
        ///     active first, then the lowest Id - so an inactive duplicate never steals the key;
        ///   - a candidate is dropped when an existing keyed row already conflicts with it;
        ///   - universal rows are keyed first, and a type-specific candidate whose key a universal row
        ///     now holds is dropped.
        /// A name that does not exist matches nothing and nothing fails. Every string comparison is
        /// BINARY, so no collation mix between the helper tables and ExtraServices can arise.
        /// </summary>
        private static void FillKnownExtraServiceKeys(MigrationBuilder migrationBuilder)
        {
            const string scope =
                "CASE WHEN es.`IsAvailableForAll` = 1 AND es.`ServiceTypeId` IS NULL THEN -1 " +
                "ELSE COALESCE(es.`ServiceTypeId`, -2) END";

            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_ExtraServiceKeyFill`;");
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_ExtraServiceKeyPick`;");
            migrationBuilder.Sql(
                "CREATE TEMPORARY TABLE `_ExtraServiceKeyFill` (" +
                "`Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL, " +
                "`Key` varchar(50) CHARACTER SET utf8mb4 NOT NULL);");

            var values = string.Join(",\n", KnownExtras.Select(e =>
                $"('{e.Name.Replace("'", "''")}', '{e.Key}')"));
            migrationBuilder.Sql($"INSERT INTO `_ExtraServiceKeyFill` (`Name`, `Key`) VALUES\n{values};");

            // One candidate per (key, scope).
            migrationBuilder.Sql(
                "CREATE TEMPORARY TABLE `_ExtraServiceKeyPick` AS " +
                "SELECT `Id`, `Key`, `IsUniversal`, `ScopeId` FROM (" +
                "  SELECT es.`Id`, f.`Key`, " +
                "         (es.`IsAvailableForAll` = 1 AND es.`ServiceTypeId` IS NULL) AS `IsUniversal`, " +
                $"         {scope} AS `ScopeId`, " +
                $"         ROW_NUMBER() OVER (PARTITION BY f.`Key`, {scope} ORDER BY es.`IsActive` DESC, es.`Id`) AS `rn` " +
                "  FROM `ExtraServices` es " +
                "  JOIN `_ExtraServiceKeyFill` f ON BINARY es.`Name` = BINARY f.`Name` " +
                "  WHERE es.`ExtraServiceKey` IS NULL" +
                ") ranked WHERE `rn` = 1;");

            // Drop candidates an ALREADY-keyed row conflicts with (a re-run, or a key an admin set).
            migrationBuilder.Sql(
                "DELETE p FROM `_ExtraServiceKeyPick` p " +
                "JOIN `ExtraServices` es ON BINARY es.`ExtraServiceKey` = BINARY p.`Key` " +
                "WHERE p.`IsUniversal` = 1 " +
                "   OR (es.`IsAvailableForAll` = 1 AND es.`ServiceTypeId` IS NULL) " +
                "   OR es.`ServiceTypeId` = p.`ScopeId`;");

            // Universal rows first...
            migrationBuilder.Sql(
                "UPDATE `ExtraServices` es JOIN `_ExtraServiceKeyPick` p ON p.`Id` = es.`Id` " +
                "SET es.`ExtraServiceKey` = p.`Key` WHERE p.`IsUniversal` = 1;");

            // ...then type-specific rows, minus any whose key a universal row now holds.
            migrationBuilder.Sql(
                "DELETE p FROM `_ExtraServiceKeyPick` p " +
                "JOIN `ExtraServices` es ON BINARY es.`ExtraServiceKey` = BINARY p.`Key` " +
                "  AND es.`IsAvailableForAll` = 1 AND es.`ServiceTypeId` IS NULL " +
                "WHERE p.`IsUniversal` = 0;");

            migrationBuilder.Sql(
                "UPDATE `ExtraServices` es JOIN `_ExtraServiceKeyPick` p ON p.`Id` = es.`Id` " +
                "SET es.`ExtraServiceKey` = p.`Key` WHERE p.`IsUniversal` = 0;");

            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_ExtraServiceKeyPick`;");
            migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS `_ExtraServiceKeyFill`;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Single-column index first, for the same foreign-key reason as in Up.
            migrationBuilder.CreateIndex(
                name: "IX_ExtraServices_ServiceTypeId",
                table: "ExtraServices",
                column: "ServiceTypeId");

            migrationBuilder.DropIndex(
                name: "IX_ExtraServices_ServiceTypeId_ExtraServiceKey",
                table: "ExtraServices");

            migrationBuilder.DropColumn(
                name: "ExtraServiceKey",
                table: "ExtraServices");
        }
    }
}
