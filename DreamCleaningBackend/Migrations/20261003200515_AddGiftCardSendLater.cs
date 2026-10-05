using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftCardSendLater : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "RecipientName",
                table: "GiftCards",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(100)",
                oldMaxLength: 100)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "RecipientEmail",
                table: "GiftCards",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(255)",
                oldMaxLength: 255)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "EmailsSentOnPurchase",
                table: "GiftCards",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Every already-paid card had its purchase emails sent by confirm-payment, so a repeated
            // confirm on an old card must not resend them. Nothing else on existing rows changes.
            migrationBuilder.Sql("UPDATE GiftCards SET EmailsSentOnPurchase = 1 WHERE IsPaid = 1;");

            migrationBuilder.AddColumn<bool>(
                name: "IsPendingSend",
                table: "GiftCards",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEmailSentAt",
                table: "GiftCards",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentAt",
                table: "GiftCards",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailsSentOnPurchase",
                table: "GiftCards");

            migrationBuilder.DropColumn(
                name: "IsPendingSend",
                table: "GiftCards");

            migrationBuilder.DropColumn(
                name: "LastEmailSentAt",
                table: "GiftCards");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "GiftCards");

            migrationBuilder.UpdateData(
                table: "GiftCards",
                keyColumn: "RecipientName",
                keyValue: null,
                column: "RecipientName",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "RecipientName",
                table: "GiftCards",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(100)",
                oldMaxLength: 100,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "GiftCards",
                keyColumn: "RecipientEmail",
                keyValue: null,
                column: "RecipientEmail",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "RecipientEmail",
                table: "GiftCards",
                type: "varchar(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(255)",
                oldMaxLength: 255,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
