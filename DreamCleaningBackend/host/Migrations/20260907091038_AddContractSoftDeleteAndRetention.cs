using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddContractSoftDeleteAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HiddenAt",
                table: "Contracts",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HiddenByUserId",
                table: "Contracts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "Contracts",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ContractDeletionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ContractNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContractId = table.Column<int>(type: "int", nullable: false),
                    ClientLegalName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StatusAtDeletion = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    HiddenAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    HiddenBy = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    VersionCount = table.Column<int>(type: "int", nullable: false),
                    SignatureCount = table.Column<int>(type: "int", nullable: false),
                    FileCount = table.Column<int>(type: "int", nullable: false),
                    DeletedBy = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractDeletionLogs", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_HiddenByUserId",
                table: "Contracts",
                column: "HiddenByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_IsHidden",
                table: "Contracts",
                column: "IsHidden");

            migrationBuilder.CreateIndex(
                name: "IX_ContractDeletionLogs_DeletedAt",
                table: "ContractDeletionLogs",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ContractDeletionLogs_Number",
                table: "ContractDeletionLogs",
                column: "ContractNumber");

            migrationBuilder.AddForeignKey(
                name: "FK_Contracts_Users_HiddenByUserId",
                table: "Contracts",
                column: "HiddenByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Contracts_Users_HiddenByUserId",
                table: "Contracts");

            migrationBuilder.DropTable(
                name: "ContractDeletionLogs");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_HiddenByUserId",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Contracts_IsHidden",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "HiddenAt",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "HiddenByUserId",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "Contracts");
        }
    }
}
