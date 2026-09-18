using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCommercialInvoiceArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "CommercialInvoices",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArchivedByUserId",
                table: "CommercialInvoices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "CommercialInvoices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoices_Archived_InvoiceDate",
                table: "CommercialInvoices",
                columns: new[] { "IsArchived", "InvoiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoices_ArchivedByUserId",
                table: "CommercialInvoices",
                column: "ArchivedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CommercialInvoices_Users_ArchivedByUserId",
                table: "CommercialInvoices",
                column: "ArchivedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CommercialInvoices_Users_ArchivedByUserId",
                table: "CommercialInvoices");

            migrationBuilder.DropIndex(
                name: "IX_CommercialInvoices_Archived_InvoiceDate",
                table: "CommercialInvoices");

            migrationBuilder.DropIndex(
                name: "IX_CommercialInvoices_ArchivedByUserId",
                table: "CommercialInvoices");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "CommercialInvoices");

            migrationBuilder.DropColumn(
                name: "ArchivedByUserId",
                table: "CommercialInvoices");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "CommercialInvoices");
        }
    }
}
