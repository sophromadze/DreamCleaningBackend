using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddRedemptionPointThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 11,
                column: "Description",
                value: "Dollar credit for Tier 1 redemption");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 12,
                column: "Description",
                value: "Dollar credit for Tier 2 redemption");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 13,
                column: "Description",
                value: "Dollar credit for Tier 3 redemption");

            migrationBuilder.InsertData(
                table: "BubbleRewardsSettings",
                columns: new[] { "Id", "Category", "Description", "SettingKey", "SettingValue", "UpdatedAt" },
                values: new object[,]
                {
                    { 36, "Redemption", "Points required for Tier 1 redemption", "RedemptionTier1Points", "400", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 37, "Redemption", "Points required for Tier 2 redemption", "RedemptionTier2Points", "1000", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 38, "Redemption", "Points required for Tier 3 redemption", "RedemptionTier3Points", "2000", new DateTime(2026, 4, 4, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6083));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6086));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6087));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6089));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6090));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6092));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6094));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6096));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6097));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(1378));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(1380));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6041));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6044));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6047));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6064));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(6067));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(1265));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(1268));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(1270));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 5, 8, 16, 59, 17, DateTimeKind.Utc).AddTicks(1271));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 36);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 37);

            migrationBuilder.DeleteData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 38);

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 11,
                column: "Description",
                value: "Dollar credit for 200 points");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 12,
                column: "Description",
                value: "Dollar credit for 500 points");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 13,
                column: "Description",
                value: "Dollar credit for 1000 points");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5972));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5974));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5976));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5978));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5980));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5982));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5984));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5985));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5987));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1113));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1115));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5908));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5933));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5937));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5953));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(5955));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1004));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1007));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1009));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 4, 18, 40, 54, 902, DateTimeKind.Utc).AddTicks(1010));
        }
    }
}
