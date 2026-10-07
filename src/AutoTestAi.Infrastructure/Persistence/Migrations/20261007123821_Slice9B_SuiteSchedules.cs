using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice9B_SuiteSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "test_suite_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    OverlapPolicy = table.Column<string>(type: "text", nullable: false),
                    LastTriggeredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_suite_schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_test_suite_schedules_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_test_suite_schedules_test_suites_SuiteId",
                        column: x => x.SuiteId,
                        principalTable: "test_suites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_test_suite_schedules_ProjectId",
                table: "test_suite_schedules",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_test_suite_schedules_SuiteId",
                table: "test_suite_schedules",
                column: "SuiteId");

            // Case-insensitive name uniqueness within a project. Expression
            // indexes cannot be expressed in the EF model, so this lives only
            // in the migration (the model snapshot is unaffected); the service
            // layer enforces the same rule for database-agnostic tests.
            migrationBuilder.Sql(
                """"CREATE UNIQUE INDEX "IX_test_suite_schedules_Project_Name" ON "test_suite_schedules" ("ProjectId", lower("Name"))"""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(""""DROP INDEX IF EXISTS "IX_test_suite_schedules_Project_Name"""");
            migrationBuilder.DropTable(
                name: "test_suite_schedules");
        }
    }
}
