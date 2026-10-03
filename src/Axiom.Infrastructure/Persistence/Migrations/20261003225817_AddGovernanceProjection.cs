using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Axiom.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernanceProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "governance_record",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    statement = table.Column<string>(type: "text", nullable: false),
                    owners = table.Column<List<string>>(type: "text[]", nullable: false),
                    tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    tags_text = table.Column<string>(type: "text", nullable: false),
                    review_after = table.Column<DateOnly>(type: "date", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    blob_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "record_id", "title", "statement", "tags_text" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_record", x => new { x.organization_id, x.record_id });
                });

            migrationBuilder.CreateTable(
                name: "governance_relation",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_relation", x => new { x.organization_id, x.record_id, x.kind, x.target_id });
                });

            migrationBuilder.CreateTable(
                name: "governance_revision",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    blob_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_revision", x => new { x.organization_id, x.record_id, x.revision });
                });

            migrationBuilder.CreateTable(
                name: "governance_scope",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    dimension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    value = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_scope", x => new { x.organization_id, x.record_id, x.dimension, x.value_key });
                });

            migrationBuilder.CreateTable(
                name: "governance_snapshot",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    snapshot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    warning_count = table.Column<int>(type: "integer", nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_snapshot", x => new { x.organization_id, x.source_commit });
                });

            migrationBuilder.CreateTable(
                name: "governance_snapshot_entry",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    from_sequence = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    blob_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_snapshot_entry", x => new { x.organization_id, x.record_id, x.from_sequence });
                });

            migrationBuilder.CreateTable(
                name: "governance_source",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repository_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    root_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_governance_source", x => x.organization_id);
                });

            migrationBuilder.CreateTable(
                name: "sync_checkpoint",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repository_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    root_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    head_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    head_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    published_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    published_sequence = table.Column<int>(type: "integer", nullable: false),
                    snapshot_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_checkpoint", x => x.organization_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_governance_record_kind",
                schema: "axiom",
                table: "governance_record",
                columns: new[] { "organization_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_governance_record_search",
                schema: "axiom",
                table: "governance_record",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_governance_record_status_review",
                schema: "axiom",
                table: "governance_record",
                columns: new[] { "organization_id", "status", "review_after" });

            migrationBuilder.CreateIndex(
                name: "ix_governance_relation_target",
                schema: "axiom",
                table: "governance_relation",
                columns: new[] { "organization_id", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ux_governance_revision_content",
                schema: "axiom",
                table: "governance_revision",
                columns: new[] { "organization_id", "record_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_governance_scope_value",
                schema: "axiom",
                table: "governance_scope",
                columns: new[] { "organization_id", "dimension", "value_key" });

            migrationBuilder.CreateIndex(
                name: "ix_governance_snapshot_id",
                schema: "axiom",
                table: "governance_snapshot",
                columns: new[] { "organization_id", "snapshot_id" });

            migrationBuilder.CreateIndex(
                name: "ux_governance_snapshot_sequence",
                schema: "axiom",
                table: "governance_snapshot",
                columns: new[] { "organization_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "governance_record",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "governance_relation",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "governance_revision",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "governance_scope",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "governance_snapshot",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "governance_snapshot_entry",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "governance_source",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "sync_checkpoint",
                schema: "axiom");
        }
    }
}
