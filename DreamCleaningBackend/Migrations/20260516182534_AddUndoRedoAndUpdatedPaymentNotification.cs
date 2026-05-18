using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddUndoRedoAndUpdatedPaymentNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedPaymentNotificationSentAt",
                table: "OrderUpdateHistories",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UndoneAt",
                table: "AuditLogs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4745));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4748));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4750));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4751));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4753));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4755));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4756));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4759));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4760));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 28, DateTimeKind.Utc).AddTicks(9943));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 28, DateTimeKind.Utc).AddTicks(9945));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4702));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4705));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4708));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4727));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 29, DateTimeKind.Utc).AddTicks(4729));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 28, DateTimeKind.Utc).AddTicks(9834));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 28, DateTimeKind.Utc).AddTicks(9838));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 28, DateTimeKind.Utc).AddTicks(9840));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 18, 25, 34, 28, DateTimeKind.Utc).AddTicks(9841));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedPaymentNotificationSentAt",
                table: "OrderUpdateHistories");

            migrationBuilder.DropColumn(
                name: "UndoneAt",
                table: "AuditLogs");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9626));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9630));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9632));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9635));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9637));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9639));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9640));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9643));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9645));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(4747));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(4749));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9579));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9583));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9586));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9605));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(9609));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(4641));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(4644));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(4646));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 9, 10, 56, 7, 167, DateTimeKind.Utc).AddTicks(4647));
        }
    }
}
