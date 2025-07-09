using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DreamCleaningBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddPollFunctionality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Condition",
                table: "OrderExtraServices");

            migrationBuilder.DropColumn(
                name: "HasCondition",
                table: "ExtraServices");

            migrationBuilder.AddColumn<bool>(
                name: "HasPoll",
                table: "ServiceTypes",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PollQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Question = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    QuestionType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Options = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsRequired = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PollQuestions_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PollSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    ServiceTypeId = table.Column<int>(type: "int", nullable: false),
                    ContactFirstName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContactLastName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContactEmail = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContactPhone = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ServiceAddress = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AptSuite = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    City = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    State = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PostalCode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AdminNotes = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PollSubmissions_ServiceTypes_ServiceTypeId",
                        column: x => x.ServiceTypeId,
                        principalTable: "ServiceTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PollSubmissions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PollAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PollSubmissionId = table.Column<int>(type: "int", nullable: false),
                    PollQuestionId = table.Column<int>(type: "int", nullable: false),
                    Answer = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PollAnswers_PollQuestions_PollQuestionId",
                        column: x => x.PollQuestionId,
                        principalTable: "PollQuestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PollAnswers_PollSubmissions_PollSubmissionId",
                        column: x => x.PollSubmissionId,
                        principalTable: "PollSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1011));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1014));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1017));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1020));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1022));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1026));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1028));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1032));

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(1034));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "HasPoll" },
                values: new object[] { new DateTime(2025, 7, 5, 20, 31, 47, 466, DateTimeKind.Local).AddTicks(701), false });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "HasPoll" },
                values: new object[] { new DateTime(2025, 7, 5, 20, 31, 47, 466, DateTimeKind.Local).AddTicks(704), false });

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(923));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(932));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(938));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(975));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 467, DateTimeKind.Local).AddTicks(978));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 466, DateTimeKind.Local).AddTicks(463));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 466, DateTimeKind.Local).AddTicks(479));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 466, DateTimeKind.Local).AddTicks(482));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 5, 20, 31, 47, 466, DateTimeKind.Local).AddTicks(484));

            migrationBuilder.CreateIndex(
                name: "IX_PollAnswers_PollQuestionId",
                table: "PollAnswers",
                column: "PollQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_PollAnswers_PollSubmissionId",
                table: "PollAnswers",
                column: "PollSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PollQuestions_ServiceTypeId",
                table: "PollQuestions",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_PollSubmissions_ServiceTypeId",
                table: "PollSubmissions",
                column: "ServiceTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_PollSubmissions_UserId",
                table: "PollSubmissions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PollAnswers");

            migrationBuilder.DropTable(
                name: "PollQuestions");

            migrationBuilder.DropTable(
                name: "PollSubmissions");

            migrationBuilder.DropColumn(
                name: "HasPoll",
                table: "ServiceTypes");

            migrationBuilder.AddColumn<int>(
                name: "Condition",
                table: "OrderExtraServices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasCondition",
                table: "ExtraServices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1309), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1313), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1316), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1320), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 5,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1322), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 6,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1325), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 7,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1328), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 8,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1330), false });

            migrationBuilder.UpdateData(
                table: "ExtraServices",
                keyColumn: "Id",
                keyValue: 9,
                columns: new[] { "CreatedAt", "HasCondition" },
                values: new object[] { new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1384), false });

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 477, DateTimeKind.Local).AddTicks(7477));

            migrationBuilder.UpdateData(
                table: "ServiceTypes",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 477, DateTimeKind.Local).AddTicks(7480));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1199));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1213));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1221));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1263));

            migrationBuilder.UpdateData(
                table: "Services",
                keyColumn: "Id",
                keyValue: 5,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 479, DateTimeKind.Local).AddTicks(1269));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 477, DateTimeKind.Local).AddTicks(7121));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 2,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 477, DateTimeKind.Local).AddTicks(7134));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 3,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 477, DateTimeKind.Local).AddTicks(7136));

            migrationBuilder.UpdateData(
                table: "Subscriptions",
                keyColumn: "Id",
                keyValue: 4,
                column: "CreatedAt",
                value: new DateTime(2025, 7, 3, 22, 19, 37, 477, DateTimeKind.Local).AddTicks(7138));
        }
    }
}
