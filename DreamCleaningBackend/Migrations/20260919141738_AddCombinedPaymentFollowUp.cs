using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCombinedPaymentFollowUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BookkeepingAppliedAt",
                table: "OrderPaymentBatchItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmationSentAt",
                table: "OrderPaymentBatchItems",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FollowUpAttempts",
                table: "OrderPaymentBatchItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FollowUpClaimedAt",
                table: "OrderPaymentBatchItems",
                type: "datetime(6)",
                nullable: true);

            // Hand-added data step. Items of batches that were ALREADY settled when this runs are
            // marked followed-up, so the new worker never mails a customer about an old payment or
            // re-applies an old loyalty/subscription change. Items of batches NOT yet settled are
            // left open on purpose: a charge Stripe took whose recording failed (the bug fixed
            // alongside this) is settled later by the reconciler and still owes its confirmation.
            // Idempotent: it only touches NULLs.
            migrationBuilder.Sql(@"
UPDATE OrderPaymentBatchItems i
JOIN OrderPaymentBatches b ON b.Id = i.OrderPaymentBatchId
SET i.BookkeepingAppliedAt = COALESCE(i.BookkeepingAppliedAt, UTC_TIMESTAMP(6)),
    i.ConfirmationSentAt   = COALESCE(i.ConfirmationSentAt,   UTC_TIMESTAMP(6))
WHERE b.Status = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BookkeepingAppliedAt",
                table: "OrderPaymentBatchItems");

            migrationBuilder.DropColumn(
                name: "ConfirmationSentAt",
                table: "OrderPaymentBatchItems");

            migrationBuilder.DropColumn(
                name: "FollowUpAttempts",
                table: "OrderPaymentBatchItems");

            migrationBuilder.DropColumn(
                name: "FollowUpClaimedAt",
                table: "OrderPaymentBatchItems");
        }
    }
}
