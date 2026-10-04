using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Axiom.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "axiom");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "catalog_state",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_state", x => x.organization_id);
                });

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
                name: "outbox_event",
                schema: "axiom",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    schema_version = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    traceparent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_event", x => new { x.organization_id, x.event_id });
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
                name: "repository_binding",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    alias = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    alias_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    repository_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repository_binding", x => new { x.organization_id, x.alias, x.alias_type, x.repository_ref });
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

            migrationBuilder.CreateTable(
                name: "software_edge",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    relation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_locator = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    is_fact = table.Column<bool>(type: "boolean", nullable: false),
                    confirmed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_edge", x => new { x.organization_id, x.source_ref, x.relation, x.target_ref });
                });

            migrationBuilder.CreateTable(
                name: "software_edge_observation",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    relation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    source_locator = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_edge_observation", x => new { x.organization_id, x.source_ref, x.relation, x.target_ref, x.source_locator });
                });

            migrationBuilder.CreateTable(
                name: "software_entity",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    technologies = table.Column<string[]>(type: "text[]", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_locator = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    is_fact = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_entity", x => new { x.organization_id, x.entity_ref });
                });

            migrationBuilder.CreateTable(
                name: "software_entity_observation",
                schema: "axiom",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_ref = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    source_locator = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    technologies = table.Column<string[]>(type: "text[]", nullable: false),
                    attributes = table.Column<string>(type: "jsonb", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_entity_observation", x => new { x.organization_id, x.entity_ref, x.source_locator });
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

            migrationBuilder.CreateIndex(
                name: "ix_outbox_event_pending",
                schema: "axiom",
                table: "outbox_event",
                column: "occurred_at",
                filter: "dispatched_at IS NULL");

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
                name: "ix_repository_binding_repository",
                schema: "axiom",
                table: "repository_binding",
                columns: new[] { "organization_id", "repository_ref" });

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

            migrationBuilder.CreateIndex(
                name: "ix_software_edge_relation",
                schema: "axiom",
                table: "software_edge",
                columns: new[] { "organization_id", "relation" });

            migrationBuilder.CreateIndex(
                name: "ix_software_edge_reverse",
                schema: "axiom",
                table: "software_edge",
                columns: new[] { "organization_id", "target_ref", "relation", "source_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_software_edge_unconfirmed",
                schema: "axiom",
                table: "software_edge",
                column: "organization_id",
                filter: "NOT is_fact");

            migrationBuilder.CreateIndex(
                name: "ix_software_edge_observation_locator",
                schema: "axiom",
                table: "software_edge_observation",
                columns: new[] { "organization_id", "source_locator" });

            migrationBuilder.CreateIndex(
                name: "ix_software_entity_kind",
                schema: "axiom",
                table: "software_entity",
                columns: new[] { "organization_id", "kind", "entity_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_software_entity_last_seen",
                schema: "axiom",
                table: "software_entity",
                columns: new[] { "organization_id", "last_seen" });

            migrationBuilder.CreateIndex(
                name: "ix_software_entity_observation_locator",
                schema: "axiom",
                table: "software_entity_observation",
                columns: new[] { "organization_id", "source_locator" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_state",
                schema: "axiom");

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
                name: "outbox_event",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "receipt",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "repository_binding",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "review_decision",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "software_edge",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "software_edge_observation",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "software_entity",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "software_entity_observation",
                schema: "axiom");

            migrationBuilder.DropTable(
                name: "sync_checkpoint",
                schema: "axiom");
        }
    }
}
