using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanerManagementSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    CleanerId = table.Column<int>(type: "int", nullable: false),
                    NotificationType = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationLogs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OrderCleaners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    CleanerId = table.Column<int>(type: "int", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    TipsForCleaner = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCleaners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderCleaners_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderCleaners_Users_AssignedBy",
                        column: x => x.AssignedBy,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderCleaners_Users_CleanerId",
                        column: x => x.CleanerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(922));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(925));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(927));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(929));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(934));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(936));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(939));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(941));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(943));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 247, DateTimeKind.Local).AddTicks(1554));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 247, DateTimeKind.Local).AddTicks(1558));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(845));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(851));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(858));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(890));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 248, DateTimeKind.Local).AddTicks(893));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 247, DateTimeKind.Local).AddTicks(1392));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 247, DateTimeKind.Local).AddTicks(1405));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 247, DateTimeKind.Local).AddTicks(1407));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 30, 16, 13, 33, 247, DateTimeKind.Local).AddTicks(1409));

            migrationBuilder.CreateIndex(
                name: "IX_OrderCleaners_AssignedBy",
                table: "OrderCleaners",
                column: "AssignedBy");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCleaners_CleanerId",
                table: "OrderCleaners",
                column: "CleanerId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCleaners_OrderId",
                table: "OrderCleaners",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationLogs");

            migrationBuilder.DropTable(
                name: "OrderCleaners");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2647));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2651));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2654));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2656));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2659));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2662));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2665));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2667));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2720));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 49, DateTimeKind.Local).AddTicks(2144));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 49, DateTimeKind.Local).AddTicks(2148));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2561));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2569));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2575));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2612));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 50, DateTimeKind.Local).AddTicks(2615));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 49, DateTimeKind.Local).AddTicks(1941));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 49, DateTimeKind.Local).AddTicks(1956));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 49, DateTimeKind.Local).AddTicks(1958));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 6, 26, 21, 45, 26, 49, DateTimeKind.Local).AddTicks(1959));
        }
    }
}
