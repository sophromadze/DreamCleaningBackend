using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    /// <summary>Adds FloorTypes and FloorTypeOther columns to Orders for optional floor type selection.</summary>
    public partial class AddFloorTypesToOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FloorTypes",
                table: "Orders",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FloorTypeOther",
                table: "Orders",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FloorTypes",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FloorTypeOther",
                table: "Orders");
        }
    }
}
