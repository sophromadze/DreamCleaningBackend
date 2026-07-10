using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderBookedByAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BookedByAdminUserId",
                table: "Orders",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BookedByAdminUserId",
                table: "Orders",
                column: "BookedByAdminUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Users_BookedByAdminUserId",
                table: "Orders",
                column: "BookedByAdminUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Users_BookedByAdminUserId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BookedByAdminUserId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BookedByAdminUserId",
                table: "Orders");
        }
    }
}
