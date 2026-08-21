using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certiflow.Compliance.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "compliance");

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "compliance",
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
                schema: "compliance",
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

            migrationBuilder.CreateTable(
                name: "profile_versions",
                schema: "compliance",
                columns: table => new
                {
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    profile_version = table.Column<int>(type: "int", nullable: false),
                    requirements_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profile_versions", x => x.category_id);
                });

            migrationBuilder.CreateTable(
                name: "supplier_compliance",
                schema: "compliance",
                columns: table => new
                {
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    profile_version = table.Column<int>(type: "int", nullable: false),
                    last_evaluated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    overall_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_compliance", x => x.supplier_id);
                });

            migrationBuilder.CreateTable(
                name: "obligations",
                schema: "compliance",
                columns: table => new
                {
                    requirement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    specification_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    is_applicable = table.Column<bool>(type: "bit", nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    current_evidence_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    pending_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    history_json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_obligations", x => new { x.supplier_id, x.requirement_id });
                    table.ForeignKey(
                        name: "FK_obligations_supplier_compliance_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "compliance",
                        principalTable: "supplier_compliance",
                        principalColumn: "supplier_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "compliance",
                table: "outbox",
                column: "occurred_at",
                filter: "[published_at] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "obligations",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "profile_versions",
                schema: "compliance");

            migrationBuilder.DropTable(
                name: "supplier_compliance",
                schema: "compliance");
        }
    }
}
