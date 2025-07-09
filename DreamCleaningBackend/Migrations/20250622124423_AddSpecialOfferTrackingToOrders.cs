using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecialOfferTrackingToOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserSpecialOffers_Orders_UsedOnOrderId",
                table: "UserSpecialOffers");

            migrationBuilder.DropIndex(
                name: "IX_UserSpecialOffers_UsedOnOrderId",
                table: "UserSpecialOffers");

            migrationBuilder.AddColumn<int>(
                name: "UsedOnOrderId1",
                table: "UserSpecialOffers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecialOfferName",
                table: "Orders",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "UserSpecialOfferId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1185));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1189));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1192));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1195));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1197));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1202));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1205));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1207));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1209));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 604, DateTimeKind.Local).AddTicks(421));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 604, DateTimeKind.Local).AddTicks(424));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1098));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1105));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1111));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1147));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 605, DateTimeKind.Local).AddTicks(1150));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 604, DateTimeKind.Local).AddTicks(185));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 604, DateTimeKind.Local).AddTicks(204));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 604, DateTimeKind.Local).AddTicks(207));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 22, 16, 44, 22, 604, DateTimeKind.Local).AddTicks(209));

            migrationBuilder.CreateIndex(
                name: "IX_UserSpecialOffers_UsedOnOrderId1",
                table: "UserSpecialOffers",
                column: "UsedOnOrderId1");

            migrationBuilder.AddForeignKey(
                name: "FK_UserSpecialOffers_Orders_UsedOnOrderId1",
                table: "UserSpecialOffers",
                column: "UsedOnOrderId1",
                principalTable: "Orders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserSpecialOffers_Orders_UsedOnOrderId1",
                table: "UserSpecialOffers");

            migrationBuilder.DropIndex(
                name: "IX_UserSpecialOffers_UsedOnOrderId1",
                table: "UserSpecialOffers");

            migrationBuilder.DropColumn(
                name: "UsedOnOrderId1",
                table: "UserSpecialOffers");

            migrationBuilder.DropColumn(
                name: "SpecialOfferName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "UserSpecialOfferId",
                table: "Orders");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8101));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8105));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8108));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8110));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8113));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8115));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8118));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8120));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8122));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 261, DateTimeKind.Local).AddTicks(6124));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 261, DateTimeKind.Local).AddTicks(6127));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8014));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8021));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8027));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8065));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 262, DateTimeKind.Local).AddTicks(8068));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 261, DateTimeKind.Local).AddTicks(5877));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 261, DateTimeKind.Local).AddTicks(5894));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 261, DateTimeKind.Local).AddTicks(5896));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 21, 17, 17, 57, 261, DateTimeKind.Local).AddTicks(5897));

            migrationBuilder.CreateIndex(
                name: "IX_UserSpecialOffers_UsedOnOrderId",
                table: "UserSpecialOffers",
                column: "UsedOnOrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserSpecialOffers_Orders_UsedOnOrderId",
                table: "UserSpecialOffers",
                column: "UsedOnOrderId",
                principalTable: "Orders",
                principalColumn: "Id");
        }
    }
}
