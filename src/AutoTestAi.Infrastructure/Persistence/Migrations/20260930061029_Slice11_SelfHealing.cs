using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice11_SelfHealing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "self_healing_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepOrder = table.Column<int>(type: "integer", nullable: false),
                    StepAction = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OriginalStrategy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OriginalValue = table.Column<string>(type: "text", nullable: true),
                    RecoveredStrategy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RecoveredValue = table.Column<string>(type: "text", nullable: true),
                    HealingStrategy = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    WasApplied = table.Column<bool>(type: "boolean", nullable: false),
                    IsAiAssisted = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_self_healing_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "self_healing_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AiFallbackEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxAttemptsPerStep = table.Column<int>(type: "integer", nullable: false),
                    MinDeterministicScore = table.Column<int>(type: "integer", nullable: true),
                    MinAiConfidence = table.Column<decimal>(type: "numeric", nullable: true),
                    AllowedStrategies = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_self_healing_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_self_healing_attempts_CreatedAt",
                table: "self_healing_attempts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_self_healing_attempts_ExecutionId",
                table: "self_healing_attempts",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_self_healing_attempts_ExecutionTestId_StepOrder",
                table: "self_healing_attempts",
                columns: new[] { "ExecutionTestId", "StepOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_self_healing_attempts_ProjectId",
                table: "self_healing_attempts",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_self_healing_attempts_TestCaseVersionId",
                table: "self_healing_attempts",
                column: "TestCaseVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_self_healing_policies_ProjectId",
                table: "self_healing_policies",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "self_healing_attempts");

            migrationBuilder.DropTable(
                name: "self_healing_policies");
        }
    }
}
