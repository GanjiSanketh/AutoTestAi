using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoTestAi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Slice6_FailureAnalysisAndDefects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_failure_analyses_ExecutionTestId",
                table: "failure_analyses");

            migrationBuilder.AlterColumn<string>(
                name: "RootCause",
                table: "failure_analyses",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "failure_analyses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Model",
                table: "failure_analyses",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Assumptions",
                table: "failure_analyses",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "Attempt",
                table: "failure_analyses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "failure_analyses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                table: "failure_analyses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsLikelyDefect",
                table: "failure_analyses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "LatencyMs",
                table: "failure_analyses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                table: "failure_analyses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptVersion",
                table: "failure_analyses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecommendedAction",
                table: "failure_analyses",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "failure_analyses",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "failure_analyses",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalTokens",
                table: "failure_analyses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Warnings",
                table: "failure_analyses",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "defects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FailureAnalysisId",
                table: "defects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_failure_analyses_ExecutionTestId",
                table: "failure_analyses",
                column: "ExecutionTestId",
                unique: true,
                filter: "\"Status\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_failure_analyses_ExecutionTestId_Attempt",
                table: "failure_analyses",
                columns: new[] { "ExecutionTestId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_defects_ExecutionTestId",
                table: "defects",
                column: "ExecutionTestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_failure_analyses_ExecutionTestId",
                table: "failure_analyses");

            migrationBuilder.DropIndex(
                name: "IX_failure_analyses_ExecutionTestId_Attempt",
                table: "failure_analyses");

            migrationBuilder.DropIndex(
                name: "IX_defects_ExecutionTestId",
                table: "defects");

            migrationBuilder.DropColumn(
                name: "Assumptions",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "Attempt",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "IsLikelyDefect",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "PromptVersion",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "RecommendedAction",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "Warnings",
                table: "failure_analyses");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "defects");

            migrationBuilder.DropColumn(
                name: "FailureAnalysisId",
                table: "defects");

            migrationBuilder.AlterColumn<string>(
                name: "RootCause",
                table: "failure_analyses",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "failure_analyses",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Model",
                table: "failure_analyses",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_failure_analyses_ExecutionTestId",
                table: "failure_analyses",
                column: "ExecutionTestId");
        }
    }
}
