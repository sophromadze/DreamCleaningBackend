using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgTitlesBusinessFlagAndSigningChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBusiness",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OrgTitle",
                table: "Users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ContractSigners",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SignedByUserId",
                table: "ContractSignatures",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SigningChannel",
                table: "ContractSignatures",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "ContractContacts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceUserId",
                table: "ContractClients",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContractSigners_UserId",
                table: "ContractSigners",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractSignatures_SignedByUserId",
                table: "ContractSignatures",
                column: "SignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractContacts_UserId",
                table: "ContractContacts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractClients_SourceUserId",
                table: "ContractClients",
                column: "SourceUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ContractClients_Users_SourceUserId",
                table: "ContractClients",
                column: "SourceUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContractContacts_Users_UserId",
                table: "ContractContacts",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ContractSignatures_Users_SignedByUserId",
                table: "ContractSignatures",
                column: "SignedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ContractSigners_Users_UserId",
                table: "ContractSigners",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ContractClients_Users_SourceUserId",
                table: "ContractClients");

            migrationBuilder.DropForeignKey(
                name: "FK_ContractContacts_Users_UserId",
                table: "ContractContacts");

            migrationBuilder.DropForeignKey(
                name: "FK_ContractSignatures_Users_SignedByUserId",
                table: "ContractSignatures");

            migrationBuilder.DropForeignKey(
                name: "FK_ContractSigners_Users_UserId",
                table: "ContractSigners");

            migrationBuilder.DropIndex(
                name: "IX_ContractSigners_UserId",
                table: "ContractSigners");

            migrationBuilder.DropIndex(
                name: "IX_ContractSignatures_SignedByUserId",
                table: "ContractSignatures");

            migrationBuilder.DropIndex(
                name: "IX_ContractContacts_UserId",
                table: "ContractContacts");

            migrationBuilder.DropIndex(
                name: "IX_ContractClients_SourceUserId",
                table: "ContractClients");

            migrationBuilder.DropColumn(
                name: "IsBusiness",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OrgTitle",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ContractSigners");

            migrationBuilder.DropColumn(
                name: "SignedByUserId",
                table: "ContractSignatures");

            migrationBuilder.DropColumn(
                name: "SigningChannel",
                table: "ContractSignatures");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "ContractContacts");

            migrationBuilder.DropColumn(
                name: "SourceUserId",
                table: "ContractClients");
        }
    }
}
