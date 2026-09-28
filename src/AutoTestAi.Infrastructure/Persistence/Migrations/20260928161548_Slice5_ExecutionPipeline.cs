using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice5_ExecutionPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "executions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Browser",
                table: "execution_tests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureClassification",
                table: "execution_tests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Framework",
                table: "execution_tests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FileName",
                table: "execution_artifacts",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StepOrder",
                table: "execution_artifacts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "execution_step_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepOrder = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Target = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_step_results", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_executions_ProjectId_IdempotencyKey",
                table: "executions",
                columns: new[] { "ProjectId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_execution_step_results_ExecutionTestId_StepOrder",
                table: "execution_step_results",
                columns: new[] { "ExecutionTestId", "StepOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "execution_step_results");

            migrationBuilder.DropIndex(
                name: "IX_executions_ProjectId_IdempotencyKey",
                table: "executions");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "executions");

            migrationBuilder.DropColumn(
                name: "Browser",
                table: "execution_tests");

            migrationBuilder.DropColumn(
                name: "FailureClassification",
                table: "execution_tests");

            migrationBuilder.DropColumn(
                name: "Framework",
                table: "execution_tests");

            migrationBuilder.DropColumn(
                name: "FileName",
                table: "execution_artifacts");

            migrationBuilder.DropColumn(
                name: "StepOrder",
                table: "execution_artifacts");
        }
    }
}
