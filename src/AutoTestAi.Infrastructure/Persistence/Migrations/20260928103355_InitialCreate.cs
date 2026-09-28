using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    UserAgent = table.Column<string>(type: "text", nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "defects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RootCauseType = table.Column<string>(type: "text", nullable: true),
                    AiConfidence = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_defects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "environments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactType = table.Column<string>(type: "text", nullable: false),
                    StorageKey = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_artifacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_tests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    WorkerId = table.Column<string>(type: "text", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ErrorType = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_tests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TriggerType = table.Column<string>(type: "text", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowId = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_executions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "failure_analyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Classification = table.Column<string>(type: "text", nullable: false),
                    RootCause = table.Column<string>(type: "text", nullable: true),
                    Evidence = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    Confidence = table.Column<decimal>(type: "numeric", nullable: true),
                    Provider = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_failure_analyses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "integrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    IntegrationType = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    SecretReference = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => new { x.ProjectId, x.UserId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: true),
                    TargetUrl = table.Column<string>(type: "text", nullable: true),
                    DefaultEnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Framework = table.Column<string>(type: "text", nullable: true),
                    Platform = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "suite_test_cases",
                columns: table => new
                {
                    SuiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suite_test_cases", x => new { x.SuiteId, x.TestCaseId });
                });

            migrationBuilder.CreateTable(
                name: "test_case_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TestCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceCode = table.Column<string>(type: "text", nullable: true),
                    StructuredSteps = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    GenerationRequest = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    GenerationProvider = table.Column<string>(type: "text", nullable: true),
                    GenerationModel = table.Column<string>(type: "text", nullable: true),
                    GenerationLatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    ReviewStatus = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_case_versions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "test_cases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TestKey = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Module = table.Column<string>(type: "text", nullable: true),
                    Framework = table.Column<string>(type: "text", nullable: true),
                    Platform = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SourceType = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_cases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "test_suites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_suites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ExternalTicketId = table.Column<string>(type: "text", nullable: true),
                    ExternalUrl = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: true),
                    SyncStatus = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalIdentityId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_ProjectId_CreatedAt",
                table: "audit_events",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_defects_ProjectId_Status",
                table: "defects",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_environments_ProjectId",
                table: "environments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_execution_artifacts_ExecutionTestId",
                table: "execution_artifacts",
                column: "ExecutionTestId");

            migrationBuilder.CreateIndex(
                name: "IX_execution_logs_ExecutionTestId_Timestamp",
                table: "execution_logs",
                columns: new[] { "ExecutionTestId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_execution_tests_ExecutionId",
                table: "execution_tests",
                column: "ExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tests_Status",
                table: "execution_tests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_executions_ProjectId_Status",
                table: "executions",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_failure_analyses_ExecutionTestId",
                table: "failure_analyses",
                column: "ExecutionTestId");

            migrationBuilder.CreateIndex(
                name: "IX_integrations_ProjectId",
                table: "integrations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_permissions_Code",
                table: "permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_Key",
                table: "projects",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_roles_Name",
                table: "roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_case_versions_TestCaseId_VersionNumber",
                table: "test_case_versions",
                columns: new[] { "TestCaseId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_cases_ProjectId_Status",
                table: "test_cases",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_test_cases_ProjectId_TestKey",
                table: "test_cases",
                columns: new[] { "ProjectId", "TestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_suites_ProjectId",
                table: "test_suites",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_ProjectId_Provider_ExternalTicketId",
                table: "tickets",
                columns: new[] { "ProjectId", "Provider", "ExternalTicketId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tickets_ProjectId_SyncStatus",
                table: "tickets",
                columns: new[] { "ProjectId", "SyncStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_users_ExternalIdentityId",
                table: "users",
                column: "ExternalIdentityId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "defects");

            migrationBuilder.DropTable(
                name: "environments");

            migrationBuilder.DropTable(
                name: "execution_artifacts");

            migrationBuilder.DropTable(
                name: "execution_logs");

            migrationBuilder.DropTable(
                name: "execution_tests");

            migrationBuilder.DropTable(
                name: "executions");

            migrationBuilder.DropTable(
                name: "failure_analyses");

            migrationBuilder.DropTable(
                name: "integrations");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "suite_test_cases");

            migrationBuilder.DropTable(
                name: "test_case_versions");

            migrationBuilder.DropTable(
                name: "test_cases");

            migrationBuilder.DropTable(
                name: "test_suites");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
