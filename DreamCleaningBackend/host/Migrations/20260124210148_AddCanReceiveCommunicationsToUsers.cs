using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddCanReceiveCommunicationsToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentMailLogs");

            migrationBuilder.DropTable(
                name: "ScheduledMails");

            migrationBuilder.AddColumn<bool>(
                name: "CanReceiveCommunications",
                table: "Users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4577));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4581));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4583));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4586));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4588));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4590));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4595));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4598));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4600));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 10, DateTimeKind.Utc).AddTicks(3376));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 10, DateTimeKind.Utc).AddTicks(3380));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4485));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4488));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4494));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4540));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 11, DateTimeKind.Utc).AddTicks(4543));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 10, DateTimeKind.Utc).AddTicks(3151));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 10, DateTimeKind.Utc).AddTicks(3157));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 10, DateTimeKind.Utc).AddTicks(3158));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 24, 21, 1, 47, 10, DateTimeKind.Utc).AddTicks(3160));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanReceiveCommunications",
                table: "Users");

            migrationBuilder.CreateTable(
                name: "ScheduledMails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CreatedById = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    Frequency = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    NextScheduledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    ScheduleTimezone = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ScheduleType = table.Column<int>(type: "int", nullable: false),
                    ScheduledDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ScheduledTime = table.Column<TimeSpan>(type: "time(6)", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TargetRoles = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TimesSent = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    WeekOfMonth = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledMails_Users_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SentMailLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ScheduledMailId = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsDelivered = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RecipientEmail = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecipientName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RecipientRole = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentMailLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentMailLogs_ScheduledMails_ScheduledMailId",
                        column: x => x.ScheduledMailId,
                        principalTable: "ScheduledMails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2831));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2835));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2837));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2840));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2843));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2845));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2849));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2857));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2859));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 649, DateTimeKind.Utc).AddTicks(1859));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 649, DateTimeKind.Utc).AddTicks(1863));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2738));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2742));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2748));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2790));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 651, DateTimeKind.Utc).AddTicks(2794));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 649, DateTimeKind.Utc).AddTicks(1563));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 649, DateTimeKind.Utc).AddTicks(1569));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 649, DateTimeKind.Utc).AddTicks(1570));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2026, 1, 20, 21, 15, 58, 649, DateTimeKind.Utc).AddTicks(1572));

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMails_CreatedById",
                table: "ScheduledMails",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMails_NextScheduledAt",
                table: "ScheduledMails",
                column: "NextScheduledAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMails_Status",
                table: "ScheduledMails",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SentMailLogs_MailId_SentAt",
                table: "SentMailLogs",
                columns: new[] { "ScheduledMailId", "SentAt" });
        }
    }
}
