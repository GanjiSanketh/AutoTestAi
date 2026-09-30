using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice10_AutoTicketClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimExpiresAt",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RowVersion",
                table: "tickets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Distinct name: the Slice 7 Synced-only index is untouched.
            migrationBuilder.CreateIndex(
                name: "IX_tickets_DefectId_IntegrationId_Pending",
                table: "tickets",
                columns: new[] { "DefectId", "IntegrationId" },
                unique: true,
                filter: "\"DefectId\" IS NOT NULL AND \"IntegrationId\" IS NOT NULL AND \"SyncStatus\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tickets_DefectId_IntegrationId_Pending",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ClaimExpiresAt",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "tickets");
        }
    }
}
