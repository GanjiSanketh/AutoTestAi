using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice9_ExecutionTestStartedAssignmentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignmentId",
                table: "execution_tests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StartedAssignmentId",
                table: "execution_tests",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignmentId",
                table: "execution_tests");

            migrationBuilder.DropColumn(
                name: "StartedAssignmentId",
                table: "execution_tests");
        }
    }
}
