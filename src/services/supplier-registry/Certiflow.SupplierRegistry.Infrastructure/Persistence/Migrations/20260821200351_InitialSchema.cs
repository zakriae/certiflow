using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Certiflow.SupplierRegistry.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "registry");

            migrationBuilder.CreateTable(
                name: "compliance_profiles",
                schema: "registry",
                columns: table => new
                {
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    published_version = table.Column<int>(type: "int", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    has_unpublished_changes = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_profiles", x => x.category_id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "registry",
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
                schema: "registry",
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
                name: "suppliers",
                schema: "registry",
                columns: table => new
                {
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    legal_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    trading_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    registration_number = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    registration_number_normalized = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    country_code = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.supplier_id);
                });

            migrationBuilder.CreateTable(
                name: "requirements",
                schema: "registry",
                columns: table => new
                {
                    requirement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    document_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    is_mandatory = table.Column<bool>(type: "bit", nullable: false),
                    renewal_lead_time_days = table.Column<int>(type: "int", nullable: false),
                    min_validity_days = table.Column<int>(type: "int", nullable: false),
                    requires_issuer_match = table.Column<bool>(type: "bit", nullable: false),
                    accepted_issuers = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    auto_accept_threshold = table.Column<decimal>(type: "decimal(3,2)", precision: 3, scale: 2, nullable: false),
                    is_deprecated = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirements", x => new { x.category_id, x.requirement_id });
                    table.ForeignKey(
                        name: "FK_requirements_compliance_profiles_category_id",
                        column: x => x.category_id,
                        principalSchema: "registry",
                        principalTable: "compliance_profiles",
                        principalColumn: "category_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "supplier_contacts",
                schema: "registry",
                columns: table => new
                {
                    contact_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    role = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    is_primary = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_contacts", x => new { x.supplier_id, x.contact_id });
                    table.ForeignKey(
                        name: "FK_supplier_contacts_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalSchema: "registry",
                        principalTable: "suppliers",
                        principalColumn: "supplier_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "registry",
                table: "outbox",
                column: "occurred_at",
                filter: "[published_at] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "requirements",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "supplier_contacts",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "compliance_profiles",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "suppliers",
                schema: "registry");
        }
    }
}
