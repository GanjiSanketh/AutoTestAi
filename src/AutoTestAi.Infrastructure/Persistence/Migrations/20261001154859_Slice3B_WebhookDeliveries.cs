using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3B_WebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VerificationStatus = table.Column<string>(type: "text", nullable: false),
                    ProcessingStatus = table.Column<string>(type: "text", nullable: false),
                    NormalizedMetadataJson = table.Column<string>(type: "text", nullable: true),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriggeredCount = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_ExecutionId",
                table: "webhook_deliveries",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_Integration_Delivery",
                table: "webhook_deliveries",
                columns: new[] { "IntegrationId", "DeliveryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_IntegrationId",
                table: "webhook_deliveries",
                column: "IntegrationId");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_ProcessingStatus",
                table: "webhook_deliveries",
                column: "ProcessingStatus");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_ProjectId",
                table: "webhook_deliveries",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_ReceivedAt",
                table: "webhook_deliveries",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_deliveries");
        }
    }
}
