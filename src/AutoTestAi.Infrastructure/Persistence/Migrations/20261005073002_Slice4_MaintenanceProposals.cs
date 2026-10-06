using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice4_MaintenanceProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepOrder = table.Column<int>(type: "integer", nullable: false),
                    StepAction = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OriginalStrategy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    OriginalValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProposedStrategy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProposedValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HealingStrategy = table.Column<string>(type: "text", nullable: false),
                    SignalType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Confidence = table.Column<int>(type: "integer", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProposedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_proposals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_proposals_ProjectId_Status",
                table: "maintenance_proposals",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_proposals_TestCaseId_StepOrder",
                table: "maintenance_proposals",
                columns: new[] { "TestCaseId", "StepOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_proposals_TestCaseId_TestCaseVersionId_StepOrde~",
                table: "maintenance_proposals",
                columns: new[] { "TestCaseId", "TestCaseVersionId", "StepOrder", "ProposedStrategy", "ProposedValue" },
                unique: true,
                filter: "\"Status\" = 'Proposed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_proposals");
        }
    }
}
