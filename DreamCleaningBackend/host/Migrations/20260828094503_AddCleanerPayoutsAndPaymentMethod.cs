using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanerPayoutsAndPaymentMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "OrderCleaners",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidAmount",
                table: "OrderCleaners",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaidAt",
                table: "OrderCleaners",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaidByUserId",
                table: "OrderCleaners",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaidVia",
                table: "OrderCleaners",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentNote",
                table: "OrderCleaners",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "SalaryBillableMinutes",
                table: "OrderCleaners",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SalaryHourlyRate",
                table: "OrderCleaners",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentDetails",
                table: "Cleaners",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "PaymentMethod",
                table: "Cleaners",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderCleaners_IsPaid",
                table: "OrderCleaners",
                column: "IsPaid");

            migrationBuilder.CreateIndex(
                name: "IX_OrderCleaners_PaidByUserId",
                table: "OrderCleaners",
                column: "PaidByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderCleaners_Users_PaidByUserId",
                table: "OrderCleaners",
                column: "PaidByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderCleaners_Users_PaidByUserId",
                table: "OrderCleaners");

            migrationBuilder.DropIndex(
                name: "IX_OrderCleaners_IsPaid",
                table: "OrderCleaners");

            migrationBuilder.DropIndex(
                name: "IX_OrderCleaners_PaidByUserId",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "PaidAmount",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "PaidByUserId",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "PaidVia",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "PaymentNote",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "SalaryBillableMinutes",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "SalaryHourlyRate",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "PaymentDetails",
                table: "Cleaners");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Cleaners");
        }
    }
}
