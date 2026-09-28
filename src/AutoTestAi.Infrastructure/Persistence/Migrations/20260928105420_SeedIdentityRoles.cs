using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Seeds the BRD predefined roles (FR-1.1). Fixed ids keep membership
    /// references stable across environments.
    /// </summary>
    public partial class SeedIdentityRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var now = new DateTimeOffset(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc));
            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "Id", "Name", "Description", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), "admin", "Platform administrator (FR-1.1)", now, now },
                    { new Guid("22222222-2222-2222-2222-222222222222"), "qa-lead", "QA lead / manager (FR-1.1)", now, now },
                    { new Guid("33333333-3333-3333-3333-333333333333"), "tester", "Test automation engineer / QA tester (FR-1.1)", now, now },
                    { new Guid("44444444-4444-4444-4444-444444444444"), "viewer", "Read-only viewer (FR-1.1)", now, now },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "roles", keyColumn: "Id", keyValue: new Guid("11111111-1111-1111-1111-111111111111"));
            migrationBuilder.DeleteData(table: "roles", keyColumn: "Id", keyValue: new Guid("22222222-2222-2222-2222-222222222222"));
            migrationBuilder.DeleteData(table: "roles", keyColumn: "Id", keyValue: new Guid("33333333-3333-3333-3333-333333333333"));
            migrationBuilder.DeleteData(table: "roles", keyColumn: "Id", keyValue: new Guid("44444444-4444-4444-4444-444444444444"));
        }
    }
}
