using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Axiom.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "ix_repository_binding_repository",
                schema: "axiom",
                table: "repository_binding",
                columns: new[] { "organization_id", "repository_ref" });

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
                name: "repository_binding",
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
        }
    }
}
