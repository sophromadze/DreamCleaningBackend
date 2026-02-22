using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    public partial class AddScheduledSms : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledSms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Content = table.Column<string>(type: "varchar(1600)", maxLength: 1600, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TargetRoles = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ScheduleType = table.Column<int>(type: "int", nullable: false),
                    ScheduledDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ScheduledTime = table.Column<TimeSpan>(type: "time(6)", nullable: true),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    WeekOfMonth = table.Column<int>(type: "int", nullable: true),
                    Frequency = table.Column<int>(type: "int", nullable: true),
                    ScheduleTimezone = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextScheduledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    TimesSent = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledSms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledSms_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSms_CreatedById",
                table: "ScheduledSms",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSms_NextScheduledAt",
                table: "ScheduledSms",
                column: "NextScheduledAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledSms_Status",
                table: "ScheduledSms",
                column: "Status");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ScheduledSms");
        }
    }
}
