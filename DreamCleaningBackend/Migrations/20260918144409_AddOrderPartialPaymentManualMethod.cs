using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderPartialPaymentManualMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ManualPaymentRecordedAt",
                table: "OrderPartialPayments",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManualPaymentRecordedByUserId",
                table: "OrderPartialPayments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentMethod",
                table: "OrderPartialPayments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PaymentNotes",
                table: "OrderPartialPayments",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PaymentReference",
                table: "OrderPartialPayments",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OrderPartialPayments_ManualPaymentRecordedByUserId",
                table: "OrderPartialPayments",
                column: "ManualPaymentRecordedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderPartialPayments_Users_ManualPaymentRecordedByUserId",
                table: "OrderPartialPayments",
                column: "ManualPaymentRecordedByUserId",
                principalTable: "Users",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderPartialPayments_Users_ManualPaymentRecordedByUserId",
                table: "OrderPartialPayments");

            migrationBuilder.DropIndex(
                name: "IX_OrderPartialPayments_ManualPaymentRecordedByUserId",
                table: "OrderPartialPayments");

            migrationBuilder.DropColumn(
                name: "ManualPaymentRecordedAt",
                table: "OrderPartialPayments");

            migrationBuilder.DropColumn(
                name: "ManualPaymentRecordedByUserId",
                table: "OrderPartialPayments");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "OrderPartialPayments");

            migrationBuilder.DropColumn(
                name: "PaymentNotes",
                table: "OrderPartialPayments");

            migrationBuilder.DropColumn(
                name: "PaymentReference",
                table: "OrderPartialPayments");
        }
    }
}
