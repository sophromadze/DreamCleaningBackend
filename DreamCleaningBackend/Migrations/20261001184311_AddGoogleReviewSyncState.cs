using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleReviewSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleReviewSyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    TotalReviewCount = table.Column<int>(type: "int", nullable: true),
                    AverageRating = table.Column<double>(type: "double", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastError = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    LastErrorAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    AlertSentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleReviewSyncStates", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleReviewSyncStates");
        }
    }
}
