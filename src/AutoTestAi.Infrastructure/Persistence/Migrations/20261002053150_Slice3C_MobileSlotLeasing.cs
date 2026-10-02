using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3C_MobileSlotLeasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mobile_device_slots_ClaimExpiresAt",
                table: "mobile_device_slots");

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                table: "mobile_device_slots",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_slots_AssignmentId",
                table: "mobile_device_slots",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_slots_Pool_Status",
                table: "mobile_device_slots",
                columns: new[] { "PoolId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_slots_Status_ClaimExpiresAt",
                table: "mobile_device_slots",
                columns: new[] { "Status", "ClaimExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mobile_slots_AssignmentId",
                table: "mobile_device_slots");

            migrationBuilder.DropIndex(
                name: "IX_mobile_slots_Pool_Status",
                table: "mobile_device_slots");

            migrationBuilder.DropIndex(
                name: "IX_mobile_slots_Status_ClaimExpiresAt",
                table: "mobile_device_slots");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                table: "mobile_device_slots");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_slots_ClaimExpiresAt",
                table: "mobile_device_slots",
                column: "ClaimExpiresAt");
        }
    }
}
