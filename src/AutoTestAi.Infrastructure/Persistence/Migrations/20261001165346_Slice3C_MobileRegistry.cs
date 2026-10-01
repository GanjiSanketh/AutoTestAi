using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3C_MobileRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MobileAppId",
                table: "executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MobileDevicePoolId",
                table: "executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MobileDeviceSessionId",
                table: "executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mobile_apps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PackageId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    BundleId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    InstallPolicy = table.Column<string>(type: "text", nullable: false),
                    LaunchActivity = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DeepLink = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_apps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_device_pools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_device_pools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mobile_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    PlatformVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Udid = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AutomationName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mobile_devices_mobile_device_pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "mobile_device_pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_device_slots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_device_slots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mobile_device_slots_mobile_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "mobile_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mobile_device_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceSlotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppiumSessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mobile_device_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mobile_device_sessions_mobile_device_slots_DeviceSlotId",
                        column: x => x.DeviceSlotId,
                        principalTable: "mobile_device_slots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_apps_Project_Bundle",
                table: "mobile_apps",
                columns: new[] { "ProjectId", "Platform", "BundleId" },
                unique: true,
                filter: "\"BundleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_apps_Project_Package",
                table: "mobile_apps",
                columns: new[] { "ProjectId", "Platform", "PackageId" },
                unique: true,
                filter: "\"PackageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_apps_ProjectId",
                table: "mobile_apps",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_apps_ProjectId_Platform",
                table: "mobile_apps",
                columns: new[] { "ProjectId", "Platform" });

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_pools_ProjectId",
                table: "mobile_device_pools",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_pools_Status",
                table: "mobile_device_pools",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_pools_Project_Name",
                table: "mobile_device_pools",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_sessions_AssignmentId",
                table: "mobile_device_sessions",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_sessions_DeviceSlotId",
                table: "mobile_device_sessions",
                column: "DeviceSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_sessions_ExecutionId",
                table: "mobile_device_sessions",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_sessions_Status",
                table: "mobile_device_sessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_slots_ClaimExpiresAt",
                table: "mobile_device_slots",
                column: "ClaimExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_slots_DeviceId",
                table: "mobile_device_slots",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_slots_PoolId",
                table: "mobile_device_slots",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_device_slots_Status",
                table: "mobile_device_slots",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_slots_Device_SlotNumber",
                table: "mobile_device_slots",
                columns: new[] { "DeviceId", "SlotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_Platform",
                table: "mobile_devices",
                column: "Platform");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_PoolId",
                table: "mobile_devices",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_Project_Udid",
                table: "mobile_devices",
                columns: new[] { "ProjectId", "Udid" },
                unique: true,
                filter: "\"Udid\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_ProjectId",
                table: "mobile_devices",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_mobile_devices_ProjectId_Status",
                table: "mobile_devices",
                columns: new[] { "ProjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_apps");

            migrationBuilder.DropTable(
                name: "mobile_device_sessions");

            migrationBuilder.DropTable(
                name: "mobile_device_slots");

            migrationBuilder.DropTable(
                name: "mobile_devices");

            migrationBuilder.DropTable(
                name: "mobile_device_pools");

            migrationBuilder.DropColumn(
                name: "MobileAppId",
                table: "executions");

            migrationBuilder.DropColumn(
                name: "MobileDevicePoolId",
                table: "executions");

            migrationBuilder.DropColumn(
                name: "MobileDeviceSessionId",
                table: "executions");
        }
    }
}
