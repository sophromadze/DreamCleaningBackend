using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCommercialBillingUpgrade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "ContractTemplates",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrimaryBillingContact",
                table: "ContractContacts",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ServiceDatesJson",
                table: "CommercialInvoices",
                type: "LONGTEXT",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "ProcessingFee",
                table: "CommercialInvoicePayments",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ProcessingFee",
                table: "CommercialInvoicePaymentAttempts",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalCharged",
                table: "CommercialInvoicePaymentAttempts",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AchCustomerFeeCapAmount",
                table: "BillingSettings",
                type: "decimal(10,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "AchCustomerFeeEnabled",
                table: "BillingSettings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "AchCustomerFeeRatePercent",
                table: "BillingSettings",
                type: "decimal(6,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "DefaultContractPriceMode",
                table: "BillingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // ══ Data upgrade for the row that already exists ══════════════════════════════════
            //
            // BillingSettings is a SINGLE ROW created on first read, so on any deployed database it
            // already exists and the columns above have just landed on it as 0 / false. Left alone,
            // that would mean the ACH processing fee is switched OFF with a zero rate and a zero
            // cap on every existing installation - the feature would ship inert and nobody would
            // know why. These are brand-new columns nobody has configured, so writing the intended
            // defaults into them is not overruling anyone.
            migrationBuilder.Sql(@"
                UPDATE BillingSettings
                SET AchCustomerFeeEnabled = 1,
                    AchCustomerFeeRatePercent = 0.8,
                    AchCustomerFeeCapAmount = 5.00;");

            // The tax defaults are DIFFERENT and are treated differently, because they are NOT new
            // columns - an admin may deliberately have set them. So this only moves a row that is
            // still on the original shipped default of "Exempt, no rate", which is the state that
            // means nobody ever chose. A row where somebody picked Exempt on purpose is
            // indistinguishable from that and will be moved too; a row where they picked anything
            // else, including a different rate, is left exactly as it is.
            //
            // This changes what a NEW invoice or contract is PREFILLED with. It cannot touch a
            // finalized invoice or a signed contract: both carry their own tax snapshot and are
            // never recomputed from this row.
            migrationBuilder.Sql(@"
                UPDATE BillingSettings
                SET DefaultTaxType = 1,
                    DefaultTaxRate = 8.875
                WHERE DefaultTaxType = 0 AND DefaultTaxRate IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "ContractTemplates");

            migrationBuilder.DropColumn(
                name: "IsPrimaryBillingContact",
                table: "ContractContacts");

            migrationBuilder.DropColumn(
                name: "ServiceDatesJson",
                table: "CommercialInvoices");

            migrationBuilder.DropColumn(
                name: "ProcessingFee",
                table: "CommercialInvoicePayments");

            migrationBuilder.DropColumn(
                name: "ProcessingFee",
                table: "CommercialInvoicePaymentAttempts");

            migrationBuilder.DropColumn(
                name: "TotalCharged",
                table: "CommercialInvoicePaymentAttempts");

            migrationBuilder.DropColumn(
                name: "AchCustomerFeeCapAmount",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "AchCustomerFeeEnabled",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "AchCustomerFeeRatePercent",
                table: "BillingSettings");

            migrationBuilder.DropColumn(
                name: "DefaultContractPriceMode",
                table: "BillingSettings");
        }
    }
}
