using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyDiscountSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLogs_Orders_OrderId",
                table: "NotificationLogs");

            migrationBuilder.AddColumn<DateTime>(
                name: "LoyaltyDiscountActivatedAt",
                table: "Users",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LoyaltyDiscountIsManualOverride",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LoyaltyDiscountLastUsedAt",
                table: "Users",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LoyaltyDiscountPercentage",
                table: "Users",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LoyaltyDiscountAmount",
                table: "Orders",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "LoyaltyDiscountPercentage",
                table: "Orders",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AlterColumn<int>(
                name: "OrderId",
                table: "NotificationLogs",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.InsertData(
                table: "BubbleRewardsSettings",
                columns: new[] { "Id", "Category", "Description", "SettingKey", "SettingValue", "UpdatedAt" },
                values: new object[,]
                {
                    { 39, "LoyaltyDiscount", "Master switch for the loyalty re-engagement system", "LoyaltyDiscountEnabled", "true", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 40, "LoyaltyDiscount", "Discount % auto-activated on the 60-day reminder", "LoyaltyDay60Percentage", "10", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 41, "LoyaltyDiscount", "Discount % upgraded to on the 90-day reminder", "LoyaltyDay90Percentage", "15", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 42, "LoyaltyDiscount", "Days of inactivity before the first re-engagement reminder", "DaysUntilFirstReminder", "30", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 43, "LoyaltyDiscount", "Days of inactivity before the loyalty discount is auto-activated", "DaysUntilDiscountActivation", "60", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 44, "LoyaltyDiscount", "Days of inactivity before the loyalty discount is upgraded", "DaysUntilDiscountUpgrade", "90", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 45, "LoyaltyDiscount", "Cooldown after using a loyalty discount before a new cycle can start", "MinDaysFromLastUseBeforeReActivation", "30", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7345));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7347));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7349));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7350));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7352));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7354));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7356));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7357));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7359));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(2177));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(2179));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7293));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7296));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7300));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7320));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(7322));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(2053));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(2057));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(2058));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 16, 23, 47, 5, 981, DateTimeKind.Utc).AddTicks(2060));

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_Orders_OrderId",
                table: "NotificationLogs",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationLogs_Orders_OrderId",
                table: "NotificationLogs");

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 39);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 40);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 41);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 42);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 43);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 44);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 45);

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountActivatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountIsManualOverride",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountLastUsedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountPercentage",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LoyaltyDiscountPercentage",
                table: "Orders");

            migrationBuilder.AlterColumn<int>(
                name: "OrderId",
                table: "NotificationLogs",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

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

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationLogs_Orders_OrderId",
                table: "NotificationLogs",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
