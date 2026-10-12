using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanerTipsWithholding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TipsHiddenFromCleaner",
                table: "OrderCleaners",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TipsWithheldFromPayout",
                table: "OrderCleaners",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TipsHiddenFromCleaner",
                table: "OrderCleaners");

            migrationBuilder.DropColumn(
                name: "TipsWithheldFromPayout",
                table: "OrderCleaners");
        }
    }
}
