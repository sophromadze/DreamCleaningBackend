using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminBonusAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignedAdminId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdminBonusSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RatePerOrder = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminBonusSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminBonusSettings_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OrderAdminAssignmentHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    PreviousAdminId = table.Column<int>(type: "int", nullable: true),
                    NewAdminId = table.Column<int>(type: "int", nullable: true),
                    ChangedByUserId = table.Column<int>(type: "int", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    BonusRateAtChange = table.Column<decimal>(type: "decimal(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderAdminAssignmentHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderAdminAssignmentHistories_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderAdminAssignmentHistories_Users_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderAdminAssignmentHistories_Users_NewAdminId",
                        column: x => x.NewAdminId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderAdminAssignmentHistories_Users_PreviousAdminId",
                        column: x => x.PreviousAdminId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "AdminBonusSettings",
                columns: new[] { "Id", "Currency", "RatePerOrder", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 1, "GEL", 10m, new DateTime(2026, 5, 24, 0, 0, 0, 0, DateTimeKind.Utc), null });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(893));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(896));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(897));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(899));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(901));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(903));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(904));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(907));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(908));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 875, DateTimeKind.Utc).AddTicks(6049));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 875, DateTimeKind.Utc).AddTicks(6051));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(826));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(829));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(832));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(871));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 876, DateTimeKind.Utc).AddTicks(873));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 875, DateTimeKind.Utc).AddTicks(5923));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 875, DateTimeKind.Utc).AddTicks(5928));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 875, DateTimeKind.Utc).AddTicks(5930));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 24, 9, 4, 21, 875, DateTimeKind.Utc).AddTicks(5931));

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AssignedAdminId",
                table: "Orders",
                column: "AssignedAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminBonusSettings_UpdatedByUserId",
                table: "AdminBonusSettings",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAdminAssignmentHistories_ChangedByUserId",
                table: "OrderAdminAssignmentHistories",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAdminAssignmentHistories_PreviousAdminId",
                table: "OrderAdminAssignmentHistories",
                column: "PreviousAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAdminAssignmentHistory_ChangedAt",
                table: "OrderAdminAssignmentHistories",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAdminAssignmentHistory_NewAdminId",
                table: "OrderAdminAssignmentHistories",
                column: "NewAdminId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderAdminAssignmentHistory_OrderId",
                table: "OrderAdminAssignmentHistories",
                column: "OrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_AssignedAdminId",
                table: "Orders",
                column: "AssignedAdminId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_AssignedAdminId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "AdminBonusSettings");

            migrationBuilder.DropTable(
                name: "OrderAdminAssignmentHistories");

            migrationBuilder.DropIndex(
                name: "IX_Orders_AssignedAdminId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AssignedAdminId",
                table: "Orders");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3954));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3956));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3980));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3982));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3984));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3986));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3987));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3989));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3991));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 939, DateTimeKind.Utc).AddTicks(8895));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 939, DateTimeKind.Utc).AddTicks(8897));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3911));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3914));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3917));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3934));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 940, DateTimeKind.Utc).AddTicks(3937));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 939, DateTimeKind.Utc).AddTicks(8754));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 939, DateTimeKind.Utc).AddTicks(8758));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 939, DateTimeKind.Utc).AddTicks(8760));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 5, 18, 13, 27, 14, 939, DateTimeKind.Utc).AddTicks(8793));
        }
    }
}
