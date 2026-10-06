using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class InvoicePaymentMethodsRecipientsCleanerPayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PaymentMethod",
                table: "CommercialRecurringInvoiceTemplates",
                newName: "PaymentMethods");

            migrationBuilder.RenameColumn(
                name: "PaymentMethod",
                table: "CommercialInvoices",
                newName: "PaymentMethods");

            // The renamed columns still hold the OLD single "payment method" (0 ACH, 1 Card,
            // 2 Other), which no payment path ever read: what an invoice page actually offered was
            // the three global Billing Settings switches. So every existing invoice (and template)
            // becomes exactly those switches as they stand now, plus Other where Other was chosen
            // — its page keeps behaving as it does today. A missing settings row means the
            // entity defaults (bank on, card off, manual on). Bits: 1 online bank, 2 card,
            // 4 manual transfer, 8 other.
            foreach (var table in new[] { "CommercialInvoices", "CommercialRecurringInvoiceTemplates" })
            {
                migrationBuilder.Sql($@"
                    UPDATE `{table}` t
                    LEFT JOIN (
                        SELECT `StripeAchEnabled`, `StripeCardEnabled`, `ManualAchEnabled`
                        FROM `BillingSettings` ORDER BY `Id` LIMIT 1
                    ) s ON 1 = 1
                    SET t.`PaymentMethods` =
                          (CASE WHEN COALESCE(s.`StripeAchEnabled`, 1) = 1 THEN 1 ELSE 0 END)
                        | (CASE WHEN COALESCE(s.`StripeCardEnabled`, 0) = 1 THEN 2 ELSE 0 END)
                        | (CASE WHEN COALESCE(s.`ManualAchEnabled`, 1) = 1 THEN 4 ELSE 0 END)
                        | (CASE WHEN t.`PaymentMethods` = 2 THEN 8 ELSE 0 END);
                ");
            }

            migrationBuilder.AddColumn<string>(
                name: "DefaultInvoiceRecipientsJson",
                table: "ContractClients",
                type: "LONGTEXT",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RecipientEmailsJson",
                table: "CommercialInvoices",
                type: "LONGTEXT",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SendBatchId",
                table: "CommercialInvoiceEmailLogs",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CleanerPayouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CleanerId = table.Column<int>(type: "int", nullable: false),
                    CleanerName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    PaidOn = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PaidVia = table.Column<int>(type: "int", nullable: true),
                    Reference = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedByName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    IsImported = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RevertedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevertedByUserId = table.Column<int>(type: "int", nullable: true),
                    RevertReason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdempotencyKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CleanerPayouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CleanerPayouts_Cleaners_CleanerId",
                        column: x => x.CleanerId,
                        principalTable: "Cleaners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CleanerPayouts_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CleanerPayouts_Users_RevertedByUserId",
                        column: x => x.RevertedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CleanerPayoutLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CleanerPayoutId = table.Column<int>(type: "int", nullable: false),
                    OrderCleanerId = table.Column<int>(type: "int", nullable: true),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    CleanerId = table.Column<int>(type: "int", nullable: false),
                    ServiceDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    IsTopUp = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RevertedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevertedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CleanerPayoutLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CleanerPayoutLines_CleanerPayouts_CleanerPayoutId",
                        column: x => x.CleanerPayoutId,
                        principalTable: "CleanerPayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CleanerPayoutLines_OrderCleaners_OrderCleanerId",
                        column: x => x.OrderCleanerId,
                        principalTable: "OrderCleaners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayoutLines_CleanerPayoutId",
                table: "CleanerPayoutLines",
                column: "CleanerPayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayoutLines_OrderCleaner_Reverted",
                table: "CleanerPayoutLines",
                columns: new[] { "OrderCleanerId", "RevertedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayoutLines_OrderId",
                table: "CleanerPayoutLines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayouts_Cleaner_PaidOn",
                table: "CleanerPayouts",
                columns: new[] { "CleanerId", "PaidOn" });

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayouts_CreatedByUserId",
                table: "CleanerPayouts",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayouts_IdempotencyKey",
                table: "CleanerPayouts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayouts_PaidOn",
                table: "CleanerPayouts",
                column: "PaidOn");

            migrationBuilder.CreateIndex(
                name: "IX_CleanerPayouts_RevertedByUserId",
                table: "CleanerPayouts",
                column: "RevertedByUserId");

            // Every invoice gets its own recipient list, starting from who it actually went to:
            // the address of its most recent successful send...
            migrationBuilder.Sql(@"
                UPDATE `CommercialInvoices` i
                JOIN (
                    SELECT l.`CommercialInvoiceId`, l.`Recipient`
                    FROM `CommercialInvoiceEmailLogs` l
                    WHERE l.`Id` = (
                        SELECT MAX(l2.`Id`) FROM `CommercialInvoiceEmailLogs` l2
                        WHERE l2.`CommercialInvoiceId` = l.`CommercialInvoiceId`
                          AND l2.`Status` = 0
                          AND l2.`EmailType` IN (0, 1))
                ) last_sent ON last_sent.`CommercialInvoiceId` = i.`Id`
                SET i.`RecipientEmailsJson` = JSON_ARRAY(TRIM(last_sent.`Recipient`))
                WHERE i.`RecipientEmailsJson` IS NULL
                  AND TRIM(last_sent.`Recipient`) <> '';
            ");

            // ...and, for one never sent, the address a send would have used: the client's billing
            // contact (primary-billing flag first, then the client signer, then the oldest — the
            // order InvoiceService.ResolveBillingContactAsync resolves by), else the notice email.
            // An invoice whose client has no address at all stays NULL, and sending it asks for one.
            migrationBuilder.Sql(@"
                UPDATE `CommercialInvoices` i
                JOIN `ContractClients` c ON c.`Id` = i.`ContractClientId`
                SET i.`RecipientEmailsJson` = JSON_ARRAY(COALESCE(
                    NULLIF(TRIM((
                        SELECT cc.`Email` FROM `ContractContacts` cc
                        WHERE cc.`ContractClientId` = c.`Id` AND cc.`IsActive` = 1
                        ORDER BY cc.`IsPrimaryBillingContact` DESC, (cc.`Role` = 1) DESC, cc.`Id`
                        LIMIT 1)), ''),
                    NULLIF(TRIM(c.`NoticeEmail`), '')))
                WHERE i.`RecipientEmailsJson` IS NULL
                  AND COALESCE(
                    NULLIF(TRIM((
                        SELECT cc.`Email` FROM `ContractContacts` cc
                        WHERE cc.`ContractClientId` = c.`Id` AND cc.`IsActive` = 1
                        ORDER BY cc.`IsPrimaryBillingContact` DESC, (cc.`Role` = 1) DESC, cc.`Id`
                        LIMIT 1)), ''),
                    NULLIF(TRIM(c.`NoticeEmail`), '')) IS NOT NULL;
            ");

            // Cleaner payout HISTORY is backfilled at startup by CleanerPayoutBackfill, not here:
            // a line paid before amounts were recorded is imported at what the payroll calculator
            // says it was worth, and that calculator is C#, not SQL.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CleanerPayoutLines");

            migrationBuilder.DropTable(
                name: "CleanerPayouts");

            migrationBuilder.DropColumn(
                name: "DefaultInvoiceRecipientsJson",
                table: "ContractClients");

            migrationBuilder.DropColumn(
                name: "RecipientEmailsJson",
                table: "CommercialInvoices");

            migrationBuilder.DropColumn(
                name: "SendBatchId",
                table: "CommercialInvoiceEmailLogs");

            // Back to one method: Other where Other was offered, Card where card was the only
            // online method, otherwise ACH — the closest the old single value can say.
            foreach (var table in new[] { "CommercialInvoices", "CommercialRecurringInvoiceTemplates" })
            {
                migrationBuilder.Sql($@"
                    UPDATE `{table}`
                    SET `PaymentMethods` = CASE
                        WHEN (`PaymentMethods` & 8) = 8 THEN 2
                        WHEN (`PaymentMethods` & 2) = 2 AND (`PaymentMethods` & 1) = 0 THEN 1
                        ELSE 0 END;
                ");
            }

            migrationBuilder.RenameColumn(
                name: "PaymentMethods",
                table: "CommercialRecurringInvoiceTemplates",
                newName: "PaymentMethod");

            migrationBuilder.RenameColumn(
                name: "PaymentMethods",
                table: "CommercialInvoices",
                newName: "PaymentMethod");
        }
    }
}
