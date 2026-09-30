using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice3A_VariablesAndSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_secrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SecretReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    EncryptedValue = table.Column<string>(type: "text", nullable: true),
                    Nonce = table.Column<string>(type: "text", nullable: true),
                    KeyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "execution_variables",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    VariableOverridesJson = table.Column<string>(type: "text", nullable: false),
                    SecretRefOverridesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_variables", x => x.ExecutionId);
                });

            migrationBuilder.CreateTable(
                name: "variable_sets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeType = table.Column<string>(type: "text", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VariablesJson = table.Column<string>(type: "text", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_variable_sets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_secrets_ProjectId",
                table: "environment_secrets",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_environment_secrets_ProjectId_EnvironmentId",
                table: "environment_secrets",
                columns: new[] { "ProjectId", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_secrets_ProjectId_EnvironmentId_Name",
                table: "environment_secrets",
                columns: new[] { "ProjectId", "EnvironmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_variable_sets_Project_Once",
                table: "variable_sets",
                column: "ProjectId",
                unique: true,
                filter: "\"ScopeType\" = 'Project'");

            migrationBuilder.CreateIndex(
                name: "IX_variable_sets_Scope_Once",
                table: "variable_sets",
                columns: new[] { "ProjectId", "ScopeType", "ScopeId" },
                unique: true,
                filter: "\"ScopeId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_secrets");

            migrationBuilder.DropTable(
                name: "execution_variables");

            migrationBuilder.DropTable(
                name: "variable_sets");
        }
    }
}
