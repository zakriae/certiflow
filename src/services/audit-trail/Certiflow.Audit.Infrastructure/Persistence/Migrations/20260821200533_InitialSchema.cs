using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certiflow.Audit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "audit",
                columns: table => new
                {
                    entry_id = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    correlation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    payload_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    previous_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    entry_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.entry_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "audit",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_type = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "audit",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_type = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    payload_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    publish_attempts = table.Column<int>(type: "int", nullable: false),
                    last_error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_correlation",
                schema: "audit",
                table: "entries",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity",
                schema: "audit",
                table: "entries",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_occurred",
                schema: "audit",
                table: "entries",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "audit",
                table: "outbox",
                column: "occurred_at",
                filter: "[published_at] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "audit");
        }
    }
}
