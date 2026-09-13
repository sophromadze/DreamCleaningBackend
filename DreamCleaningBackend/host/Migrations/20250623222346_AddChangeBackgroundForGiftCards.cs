using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeBackgroundForGiftCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "GiftCardConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    BackgroundImagePath = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastUpdated = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftCardConfigs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6344));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6348));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6351));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6353));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6356));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6359));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6362));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6364));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6367));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 45, DateTimeKind.Local).AddTicks(6627));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 45, DateTimeKind.Local).AddTicks(6630));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6264));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6271));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6275));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6313));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 46, DateTimeKind.Local).AddTicks(6315));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 45, DateTimeKind.Local).AddTicks(6403));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 45, DateTimeKind.Local).AddTicks(6421));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 45, DateTimeKind.Local).AddTicks(6423));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 24, 2, 23, 46, 45, DateTimeKind.Local).AddTicks(6424));

            migrationBuilder.CreateIndex(
                name: "IX_UserSpecialOffers_UsedOnOrderId",
                table: "UserSpecialOffers",
                column: "UsedOnOrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserSpecialOffers_Orders_UsedOnOrderId",
                table: "UserSpecialOffers",
                column: "UsedOnOrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserSpecialOffers_Orders_UsedOnOrderId",
                table: "UserSpecialOffers");

            migrationBuilder.DropTable(
                name: "GiftCardConfigs");

            migrationBuilder.DropIndex(
                name: "IX_UserSpecialOffers_UsedOnOrderId",
                table: "UserSpecialOffers");

            migrationBuilder.AddColumn<int>(
                name: "UsedOnOrderId1",
                table: "UserSpecialOffers",
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
    }
}
