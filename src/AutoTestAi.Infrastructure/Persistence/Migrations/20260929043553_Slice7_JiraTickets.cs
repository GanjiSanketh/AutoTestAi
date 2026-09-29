using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice7_JiraTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalKey",
                table: "tickets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IntegrationId",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "tickets",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_DefectId",
                table: "tickets",
                column: "DefectId");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_DefectId_IntegrationId",
                table: "tickets",
                columns: new[] { "DefectId", "IntegrationId" },
                unique: true,
                filter: "\"DefectId\" IS NOT NULL AND \"IntegrationId\" IS NOT NULL AND \"SyncStatus\" = 'Synced'");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_ProjectId_Provider",
                table: "integrations",
                columns: new[] { "ProjectId", "Provider" },
                unique: true,
                filter: "\"ProjectId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_DefectId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_DefectId_IntegrationId",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_integrations_ProjectId_Provider",
                table: "integrations");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ExternalKey",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "tickets");
        }
    }
}
