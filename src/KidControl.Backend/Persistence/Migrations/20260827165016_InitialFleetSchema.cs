using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidControl.Backend.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFleetSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    detail_json = table.Column<string>(type: "jsonb", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "admin",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    telegram_chat_id = table.Column<long>(type: "bigint", nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin", x => x.id);
                    table.ForeignKey(
                        name: "fk_admin_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    group_label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    agent_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    os_info = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enroll_code",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_by_device_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enroll_code", x => x.code);
                    table.ForeignKey(
                        name: "fk_enroll_code_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "command",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    ttl_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command", x => x.id);
                    table.ForeignKey(
                        name: "fk_command_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_desired",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    force_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    night_bypass_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_desired", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_device_desired_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_policy",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    play_minutes = table.Column<int>(type: "integer", nullable: false),
                    rest_minutes = table.Column<int>(type: "integer", nullable: false),
                    night_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    night_start = table.Column<TimeSpan>(type: "interval", nullable: false),
                    night_end = table.Column<TimeSpan>(type: "interval", nullable: false),
                    intervals_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    target_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_policy", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_device_policy_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_status",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    time_remaining = table.Column<TimeSpan>(type: "interval", nullable: false),
                    is_night = table.Column<bool>(type: "boolean", nullable: false),
                    is_unlimited = table.Column<bool>(type: "boolean", nullable: false),
                    shutdown_in_seconds = table.Column<int>(type: "integer", nullable: false),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_status", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_device_status_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "tenant",
                columns: new[] { "id", "name" },
                values: new object[] { new Guid("00000000-0000-0000-0000-0000000f1eef"), "Семья" });

            migrationBuilder.CreateIndex(
                name: "ix_admin_tenant_id_telegram_chat_id",
                table: "admin",
                columns: new[] { "tenant_id", "telegram_chat_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_tenant_id_at",
                table: "audit",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_command_device_id_acked_at",
                table: "command",
                columns: new[] { "device_id", "acked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_device_last_seen_at",
                table: "device",
                column: "last_seen_at");

            migrationBuilder.CreateIndex(
                name: "ix_device_tenant_id",
                table: "device",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_token_hash",
                table: "device",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enroll_code_expires_at",
                table: "enroll_code",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_enroll_code_tenant_id",
                table: "enroll_code",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin");

            migrationBuilder.DropTable(
                name: "audit");

            migrationBuilder.DropTable(
                name: "command");

            migrationBuilder.DropTable(
                name: "device_desired");

            migrationBuilder.DropTable(
                name: "device_policy");

            migrationBuilder.DropTable(
                name: "device_status");

            migrationBuilder.DropTable(
                name: "enroll_code");

            migrationBuilder.DropTable(
                name: "device");

            migrationBuilder.DropTable(
                name: "tenant");
        }
    }
}
