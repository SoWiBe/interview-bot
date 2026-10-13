using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "hints_used",
                table: "task_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "awaiting_solution_since",
                table: "practice_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "practice_tasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "clarifications",
                table: "practice_tasks",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "practice_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "practice_tasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // уже существующие задачи — обычные, без роли разогрева
                defaultValue: "Main");

            migrationBuilder.AddColumn<string>(
                name: "schema",
                table: "practice_tasks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "task_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_messages_practice_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "practice_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_messages_task_id",
                table: "task_messages",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_messages_telegram_message_id",
                table: "task_messages",
                column: "telegram_message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_messages");

            migrationBuilder.DropColumn(
                name: "hints_used",
                table: "task_attempts");

            migrationBuilder.DropColumn(
                name: "awaiting_solution_since",
                table: "practice_tasks");

            migrationBuilder.DropColumn(
                name: "category",
                table: "practice_tasks");

            migrationBuilder.DropColumn(
                name: "clarifications",
                table: "practice_tasks");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "practice_tasks");

            migrationBuilder.DropColumn(
                name: "role",
                table: "practice_tasks");

            migrationBuilder.DropColumn(
                name: "schema",
                table: "practice_tasks");
        }
    }
}
