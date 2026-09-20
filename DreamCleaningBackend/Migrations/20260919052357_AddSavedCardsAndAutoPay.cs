using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddSavedCardsAndAutoPay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPayEnabled",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoPayUpdatedAt",
                table: "Users",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BackupPaymentMethodId",
                table: "Users",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrimaryPaymentMethodId",
                table: "Users",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BillingNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Message = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActionUrl = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActionLabel = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ObligationKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    CommercialInvoiceId = table.Column<int>(type: "int", nullable: true),
                    PaymentAttemptId = table.Column<int>(type: "int", nullable: true),
                    DedupeKey = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ShowInApp = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EmailStatus = table.Column<int>(type: "int", nullable: false),
                    EmailTo = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EmailSubject = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EmailHtml = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EmailAttempts = table.Column<int>(type: "int", nullable: false),
                    EmailLastError = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SmsStatus = table.Column<int>(type: "int", nullable: false),
                    SmsTo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SmsBody = table.Column<string>(type: "varchar(640)", maxLength: 640, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SmsAttempts = table.Column<int>(type: "int", nullable: false),
                    SmsLastError = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NextDeliveryAttemptAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingNotifications", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "BillingPaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ObligationKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ObligationType = table.Column<int>(type: "int", nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    CommercialInvoiceId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RunKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Trigger = table.Column<int>(type: "int", nullable: false),
                    CardRole = table.Column<int>(type: "int", nullable: false),
                    CustomerPaymentMethodId = table.Column<int>(type: "int", nullable: true),
                    StripePaymentMethodId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CardBrand = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CardLast4 = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaymentAuthorizationId = table.Column<int>(type: "int", nullable: true),
                    InitiatedByUserId = table.Column<int>(type: "int", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StripePaymentIntentId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CommercialInvoicePaymentAttemptId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeclineCode = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FailureMessage = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActiveLockKey = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReconcileAttempts = table.Column<int>(type: "int", nullable: false),
                    RunFinalizedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingPaymentAttempts", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CustomerPaymentMethods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StripePaymentMethodId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Brand = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Last4 = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExpMonth = table.Column<int>(type: "int", nullable: true),
                    ExpYear = table.Column<int>(type: "int", nullable: true),
                    Funding = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Wallet = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusReason = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Source = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    RemovedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastFailedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastFailureCode = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPaymentMethods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerPaymentMethods_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PaymentAuthorizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    RecurringSeriesId = table.Column<int>(type: "int", nullable: true),
                    ContractClientId = table.Column<int>(type: "int", nullable: true),
                    ScopeKey = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ActiveScopeKey = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AllowBackupFallback = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TermsVersion = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TermsHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TermsSnapshot = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SmsConsentAccepted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CancellationFeeAccepted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    TermsOfServiceAccepted = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AcceptedIp = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AcceptedUserAgent = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RevokedByUserId = table.Column<int>(type: "int", nullable: true),
                    RevokedReason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentAuthorizations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Users_BackupPaymentMethodId",
                table: "Users",
                column: "BackupPaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_PrimaryPaymentMethodId",
                table: "Users",
                column: "PrimaryPaymentMethodId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_BackupRequiresPrimary",
                table: "Users",
                sql: "`BackupPaymentMethodId` IS NULL OR `PrimaryPaymentMethodId` IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_PrimaryBackupDistinct",
                table: "Users",
                sql: "`PrimaryPaymentMethodId` IS NULL OR `BackupPaymentMethodId` IS NULL OR `PrimaryPaymentMethodId` <> `BackupPaymentMethodId`");

            migrationBuilder.CreateIndex(
                name: "IX_BillingNotifications_DedupeKey",
                table: "BillingNotifications",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingNotifications_Delivery",
                table: "BillingNotifications",
                columns: new[] { "EmailStatus", "SmsStatus", "NextDeliveryAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingNotifications_User",
                table: "BillingNotifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_ActiveLock",
                table: "BillingPaymentAttempts",
                column: "ActiveLockKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_IdempotencyKey",
                table: "BillingPaymentAttempts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_Obligation",
                table: "BillingPaymentAttempts",
                columns: new[] { "ObligationKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_PaymentIntent",
                table: "BillingPaymentAttempts",
                column: "StripePaymentIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_Run_Sequence",
                table: "BillingPaymentAttempts",
                columns: new[] { "RunKey", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_Status",
                table: "BillingPaymentAttempts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BillingPaymentAttempts_User",
                table: "BillingPaymentAttempts",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMethods_StripePaymentMethodId",
                table: "CustomerPaymentMethods",
                column: "StripePaymentMethodId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPaymentMethods_User_Status",
                table: "CustomerPaymentMethods",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAuthorizations_ContractClient",
                table: "PaymentAuthorizations",
                column: "ContractClientId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAuthorizations_RecurringSeries",
                table: "PaymentAuthorizations",
                column: "RecurringSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAuthorizations_Scope_Status",
                table: "PaymentAuthorizations",
                columns: new[] { "Scope", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAuthorizations_User_ActiveScope",
                table: "PaymentAuthorizations",
                columns: new[] { "UserId", "ActiveScopeKey" },
                unique: true);

            // ── Carry every existing one-card customer across (hand-written DATA step) ──────────
            // The pre-2026-09 feature stored one card per user in Users.DefaultPaymentMethodId
            // (+ brand / last four, + StripeCustomerId). Each becomes a CustomerPaymentMethods row
            // and that user's PRIMARY card. Nothing is lost and nothing is invented:
            //  • idempotent — NOT EXISTS on the pm id, and the Primary is set only where still
            //    NULL — so a re-run or an interrupted run cannot duplicate or overwrite anything;
            //  • only rows with BOTH a pm id and a Stripe Customer (a card without its Customer
            //    could never be charged, so it is left exactly where it is, untouched);
            //  • expiry is unknown for these rows and is backfilled from Stripe the first time the
            //    Billing tab loads (unknown is never treated as expired);
            //  • the legacy columns are KEPT (now a mirror of the Primary) — nothing is dropped.
            // AutoPay stays OFF for everyone: saving a card never implied it.
            // Verified by LegacyCardMigrationTests, which executes exactly these two statements.
            migrationBuilder.Sql(@"
INSERT INTO `CustomerPaymentMethods`
    (`UserId`, `StripeCustomerId`, `StripePaymentMethodId`, `Brand`, `Last4`, `Status`, `Source`, `CreatedAt`, `UpdatedAt`)
SELECT u.`Id`, u.`StripeCustomerId`, u.`DefaultPaymentMethodId`, u.`SavedCardBrand`, u.`SavedCardLast4`,
       0, 'legacy_migration', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM `Users` u
WHERE u.`DefaultPaymentMethodId` IS NOT NULL AND u.`DefaultPaymentMethodId` <> ''
  AND u.`StripeCustomerId` IS NOT NULL AND u.`StripeCustomerId` <> ''
  AND NOT EXISTS (SELECT 1 FROM `CustomerPaymentMethods` c WHERE c.`StripePaymentMethodId` = u.`DefaultPaymentMethodId`);
");

            migrationBuilder.Sql(@"
UPDATE `Users` u
JOIN `CustomerPaymentMethods` c
  ON c.`StripePaymentMethodId` = u.`DefaultPaymentMethodId` AND c.`UserId` = u.`Id`
SET u.`PrimaryPaymentMethodId` = c.`Id`
WHERE u.`PrimaryPaymentMethodId` IS NULL AND c.`Status` = 0;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingNotifications");

            migrationBuilder.DropTable(
                name: "BillingPaymentAttempts");

            migrationBuilder.DropTable(
                name: "CustomerPaymentMethods");

            migrationBuilder.DropTable(
                name: "PaymentAuthorizations");

            migrationBuilder.DropIndex(
                name: "IX_Users_BackupPaymentMethodId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_PrimaryPaymentMethodId",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_BackupRequiresPrimary",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_PrimaryBackupDistinct",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AutoPayEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AutoPayUpdatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BackupPaymentMethodId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PrimaryPaymentMethodId",
                table: "Users");
        }
    }
}
