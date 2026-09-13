using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderBedroomsBathroomsQuantities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BathroomsQuantity",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BedroomsQuantity",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 3,
                column: "SettingValue",
                value: "999");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 4,
                column: "SettingValue",
                value: "1000");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 5,
                column: "SettingValue",
                value: "1999");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 6,
                column: "SettingValue",
                value: "3000");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 13,
                column: "SettingValue",
                value: "90");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 18,
                column: "SettingValue",
                value: "15");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 32,
                column: "SettingValue",
                value: "10");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 34,
                columns: new[] { "Description", "SettingValue" },
                values: new object[] { "Extra bonus points for new user who used a referral link (in addition to welcome bonus)", "50" });

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 35,
                column: "SettingValue",
                value: "true");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 36,
                column: "SettingValue",
                value: "1000");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 37,
                column: "SettingValue",
                value: "2000");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 38,
                column: "SettingValue",
                value: "4000");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3190));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3192));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3194));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3196));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3197));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3199));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3201));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3203));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3204));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 349, DateTimeKind.Utc).AddTicks(8398));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 349, DateTimeKind.Utc).AddTicks(8421));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3149));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3151));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3154));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3171));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 350, DateTimeKind.Utc).AddTicks(3173));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 349, DateTimeKind.Utc).AddTicks(8274));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 349, DateTimeKind.Utc).AddTicks(8278));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 349, DateTimeKind.Utc).AddTicks(8279));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 10, 18, 42, 7, 349, DateTimeKind.Utc).AddTicks(8280));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BathroomsQuantity",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BedroomsQuantity",
                table: "Orders");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 3,
                column: "SettingValue",
                value: "499");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 4,
                column: "SettingValue",
                value: "500");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 5,
                column: "SettingValue",
                value: "1499");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 6,
                column: "SettingValue",
                value: "1500");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 13,
                column: "SettingValue",
                value: "70");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 18,
                column: "SettingValue",
                value: "25");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 32,
                column: "SettingValue",
                value: "20");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 34,
                columns: new[] { "Description", "SettingValue" },
                values: new object[] { "Extra bonus points for new user who used referral code", "0" });

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 35,
                column: "SettingValue",
                value: "false");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 36,
                column: "SettingValue",
                value: "400");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 37,
                column: "SettingValue",
                value: "1000");

            migrationBuilder.UpdateData(
                table: "BubbleRewardsSettings",
                keyColumn: "Id",
                keyValue: 38,
                column: "SettingValue",
                value: "2000");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8336));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8339));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8341));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8342));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8344));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8346));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8347));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8349));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8351));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(3460));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(3463));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8271));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8274));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8277));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8296));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(8298));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(3368));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(3372));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(3373));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 6, 10, 46, 4, 239, DateTimeKind.Utc).AddTicks(3374));
        }
    }
}
