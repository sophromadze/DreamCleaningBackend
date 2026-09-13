using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCleanerUserAccountLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "Cleaners",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cleaners_UserId",
                table: "Cleaners",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Cleaners_Users_UserId",
                table: "Cleaners",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cleaners_Users_UserId",
                table: "Cleaners");

            migrationBuilder.DropIndex(
                name: "IX_Cleaners_UserId",
                table: "Cleaners");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Cleaners");
        }
    }
}
