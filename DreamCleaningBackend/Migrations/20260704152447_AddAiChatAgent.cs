using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddAiChatAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatAgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    GuestIdentifier = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TelegramTopicId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAgentSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatAgentSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ChatAgentSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EscalationEmailEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UpdatedByEmail = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAgentSettings", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ChatAgentMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ChatSessionId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImagePath = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImageExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatAgentMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatAgentMessages_ChatAgentSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatAgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAgentMessages_ImageExpiresAt",
                table: "ChatAgentMessages",
                column: "ImageExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAgentMessages_Session_CreatedAt",
                table: "ChatAgentMessages",
                columns: new[] { "ChatSessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatAgentSessions_GuestIdentifier",
                table: "ChatAgentSessions",
                column: "GuestIdentifier");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAgentSessions_LastMessageAt",
                table: "ChatAgentSessions",
                column: "LastMessageAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAgentSessions_TelegramTopicId",
                table: "ChatAgentSessions",
                column: "TelegramTopicId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatAgentSessions_UserId",
                table: "ChatAgentSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatAgentMessages");

            migrationBuilder.DropTable(
                name: "ChatAgentSettings");

            migrationBuilder.DropTable(
                name: "ChatAgentSessions");
        }
    }
}
