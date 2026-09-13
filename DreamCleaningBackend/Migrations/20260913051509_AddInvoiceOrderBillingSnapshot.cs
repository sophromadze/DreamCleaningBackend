using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceOrderBillingSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OriginalContractClientId",
                table: "CommercialInvoiceOrders",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalPaymentMethod",
                table: "CommercialInvoiceOrders",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalContractClientId",
                table: "CommercialInvoiceOrders");

            migrationBuilder.DropColumn(
                name: "OriginalPaymentMethod",
                table: "CommercialInvoiceOrders");
        }
    }
}
