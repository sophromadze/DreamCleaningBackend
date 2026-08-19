using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <summary>
    /// Property type (apartment vs house) on the order, plus the "Levels" service that prices
    /// stairs.
    ///
    /// The two Order columns are ordinary nullable adds. Legacy orders are deliberately NOT
    /// backfilled: null means "this order predates the feature", and every read surface renders
    /// nothing rather than an empty field.
    ///
    /// The catalog rows are seeded with raw SQL rather than HasData ON PURPOSE, for two separate
    /// reasons:
    ///
    ///   1. Services already runs past Id 14 in production with admin-created rows above the
    ///      seeded 1-5, so a HasData insert at a fixed Id would collide. Letting MySQL assign the
    ///      Id also keeps these rows out of the EF model snapshot, which is one less source of
    ///      the phantom seed drift that shows up on every generated migration in this project.
    ///
    ///   2. The TARGET service types cannot be named by Id or by Name. Both diverge between the
    ///      local and production databases:
    ///
    ///        local : 1 Residential | 2 Office | 4 Custom | 7 Filthy | 9 Pre-Arranged
    ///                15 Move in/out | 16 Heavy Conditional
    ///        prod  : 1 Residential | 2 Office | 3 Custom | 4 Move in/out | 5 Filthy Cleaning
    ///                6 Post Construction | 7 Pre-arranged | 8 Heavy Condition
    ///
    ///      An earlier version of this migration hardcoded { 1, 4 }, which happened to be right
    ///      in production and put Levels on Custom Cleaning locally. Names are no safer: they
    ///      differ in wording and casing across the two databases ("Filthy" vs "Filthy Cleaning",
    ///      "Heavy Conditional" vs "Heavy Condition"). ServiceKey is the only stable identifier,
    ///      which is also why the pricing export/import matches on it.
    ///
    /// Every statement is guarded and re-runnable. Database.Migrate() runs at application
    /// startup, so a statement that threw on a second pass would stop the API from booting.
    /// </summary>
    public partial class AddOrderPropertyTypeAndLevelsService : Migration
    {
        private const string LevelsCost = "35.00";
        private const string LevelsMinutes = "25";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PropertyType",
                table: "Orders",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "LevelsQuantity",
                table: "Orders",
                type: "int",
                nullable: true);

            SeedLevelsServices(migrationBuilder);
            SeedLevelsThresholds(migrationBuilder);
        }

        /// <summary>
        /// The Levels service row, seeded onto EVERY service type that already has an active
        /// bedrooms service.
        ///
        /// That predicate IS the definition of a room-priced service type: if it prices bedrooms
        /// it prices rooms, and stairs apply. Hourly (cleaner x hours) types are excluded by it
        /// automatically and must be, because stair time is already inside the hours the customer
        /// buys - charging per level on top would double-charge. Types with no room inputs are
        /// excluded because there is nothing to attach a level count to.
        ///
        /// Self-correcting: it resolves to Residential + Move in/out on both databases today, and
        /// a room-based service type added later picks Levels up without another migration.
        ///
        /// DisplayOrder is 4, NOT 0. A zero would be rewritten to 1 the first time an admin saved
        /// any edit to this row, because the admin panel sends displayOrder: service.displayOrder
        /// || 1, and a 1 collides with Bedrooms and cascades a reorder onto three unrelated rows.
        /// A price edit must not silently reshuffle the catalog. 4 sorts after Sq.ft in the admin
        /// Booking Services list, which is the only list this value affects: on the booking page
        /// Levels is rendered by its own gated block and filtered out of the generic loop.
        ///
        /// ZeroQuantityCost and ZeroQuantityDuration MUST stay NULL. The calculator's zero-quantity
        /// branch is generic - it fires for ANY service with a non-null value in either column, and
        /// only falls through to the bedrooms-keyed studio branch when both are null. A levels row
        /// with a zero-quantity cost configured would be priced by the studio rule instead.
        ///
        /// The whole SELECT sits inside a derived table so MySQL materialises it before the
        /// INSERT. Referencing the insert target directly in a subquery is what raises error
        /// 1093, and this migration runs at startup where a failure means the API does not boot.
        /// </summary>
        private static void SeedLevelsServices(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "INSERT INTO `Services` " +
                "(`Name`, `ServiceKey`, `Cost`, `TimeDuration`, `ServiceTypeId`, `InputType`, " +
                " `MinValue`, `MaxValue`, `StepValue`, `IsRangeInput`, `Unit`, " +
                " `ServiceRelationType`, `ChargeAboveThreshold`, `ZeroQuantityCost`, " +
                " `ZeroQuantityDuration`, `IsActive`, `DisplayOrder`, `CreatedAt`) " +
                "SELECT 'Levels', 'levels', " + LevelsCost + ", " + LevelsMinutes + ", " +
                "       `target`.`ServiceTypeId`, 'dropdown', " +
                "       1, 4, 1, 0, NULL, " +
                "       NULL, 1, NULL, " +
                "       NULL, 1, 4, UTC_TIMESTAMP() " +
                "FROM (" +
                "    SELECT DISTINCT `bedrooms`.`ServiceTypeId` " +
                "    FROM `Services` AS `bedrooms` " +
                "    WHERE `bedrooms`.`ServiceKey` = 'bedrooms' " +
                "      AND `bedrooms`.`IsActive` = 1 " +
                "      AND NOT EXISTS (" +
                "          SELECT 1 FROM `Services` AS `existing` " +
                "          WHERE `existing`.`ServiceKey` = 'levels' " +
                "            AND `existing`.`ServiceTypeId` = `bedrooms`.`ServiceTypeId`" +
                "      )" +
                ") AS `target`;");
        }

        /// <summary>
        /// The SELF-REFERENCING threshold row that makes the first level free, one per levels
        /// service created above.
        ///
        /// ServiceId and SourceServiceId are both the levels service. The calculator resolves an
        /// allowance by reading the source service's quantity straight out of the same selection
        /// array, so pointing a service at itself simply means "your allowance is a function of
        /// your own quantity". Neither the C# nor the TypeScript resolver recurses, so a
        /// self-reference cannot loop; the regression tests assert that contract directly rather
        /// than leaving it as an accident of the current implementation.
        ///
        /// One row at SourceQuantity 1 is enough because the lookup is a FLOOR match: every level
        /// count from 0 to 4 resolves to this row, giving billable = quantity - 1, which is
        /// $0 / $35 / $70 / $105 for 1 / 2 / 3 / 4 levels. A single-level house therefore costs
        /// exactly what the equivalent apartment costs.
        ///
        /// The allowance is data, so an admin can later decide two levels are included without a
        /// deploy - the Booking Services screen already has full threshold CRUD.
        ///
        /// Driven off the levels services themselves rather than off the bedrooms lookup again,
        /// so it stays correct even if the two statements are ever run out of step.
        /// </summary>
        private static void SeedLevelsThresholds(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "INSERT INTO `ServiceThresholds` " +
                "(`ServiceId`, `SourceServiceId`, `SourceQuantity`, `IncludedQuantity`, `CreatedAt`) " +
                "SELECT `target`.`Id`, `target`.`Id`, 1, 1, UTC_TIMESTAMP() " +
                "FROM (" +
                "    SELECT `levels`.`Id` " +
                "    FROM `Services` AS `levels` " +
                "    WHERE `levels`.`ServiceKey` = 'levels' " +
                "      AND NOT EXISTS (" +
                "          SELECT 1 FROM `ServiceThresholds` AS `existing` " +
                "          WHERE `existing`.`ServiceId` = `levels`.`Id` " +
                "            AND `existing`.`SourceServiceId` = `levels`.`Id`" +
                "      )" +
                ") AS `target`;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Thresholds first: ServiceThreshold.SourceServiceId is delete-restricted, so the
            // self-referencing row has to go before the service it points at.
            migrationBuilder.Sql(
                "DELETE `t` FROM `ServiceThresholds` AS `t` " +
                "INNER JOIN `Services` AS `s` ON `s`.`Id` = `t`.`ServiceId` " +
                "WHERE `s`.`ServiceKey` = 'levels';");

            // An OrderServices row referencing a levels service would block the delete. Any order
            // that recorded levels is a real order, so this reverses the catalog seed only while
            // nothing has used it yet, and leaves the row in place otherwise rather than
            // destroying order history.
            migrationBuilder.Sql(
                "DELETE `s` FROM `Services` AS `s` " +
                "WHERE `s`.`ServiceKey` = 'levels' " +
                "  AND NOT EXISTS (SELECT 1 FROM `OrderServices` AS `os` WHERE `os`.`ServiceId` = `s`.`Id`);");

            migrationBuilder.DropColumn(name: "LevelsQuantity", table: "Orders");
            migrationBuilder.DropColumn(name: "PropertyType", table: "Orders");
        }
    }
}
