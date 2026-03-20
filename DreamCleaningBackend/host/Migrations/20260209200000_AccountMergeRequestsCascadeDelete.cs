using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AccountMergeRequestsCascadeDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountMergeRequests_Users_NewAccountId",
                table: "AccountMergeRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountMergeRequests_Users_OldAccountId",
                table: "AccountMergeRequests");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountMergeRequests_Users_NewAccountId",
                table: "AccountMergeRequests",
                column: "NewAccountId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountMergeRequests_Users_OldAccountId",
                table: "AccountMergeRequests",
                column: "OldAccountId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountMergeRequests_Users_NewAccountId",
                table: "AccountMergeRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_AccountMergeRequests_Users_OldAccountId",
                table: "AccountMergeRequests");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountMergeRequests_Users_NewAccountId",
                table: "AccountMergeRequests",
                column: "NewAccountId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountMergeRequests_Users_OldAccountId",
                table: "AccountMergeRequests",
                column: "OldAccountId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
