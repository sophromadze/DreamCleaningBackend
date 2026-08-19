using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <summary>
    /// Per-service-type control over whether the booking flow asks apartment vs house.
    ///
    /// Why a FLAG and not an inferred rule: Office Cleaning and Heavy Conditional Cleaning are
    /// structurally identical - same cleaner+hours services, no bedrooms, no sq.ft, no levels,
    /// HasPoll and IsCustom both false - and yet Heavy Conditional should keep asking while Office
    /// should not. No data-driven predicate can separate them, so the distinction has to be stored.
    ///
    /// The column defaults to TRUE, so every existing service type keeps exactly its current
    /// behaviour and only the one row switched off below changes.
    /// </summary>
    public partial class AddServiceTypeCollectsPropertyType : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CollectsPropertyType",
                table: "ServiceTypes",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            // ── THE ONE DELIBERATE NAME MATCH IN THIS CODEBASE ──────────────────────────────
            //
            // Everywhere else, seeds match on ServiceKey, because service type Ids AND Names both
            // diverge between the local and production databases - that is what put Levels on
            // Custom Cleaning locally in an earlier migration.
            //
            // This statement is the documented exception, because Office Cleaning is a SINGLE
            // SPECIFIC RECORD rather than a category, and no structural property distinguishes it
            // from Heavy Conditional Cleaning (see the class summary).
            //
            // Positional alternatives were considered and rejected. "The first cleaner+hours type
            // by DisplayOrder" resolves to Office today, but DisplayOrder is admin-editable from
            // the Booking Services screen, and if it ever picked the wrong row it would SILENTLY
            // switch the selector off on Heavy Conditional. The name match has the better failure
            // mode: if it matches nothing, the column keeps its default of true and Office simply
            // continues showing the selector - visible, harmless, and fixable with the admin
            // checkbox this migration ships alongside.
            //
            // The name is identical in both databases ("Office Cleaning"), verified before writing
            // this. DO NOT copy this pattern for anything else.
            migrationBuilder.Sql(
                "UPDATE `ServiceTypes` " +
                "SET `CollectsPropertyType` = 0 " +
                "WHERE `Name` = 'Office Cleaning' AND `CollectsPropertyType` = 1;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollectsPropertyType",
                table: "ServiceTypes");
        }
    }
}
