using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminPercentBonusAndCommercialBonus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BonusLabel",
                table: "Expenses",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AdminPercentBonusRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Basis = table.Column<int>(type: "int", nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminPercentBonusRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminPercentBonusRules_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AdminPercentBonusRules_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AdminPercentBonusRules_CreatedByUserId",
                table: "AdminPercentBonusRules",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminPercentBonusRules_UserId_StartDate",
                table: "AdminPercentBonusRules",
                columns: new[] { "UserId", "StartDate" });

            // A SuperAdmin earns no staff bonus, past orders included (owner's call, 2026-10-14).
            // AssignAdminAsync stops snapshotting them from now on; this clears the bonus link on
            // orders a SuperAdmin (Role 1) already holds, so Statistics and Finances restate those
            // months without the cost. Only the BONUS columns move — AssignedAdminId (who is
            // responsible for the order, the "By:" pill) is left exactly as it is. A booking a
            // SuperAdmin took pays their manager nothing either, so that side is cleared with it.
            // Not reversible: Down cannot know which links this removed.
            migrationBuilder.Sql(@"
UPDATE Orders o
JOIN Users u ON u.Id = o.BonusBookerId
SET o.BonusBookerId = NULL, o.BonusManagerId = NULL, o.BonusBookerPosition = 0
WHERE u.Role = 1;");
            migrationBuilder.Sql(@"
UPDATE Orders o
JOIN Users u ON u.Id = o.BonusManagerId
SET o.BonusManagerId = NULL
WHERE u.Role = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminPercentBonusRules");

            migrationBuilder.DropColumn(
                name: "BonusLabel",
                table: "Expenses");
        }
    }
}
