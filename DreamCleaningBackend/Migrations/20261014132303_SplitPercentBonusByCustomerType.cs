using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class SplitPercentBonusByCustomerType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Percent",
                table: "AdminPercentBonusRules",
                newName: "NewCustomerPercent");

            migrationBuilder.AddColumn<decimal>(
                name: "ExistingCustomerPercent",
                table: "AdminPercentBonusRules",
                type: "decimal(5,2)",
                nullable: false,
                defaultValue: 0m);

            // A rule saved before the split had ONE percent for every order; it keeps paying exactly
            // that on both kinds of customer.
            migrationBuilder.Sql("UPDATE AdminPercentBonusRules SET ExistingCustomerPercent = NewCustomerPercent;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExistingCustomerPercent",
                table: "AdminPercentBonusRules");

            migrationBuilder.RenameColumn(
                name: "NewCustomerPercent",
                table: "AdminPercentBonusRules",
                newName: "Percent");
        }
    }
}
