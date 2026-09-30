using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice10_AutoTicketing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "tickets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "auto_ticket_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Severities = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DefectStatuses = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Classifications = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MinimumConfidence = table.Column<decimal>(type: "numeric", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auto_ticket_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_ProjectId_SyncStatus_NextAttemptAt",
                table: "tickets",
                columns: new[] { "ProjectId", "SyncStatus", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_auto_ticket_policies_ProjectId",
                table: "auto_ticket_policies",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auto_ticket_policies");

            migrationBuilder.DropIndex(
                name: "IX_tickets_ProjectId_SyncStatus_NextAttemptAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "tickets");
        }
    }
}
