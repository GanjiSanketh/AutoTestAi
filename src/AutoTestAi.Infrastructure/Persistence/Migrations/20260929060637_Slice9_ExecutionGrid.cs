using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice9_ExecutionGrid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grid_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRenewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WorkerAssignmentRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AssignmentToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grid_assignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "grid_workers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WorkerType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Framework = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Browsers = table.Column<List<string>>(type: "text[]", nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    ActiveAssignmentCount = table.Column<int>(type: "integer", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CredentialHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CredentialSalt = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grid_workers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_grid_assignments_ExecutionId",
                table: "grid_assignments",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_grid_assignments_ExecutionTestId",
                table: "grid_assignments",
                column: "ExecutionTestId",
                unique: true,
                filter: "\"Status\" IN ('Claimed', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_grid_assignments_ExpiresAt",
                table: "grid_assignments",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_grid_assignments_Status",
                table: "grid_assignments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_grid_assignments_WorkerId",
                table: "grid_assignments",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_grid_workers_LastHeartbeatAt",
                table: "grid_workers",
                column: "LastHeartbeatAt");

            migrationBuilder.CreateIndex(
                name: "IX_grid_workers_Status",
                table: "grid_workers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_grid_workers_WorkerKey",
                table: "grid_workers",
                column: "WorkerKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "grid_assignments");

            migrationBuilder.DropTable(
                name: "grid_workers");
        }
    }
}
