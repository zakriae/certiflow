using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certiflow.Intelligence.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "intelligence");

            migrationBuilder.CreateTable(
                name: "extraction_jobs",
                schema: "intelligence",
                columns: table => new
                {
                    extraction_job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requirement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "int", nullable: false),
                    model_used = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    prompt_version = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    tokens_consumed = table.Column<int>(type: "int", nullable: false),
                    overall_confidence = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    auto_accept_threshold = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    is_auto_acceptable = table.Column<bool>(type: "bit", nullable: false),
                    text_source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    failure_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    fields_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extraction_jobs", x => x.extraction_job_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "intelligence",
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
                schema: "intelligence",
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
                name: "requirements",
                schema: "intelligence",
                columns: table => new
                {
                    requirement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    requires_issuer_match = table.Column<bool>(type: "bit", nullable: false),
                    accepted_issuers = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    auto_accept_threshold = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirements", x => x.requirement_id);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                schema: "intelligence",
                columns: table => new
                {
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    legal_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    trading_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.supplier_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_jobs_document",
                schema: "intelligence",
                table: "extraction_jobs",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "intelligence",
                table: "outbox",
                column: "occurred_at",
                filter: "[published_at] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extraction_jobs",
                schema: "intelligence");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "intelligence");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "intelligence");

            migrationBuilder.DropTable(
                name: "requirements",
                schema: "intelligence");

            migrationBuilder.DropTable(
                name: "suppliers",
                schema: "intelligence");
        }
    }
}
