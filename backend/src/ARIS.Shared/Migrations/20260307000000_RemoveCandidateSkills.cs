using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ARIS.Shared.Data;

#nullable disable

namespace ARIS.Shared.Migrations
{
    [DbContext(typeof(ArisDbContext))]
    [Migration("20260307000000_RemoveCandidateSkills")]
    public partial class RemoveCandidateSkills : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS UQ_candidate_skills_name_domain;");
            migrationBuilder.DropTable(name: "candidate_skills");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // candidate_skills table is permanently retired — no rollback
        }
    }
}
