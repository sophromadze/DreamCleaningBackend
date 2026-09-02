using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminSalaryPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminSalaryPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PayeeKey = table.Column<string>(type: "varchar(220)", maxLength: 220, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StaffUserId = table.Column<int>(type: "int", nullable: true),
                    PayeeName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Half = table.Column<int>(type: "int", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaidAmountUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    UsdPerGel = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PaidByUserId = table.Column<int>(type: "int", nullable: false),
                    PaymentNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminSalaryPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminSalaryPayments_Users_PaidByUserId",
                        column: x => x.PaidByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSalaryPayments_PaidByUserId",
                table: "AdminSalaryPayments",
                column: "PaidByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSalaryPayments_Payee_Period",
                table: "AdminSalaryPayments",
                columns: new[] { "PayeeKey", "Year", "Month", "Half" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminSalaryPayments_StaffUserId",
                table: "AdminSalaryPayments",
                column: "StaffUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminSalaryPayments_Year_Month",
                table: "AdminSalaryPayments",
                columns: new[] { "Year", "Month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminSalaryPayments");
        }
    }
}
