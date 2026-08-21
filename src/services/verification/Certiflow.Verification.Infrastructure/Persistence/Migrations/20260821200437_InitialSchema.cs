using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certiflow.Verification.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "verification");

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "verification",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_name = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    uploaded_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    stored_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents", x => x.document_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "verification",
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
                schema: "verification",
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
                name: "review_tasks",
                schema: "verification",
                columns: table => new
                {
                    review_task_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    extraction_job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    requirement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    uploaded_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    raised_reason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    overall_confidence = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    current_evidence_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    assigned_to = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    verdict_decision = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    verdict_reason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    verdict_reason_note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    verdict_decided_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    verdict_decided_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    cancellation_reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_tasks", x => x.review_task_id);
                });

            migrationBuilder.CreateTable(
                name: "field_reviews",
                schema: "verification",
                columns: table => new
                {
                    field_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    review_task_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    suggested_value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    confidence = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    is_mandatory = table.Column<bool>(type: "bit", nullable: false),
                    citation_page = table.Column<int>(type: "int", nullable: true),
                    citation_snippet = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    scoring_note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    accepted_value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    was_corrected = table.Column<bool>(type: "bit", nullable: false),
                    reviewer_note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    resolved_by = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_reviews", x => new { x.review_task_id, x.field_name });
                    table.ForeignKey(
                        name: "FK_field_reviews_review_tasks_review_task_id",
                        column: x => x.review_task_id,
                        principalSchema: "verification",
                        principalTable: "review_tasks",
                        principalColumn: "review_task_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "verification",
                table: "outbox",
                column: "occurred_at",
                filter: "[published_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_review_tasks_document",
                schema: "verification",
                table: "review_tasks",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_tasks_status",
                schema: "verification",
                table: "review_tasks",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "field_reviews",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "verification");

            migrationBuilder.DropTable(
                name: "review_tasks",
                schema: "verification");
        }
    }
}
