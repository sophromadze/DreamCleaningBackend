using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCommercialStripePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "ContractClients",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<int>(
                name: "RecordedByUserId",
                table: "CommercialInvoicePayments",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "CommercialInvoicePayments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecordedByLabel",
                table: "CommercialInvoicePayments",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "StripeChargeId",
                table: "CommercialInvoicePayments",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "CommercialInvoicePayments",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "BankWireRoutingNumber",
                table: "BillingSettings",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // THE DEFAULTS BELOW WERE CORRECTED BY HAND, and the reason matters.
            //
            // EF generated `defaultValue: false` for all three, because a C# property initializer
            // (`public bool ManualAchEnabled { get; set; } = true;`) is invisible to the migration
            // generator — it backfills the CLR default instead. BillingSettings is a SINGLE ROW
            // that already exists, so that backfill is what the live database would actually get.
            //
            // The consequence would have been silent and bad: ManualAchEnabled = false switches
            // OFF the bank-transfer instructions that are working today, and every invoice would
            // quietly stop showing a customer any way to pay. Nothing would error; the block would
            // simply vanish.
            //
            // These values mirror the model initializers exactly: ACH on, manual on, card off.
            migrationBuilder.AddColumn<bool>(
                name: "ManualAchEnabled",
                table: "BillingSettings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "StripeAchEnabled",
                table: "BillingSettings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            // Card stays OFF: enabling it is an owner's commercial decision, not a default.
            migrationBuilder.AddColumn<bool>(
                name: "StripeCardEnabled",
                table: "BillingSettings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CommercialInvoicePaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CommercialInvoiceId = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    PaymentMethod = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StripeCheckoutSessionId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StripePaymentIntentId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FailureMessage = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PaymentSourceLabel = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CommercialInvoicePaymentId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialInvoicePaymentAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommercialInvoicePaymentAttempts_CommercialInvoices_Commerci~",
                        column: x => x.CommercialInvoiceId,
                        principalTable: "CommercialInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoicePayments_StripePaymentIntent",
                table: "CommercialInvoicePayments",
                column: "StripePaymentIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoicePaymentAttempts_CheckoutSession",
                table: "CommercialInvoicePaymentAttempts",
                column: "StripeCheckoutSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoicePaymentAttempts_Invoice_Status",
                table: "CommercialInvoicePaymentAttempts",
                columns: new[] { "CommercialInvoiceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoicePaymentAttempts_InvoiceId",
                table: "CommercialInvoicePaymentAttempts",
                column: "CommercialInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CommercialInvoicePaymentAttempts_PaymentIntent",
                table: "CommercialInvoicePaymentAttempts",
                column: "StripePaymentIntentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommercialInvoicePaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_CommercialInvoicePayments_StripePaymentIntent",
                table: "CommercialInvoicePayments");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "ContractClients");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "CommercialInvoicePayments");

            migrationBuilder.DropColumn(
                name: "RecordedByLabel",
                table: "CommercialInvoicePayments");

            migrationBuilder.DropColumn(
                name: "StripeChargeId",
                table: "CommercialInvoicePayments");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "CommercialInvoicePayments");

            migrationBuilder.DropColumn(
                name: "BankWireRoutingNumber",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "ManualAchEnabled",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "StripeAchEnabled",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "StripeCardEnabled",
                table: "BillingSettings");

            migrationBuilder.AlterColumn<int>(
                name: "RecordedByUserId",
                table: "CommercialInvoicePayments",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
