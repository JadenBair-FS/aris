using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pgvector;
using ARIS.Shared.Data;

#nullable disable

namespace ARIS.Shared.Migrations
{
    [DbContext(typeof(ArisDbContext))]
    [Migration("20260306000000_AddCandidateSkills")]
    public partial class AddCandidateSkills : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidate_skills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    domain_prefix = table.Column<string>(type: "text", nullable: true),
                    is_tech = table.Column<bool>(type: "boolean", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    observation_count = table.Column<int>(type: "integer", nullable: false),
                    source_document_ids = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    promoted_skill_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_skills", x => x.id);
                });

            // Expression-based unique index: UNIQUE constraint syntax doesn't support COALESCE, must use CREATE UNIQUE INDEX
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX UQ_candidate_skills_name_domain
                ON candidate_skills (normalized_name, COALESCE(domain_prefix, ''));
            ");

            migrationBuilder.CreateIndex(
                name: "IX_candidate_skills_status",
                table: "candidate_skills",
                column: "status");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS UQ_candidate_skills_name_domain;");
            migrationBuilder.DropTable(name: "candidate_skills");
        }
    }
}
