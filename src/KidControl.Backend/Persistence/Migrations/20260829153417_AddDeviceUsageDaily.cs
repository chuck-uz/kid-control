using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KidControl.Backend.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceUsageDaily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_usage_daily",
                columns: table => new
                {
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    seconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_usage_daily", x => new { x.device_id, x.day });
                    table.ForeignKey(
                        name: "fk_device_usage_daily_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_usage_daily_day",
                table: "device_usage_daily",
                column: "day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_usage_daily");
        }
    }
}
