using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderRefundTotalsAndHide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenAt",
                table: "Orders",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HiddenByUserId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "Orders",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "StatusBeforeRefund",
                table: "Orders",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "TotalRefundedAmount",
                table: "Orders",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_HiddenByUserId",
                table: "Orders",
                column: "HiddenByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_IsHidden",
                table: "Orders",
                column: "IsHidden");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_HiddenByUserId",
                table: "Orders",
                column: "HiddenByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill the new cache column from refunds that already exist. Counts the same
            // statuses OrderRefundService counts as money actually moved ("succeeded"/"pending"),
            // so failed attempts never reduce reported revenue. Expected to touch zero rows on a
            // database where no refund has been issued yet — it is a no-op in that case, and safe
            // to re-run. Status/StatusBeforeRefund are deliberately NOT backfilled: deciding
            // whether a past refund was FULL needs the live payment-provider balance, which a
            // migration cannot read.
            migrationBuilder.Sql(@"
                UPDATE Orders o
                SET o.TotalRefundedAmount = (
                    SELECT COALESCE(SUM(r.Amount), 0)
                    FROM OrderRefunds r
                    WHERE r.OrderId = o.Id
                      AND r.Status IN ('succeeded', 'pending')
                )
                WHERE EXISTS (SELECT 1 FROM OrderRefunds r2 WHERE r2.OrderId = o.Id);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_HiddenByUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_HiddenByUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_IsHidden",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "HiddenAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "HiddenByUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "StatusBeforeRefund",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TotalRefundedAmount",
                table: "Orders");
        }
    }
}
