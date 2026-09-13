using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminPositionsAndBonusTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminBonusRatePerOrderGel",
                table: "MonthlyFinancialSnapshots");

            // NOTE: the scaffolder guessed RatePerOrder -> ManagerNewCustomerRate as a RENAME.
            // That was replaced by an explicit add/carry-over/drop below. The old flat rate was
            // what an ADMIN earned per order, so silently landing it in the manager column would
            // have paid every manager the administrators' rate.
            migrationBuilder.AddColumn<decimal>(
                name: "ManagerNewCustomerRate",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "AdminPosition",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ManagerId",
                table: "Users",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BonusAdministratorId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BonusManagerId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsNewCustomerOrder",
                table: "Orders",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "AdministratorExistingCustomerRate",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AdministratorNewCustomerRate",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ManagerExistingCustomerRate",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "AdminBonusRateOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    NewCustomerRate = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ExistingCustomerRate = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminBonusRateOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminBonusRateOverrides_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AdminBonusRateOverrides_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "AdminBonusSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AdministratorExistingCustomerRate", "AdministratorNewCustomerRate", "ManagerExistingCustomerRate", "ManagerNewCustomerRate" },
                values: new object[] { 10m, 10m, 15m, 5m });

            // Carry a CUSTOMISED flat rate over to the administrator side. The old single rate was
            // what an admin earned per order, so that is where it belongs; the manager rates are
            // brand new and take the seeded defaults. This runs AFTER the seed UpdateData above,
            // which would otherwise reset an owner-configured figure back to 10 — the seeded row is
            // managed by HasData, so that UpdateData is emitted whether or not the value was ever
            // changed in production.
            migrationBuilder.Sql(@"
                UPDATE `AdminBonusSettings`
                SET `AdministratorNewCustomerRate` = `RatePerOrder`,
                    `AdministratorExistingCustomerRate` = `RatePerOrder`
                WHERE `RatePerOrder` > 0;");

            migrationBuilder.DropColumn(
                name: "RatePerOrder",
                table: "AdminBonusSettings");

            // ── Backfill: bonus attribution on existing orders ──
            // Everybody who holds an order today is an administrator (the Manager position did not
            // exist), and nobody had a manager, so the administrator side is the current assignee
            // and the manager side stays empty. Historic orders therefore keep paying exactly what
            // they paid before this migration; attaching a manager retroactively would invent a
            // debt to somebody who was not owed one.
            migrationBuilder.Sql(@"
                UPDATE `Orders`
                SET `BonusAdministratorId` = `AssignedAdminId`
                WHERE `AssignedAdminId` IS NOT NULL;");

            // ── Backfill: was each order the customer's first-ever real booking? ──
            // Mirrors what BookingCreationService now records at insert time: an order is NEW when
            // no REAL booking (not cancelled, not refunded, and paid or paid outside Stripe — the
            // OrderBookedFilter.IsRealBooking rule) existed for that customer beforehand.
            //
            // Ordered by Id, not by ServiceDate or OrderDate, because Id is insertion order and
            // insertion order is exactly what the runtime rule sees. A back-dated order re-entered
            // by an admin has an early service date and a late Id, and it did NOT make its customer
            // new again.
            //
            // Customers with no real booking at all (only abandoned checkouts) have every row
            // flagged, which is likewise what the runtime rule would have produced. Those rows can
            // never pay a bonus — an unpaid order is not bonus-eligible — so this only keeps the
            // stored fact honest.
            migrationBuilder.Sql(@"
                UPDATE `Orders` o
                LEFT JOIN (
                    SELECT `UserId`, MIN(`Id`) AS `FirstRealId`
                    FROM `Orders`
                    WHERE `Status` <> 'Cancelled'
                      AND `Status` <> 'Refunded'
                      AND (`IsPaid` = 1 OR `PaymentMethod` <> 0)
                    GROUP BY `UserId`
                ) f ON f.`UserId` = o.`UserId`
                SET o.`IsNewCustomerOrder` = 1
                WHERE f.`FirstRealId` IS NULL OR o.`Id` <= f.`FirstRealId`;");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ManagerId",
                table: "Users",
                column: "ManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BonusAdministratorId",
                table: "Orders",
                column: "BonusAdministratorId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BonusManagerId",
                table: "Orders",
                column: "BonusManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminBonusRateOverrides_UpdatedByUserId",
                table: "AdminBonusRateOverrides",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminBonusRateOverrides_UserId",
                table: "AdminBonusRateOverrides",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_BonusAdministratorId",
                table: "Orders",
                column: "BonusAdministratorId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_BonusManagerId",
                table: "Orders",
                column: "BonusManagerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ManagerId",
                table: "Users",
                column: "ManagerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_BonusAdministratorId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_BonusManagerId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ManagerId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "AdminBonusRateOverrides");

            migrationBuilder.DropIndex(
                name: "IX_Users_ManagerId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BonusAdministratorId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BonusManagerId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AdminPosition",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ManagerId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BonusAdministratorId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BonusManagerId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsNewCustomerOrder",
                table: "Orders");

            // Rebuild the flat rate from the administrator side — the half it came from — BEFORE
            // the four-rate columns go, so a rollback keeps whatever figure was in force rather
            // than resetting the owner's rate to the seeded 10. Order matters here: the read has to
            // happen while AdministratorExistingCustomerRate still exists.
            migrationBuilder.AddColumn<decimal>(
                name: "RatePerOrder",
                table: "AdminBonusSettings",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 10m);

            migrationBuilder.Sql(@"
                UPDATE `AdminBonusSettings`
                SET `RatePerOrder` = `AdministratorExistingCustomerRate`
                WHERE `AdministratorExistingCustomerRate` > 0;");

            migrationBuilder.DropColumn(
                name: "AdministratorExistingCustomerRate",
                table: "AdminBonusSettings");

            migrationBuilder.DropColumn(
                name: "AdministratorNewCustomerRate",
                table: "AdminBonusSettings");

            migrationBuilder.DropColumn(
                name: "ManagerExistingCustomerRate",
                table: "AdminBonusSettings");

            migrationBuilder.DropColumn(
                name: "ManagerNewCustomerRate",
                table: "AdminBonusSettings");

            migrationBuilder.AddColumn<decimal>(
                name: "AdminBonusRatePerOrderGel",
                table: "MonthlyFinancialSnapshots",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);
        }
    }
}
