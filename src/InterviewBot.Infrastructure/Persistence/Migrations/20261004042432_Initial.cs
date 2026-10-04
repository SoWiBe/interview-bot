using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "day_logs",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_day_logs", x => x.date);
                });

            migrationBuilder.CreateTable(
                name: "topics",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    module = table.Column<int>(type: "integer", nullable: false),
                    module_title = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    code_refs = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topics", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false),
                    chat_id = table.Column<long>(type: "bigint", nullable: false),
                    send_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    weekend_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    paused_until = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_settings", x => x.telegram_user_id);
                });

            migrationBuilder.CreateTable(
                name: "digests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    for_date = table.Column<DateOnly>(type: "date", nullable: false),
                    topic_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    theory_html = table.Column<string>(type: "text", nullable: true),
                    snippet_html = table.Column<string>(type: "text", nullable: true),
                    snippet_permalink = table.Column<string>(type: "text", nullable: true),
                    feedback = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    theory_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    snippet_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tasks_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_digests", x => x.id);
                    table.ForeignKey(
                        name: "fk_digests_topics_topic_id",
                        column: x => x.topic_id,
                        principalTable: "topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "topic_progress",
                columns: table => new
                {
                    topic_id = table.Column<string>(type: "character varying(100)", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    stage = table.Column<int>(type: "integer", nullable: false),
                    next_review_on = table.Column<DateOnly>(type: "date", nullable: true),
                    last_shown_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status_changed_on = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topic_progress", x => x.topic_id);
                    table.ForeignKey(
                        name: "fk_topic_progress_topics_topic_id",
                        column: x => x.topic_id,
                        principalTable: "topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "practice_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    topic_id = table.Column<string>(type: "text", nullable: false),
                    order_in_digest = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    statement = table.Column<string>(type: "text", nullable: false),
                    expected_questions = table.Column<List<string>>(type: "text[]", nullable: false),
                    reference_solution = table.Column<string>(type: "text", nullable: false),
                    solution_explanation = table.Column<string>(type: "text", nullable: false),
                    complexity = table.Column<string>(type: "text", nullable: false),
                    telegram_message_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    solution_revealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_practice_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_practice_tasks_digests_digest_id",
                        column: x => x.digest_id,
                        principalTable: "digests",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "task_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    asked_clarifying_questions = table.Column<bool>(type: "boolean", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    review_html = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_attempts_practice_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "practice_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_digests_for_date",
                table: "digests",
                column: "for_date",
                unique: true,
                filter: "kind = 'Daily'");

            migrationBuilder.CreateIndex(
                name: "ix_digests_topic_id",
                table: "digests",
                column: "topic_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_tasks_created_at",
                table: "practice_tasks",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_practice_tasks_digest_id",
                table: "practice_tasks",
                column: "digest_id");

            migrationBuilder.CreateIndex(
                name: "ix_practice_tasks_telegram_message_id",
                table: "practice_tasks",
                column: "telegram_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_attempts_task_id",
                table: "task_attempts",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "day_logs");

            migrationBuilder.DropTable(
                name: "task_attempts");

            migrationBuilder.DropTable(
                name: "topic_progress");

            migrationBuilder.DropTable(
                name: "user_settings");

            migrationBuilder.DropTable(
                name: "practice_tasks");

            migrationBuilder.DropTable(
                name: "digests");

            migrationBuilder.DropTable(
                name: "topics");
        }
    }
}
