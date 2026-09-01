using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class SplitManagerOwnAndTeamBonusRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_BonusAdministratorId",
                table: "Orders");

            migrationBuilder.RenameColumn(
                name: "BonusAdministratorId",
                table: "Orders",
                newName: "BonusBookerId");

            migrationBuilder.RenameIndex(
                name: "IX_Orders_BonusAdministratorId",
                table: "Orders",
                newName: "IX_Orders_BonusBookerId");

            migrationBuilder.RenameColumn(
                name: "ManagerNewCustomerRate",
                table: "AdminBonusSettings",
                newName: "ManagerTeamNewCustomerRate");

            migrationBuilder.RenameColumn(
                name: "ManagerExistingCustomerRate",
                table: "AdminBonusSettings",
                newName: "ManagerTeamExistingCustomerRate");

            // NOTE: the scaffolder renamed the old single override pair onto the TEAM columns.
            // That is backwards for most rows. The old pair meant "this person's rate for whatever
            // they earn on", and under the previous model an ADMINISTRATOR only ever earned for
            // their own bookings while a MANAGER only ever earned a team share. So the pair lands
            // on OwnBooking* here, and the manager rows are moved across by the SQL below —
            // leaving an administrator's override in a team column would silently drop them back
            // to the company default.
            migrationBuilder.RenameColumn(
                name: "NewCustomerRate",
                table: "AdminBonusRateOverrides",
                newName: "OwnBookingNewCustomerRate");

            migrationBuilder.RenameColumn(
                name: "ExistingCustomerRate",
                table: "AdminBonusRateOverrides",
                newName: "OwnBookingExistingCustomerRate");

            migrationBuilder.AddColumn<int>(
                name: "BonusBookerPosition",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ManagerOwnBookingExistingCustomerRate",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ManagerOwnBookingNewCustomerRate",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TeamBookingExistingCustomerRate",
                table: "AdminBonusRateOverrides",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TeamBookingNewCustomerRate",
                table: "AdminBonusRateOverrides",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AdminBonusSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "ManagerOwnBookingExistingCustomerRate", "ManagerOwnBookingNewCustomerRate" },
                values: new object[] { 25m, 15m });

            // ── Move a MANAGER's existing override onto the team columns ──
            // See the rename note above. Own -> Team is read before Own is cleared, which MySQL
            // guarantees by evaluating a row's assignments left to right.
            migrationBuilder.Sql(@"
                UPDATE `AdminBonusRateOverrides` o
                JOIN `Users` u ON u.`Id` = o.`UserId`
                SET o.`TeamBookingNewCustomerRate` = o.`OwnBookingNewCustomerRate`,
                    o.`TeamBookingExistingCustomerRate` = o.`OwnBookingExistingCustomerRate`,
                    o.`OwnBookingNewCustomerRate` = NULL,
                    o.`OwnBookingExistingCustomerRate` = NULL
                WHERE u.`AdminPosition` = 1;");

            // ── Re-shape orders a MANAGER booked themselves ──
            // Under the previous model those rows carried no booker at all: the manager was
            // recorded in the team slot and paid the team rate. They now belong in the booker slot,
            // marked Manager, so they pay the manager's own-booking rate — which is the whole point
            // of this migration. The shape is unambiguous: no booker but a manager present can only
            // mean a manager booked it themselves.
            //
            // Everything else keeps the default position 0 (Administrator), which is correct: every
            // order backfilled by the previous migration was taken by somebody acting as an
            // administrator, because the Manager position did not exist when they were booked.
            migrationBuilder.Sql(@"
                UPDATE `Orders`
                SET `BonusBookerId` = `BonusManagerId`,
                    `BonusBookerPosition` = 1,
                    `BonusManagerId` = NULL
                WHERE `BonusBookerId` IS NULL AND `BonusManagerId` IS NOT NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_BonusBookerId",
                table: "Orders",
                column: "BonusBookerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_BonusBookerId",
                table: "Orders");

            // Put a manager's own bookings back in the team slot the old model kept them in. Must
            // run while BonusBookerPosition still exists — it is the only thing that identifies
            // them once the columns are merged again.
            migrationBuilder.Sql(@"
                UPDATE `Orders`
                SET `BonusManagerId` = `BonusBookerId`,
                    `BonusBookerId` = NULL
                WHERE `BonusBookerPosition` = 1 AND `BonusBookerId` IS NOT NULL;");

            // Fold a manager's team rates back into the single pair the old model had, which is the
            // Own* pair here because that is the one renamed back to NewCustomerRate below.
            migrationBuilder.Sql(@"
                UPDATE `AdminBonusRateOverrides` o
                JOIN `Users` u ON u.`Id` = o.`UserId`
                SET o.`OwnBookingNewCustomerRate` = o.`TeamBookingNewCustomerRate`,
                    o.`OwnBookingExistingCustomerRate` = o.`TeamBookingExistingCustomerRate`
                WHERE u.`AdminPosition` = 1;");

            migrationBuilder.DropColumn(
                name: "BonusBookerPosition",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ManagerOwnBookingExistingCustomerRate",
                table: "AdminBonusSettings");

            migrationBuilder.DropColumn(
                name: "ManagerOwnBookingNewCustomerRate",
                table: "AdminBonusSettings");

            migrationBuilder.DropColumn(
                name: "TeamBookingExistingCustomerRate",
                table: "AdminBonusRateOverrides");

            migrationBuilder.DropColumn(
                name: "TeamBookingNewCustomerRate",
                table: "AdminBonusRateOverrides");

            migrationBuilder.RenameColumn(
                name: "BonusBookerId",
                table: "Orders",
                newName: "BonusAdministratorId");

            migrationBuilder.RenameIndex(
                name: "IX_Orders_BonusBookerId",
                table: "Orders",
                newName: "IX_Orders_BonusAdministratorId");

            migrationBuilder.RenameColumn(
                name: "ManagerTeamNewCustomerRate",
                table: "AdminBonusSettings",
                newName: "ManagerNewCustomerRate");

            migrationBuilder.RenameColumn(
                name: "ManagerTeamExistingCustomerRate",
                table: "AdminBonusSettings",
                newName: "ManagerExistingCustomerRate");

            migrationBuilder.RenameColumn(
                name: "OwnBookingNewCustomerRate",
                table: "AdminBonusRateOverrides",
                newName: "NewCustomerRate");

            migrationBuilder.RenameColumn(
                name: "OwnBookingExistingCustomerRate",
                table: "AdminBonusRateOverrides",
                newName: "ExistingCustomerRate");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_BonusAdministratorId",
                table: "Orders",
                column: "BonusAdministratorId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
