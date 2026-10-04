using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Axiom.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "evaluation",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repository = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(512)", maxLength: 512, nullable: true),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    base_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pull_request = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    snapshot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    harness_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    significant_change = table.Column<bool>(type: "boolean", nullable: false),
                    finding_count = table.Column<int>(type: "integer", nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parent_evaluation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    design_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    receipt_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation", x => new { x.organization_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "evaluation_applied_record",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    evaluation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    importance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_exception = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_applied_record", x => new { x.organization_id, x.evaluation_id, x.record_id });
                });

            migrationBuilder.CreateTable(
                name: "evaluation_finding",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    evaluation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rule_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    waived_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    governance_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluation_finding", x => new { x.organization_id, x.evaluation_id, x.ordinal });
                });

            migrationBuilder.CreateTable(
                name: "receipt",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evaluation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repository = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    snapshot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    previous_chain_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    chain_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_receipt", x => new { x.organization_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "review_decision",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evaluation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    finding_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reviewer = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_decision", x => new { x.organization_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_commit",
                schema: "axiom",
                table: "evaluation",
                columns: new[] { "organization_id", "repository", "commit_sha" });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_created",
                schema: "axiom",
                table: "evaluation",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_applied_record_usage",
                schema: "axiom",
                table: "evaluation_applied_record",
                columns: new[] { "organization_id", "record_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_finding_code",
                schema: "axiom",
                table: "evaluation_finding",
                columns: new[] { "organization_id", "code", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_evaluation_finding_governance",
                schema: "axiom",
                table: "evaluation_finding",
                column: "governance_ids")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_receipt_commit",
                schema: "axiom",
                table: "receipt",
                columns: new[] { "organization_id", "repository", "commit_sha" });

            migrationBuilder.CreateIndex(
                name: "ux_receipt_evaluation",
                schema: "axiom",
                table: "receipt",
                columns: new[] { "organization_id", "evaluation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_receipt_sequence",
                schema: "axiom",
                table: "receipt",
                columns: new[] { "organization_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_decision_evaluation",
                schema: "axiom",
                table: "review_decision",
                columns: new[] { "organization_id", "evaluation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_decision_feedback",
                schema: "axiom",
                table: "review_decision",
                columns: new[] { "organization_id", "kind", "finding_code", "decided_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evaluation",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "evaluation_applied_record",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "evaluation_finding",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "receipt",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "review_decision",
                schema: "axiom");
        }
    }
}
