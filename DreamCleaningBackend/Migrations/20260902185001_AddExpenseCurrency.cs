using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Expenses",
                type: "varchar(3)",
                maxLength: 3,
                nullable: false,
                // Every expense predating this column is a USD one. EF defaults a new non-nullable
                // string column to "", which would leave them all blank — the read side normalises
                // that to USD anyway, but a required column has no business holding an empty string
                // when the real answer is known.
                defaultValue: "USD")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Expenses");
        }
    }
}
