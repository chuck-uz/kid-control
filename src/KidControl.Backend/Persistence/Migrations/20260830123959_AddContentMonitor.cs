using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KidControl.Backend.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContentMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "monitor_context_chars",
                table: "device_policy",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            // RFC-05: the content monitor is ON by default, so existing device policies are
            // backfilled with true (not the CLR default false).
            migrationBuilder.AddColumn<bool>(
                name: "word_monitor_enabled",
                table: "device_policy",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "monitor_meta",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    lists_version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitor_meta", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "monitor_term",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitor_term", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "word_alert",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    term = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_word_alert", x => x.id);
                    table.ForeignKey(
                        name: "fk_word_alert_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_monitor_term_kind",
                table: "monitor_term",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "ix_word_alert_device_id_at",
                table: "word_alert",
                columns: new[] { "device_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monitor_meta");

            migrationBuilder.DropTable(
                name: "monitor_term");

            migrationBuilder.DropTable(
                name: "word_alert");

            migrationBuilder.DropColumn(
                name: "monitor_context_chars",
                table: "device_policy");

            migrationBuilder.DropColumn(
                name: "word_monitor_enabled",
                table: "device_policy");
        }
    }
}
