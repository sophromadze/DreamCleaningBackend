using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderReminderAcknowledgments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderReminderAcknowledgments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AcknowledgedByUserId = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderReminderAcknowledgments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderReminderAcknowledgments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderReminderAcknowledgments_Users_AcknowledgedByUserId",
                        column: x => x.AcknowledgedByUserId,
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
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7229));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7231));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7233));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7235));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7237));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7240));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7242));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7243));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7245));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(2284));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(2286));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7165));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7168));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7172));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7188));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(7211));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(2169));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(2173));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(2174));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 19, 44, 22, 498, DateTimeKind.Utc).AddTicks(2176));

            migrationBuilder.CreateIndex(
                name: "IX_OrderReminderAcknowledgments_AcknowledgedByUserId",
                table: "OrderReminderAcknowledgments",
                column: "AcknowledgedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderReminderAcknowledgments_OrderType",
                table: "OrderReminderAcknowledgments",
                columns: new[] { "OrderId", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderReminderAcknowledgments");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5517));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5520));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5521));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5523));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5525));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5527));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5528));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5530));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5531));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(805));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(807));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5467));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5471));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5476));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5492));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(5497));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(677));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(682));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(684));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 20, 22, 31, 16, 224, DateTimeKind.Utc).AddTicks(685));
        }
    }
}
