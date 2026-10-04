using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InterviewBot.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Таблицы персистентного хранилища Quartz.NET 3.x. DDL — официальный скрипт
    /// quartznet/database/tables/tables_postgres.sql (без блока DROP), лежит рядом как embedded resource.
    /// </summary>
    public partial class QuartzTables : Migration
    {
        private static readonly string[] Tables =
        [
            "qrtz_fired_triggers", "qrtz_paused_trigger_grps", "qrtz_scheduler_state", "qrtz_locks",
            "qrtz_simprop_triggers", "qrtz_simple_triggers", "qrtz_cron_triggers", "qrtz_blob_triggers",
            "qrtz_triggers", "qrtz_job_details", "qrtz_calendars",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            using var stream = typeof(QuartzTables).Assembly.GetManifestResourceStream(
                "InterviewBot.Infrastructure.Persistence.quartz_tables_postgres.sql")
                ?? throw new InvalidOperationException("Quartz DDL resource not found");
            using var reader = new StreamReader(stream);

            migrationBuilder.Sql(reader.ReadToEnd());
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"DROP TABLE IF EXISTS {table};");
            }
        }
    }
}
