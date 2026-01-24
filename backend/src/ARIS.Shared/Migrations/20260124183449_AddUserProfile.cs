using System;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace ARIS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            /*
            migrationBuilder.CreateTable(
                name: "ref_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    onet_code = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(384)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ref_skills",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(384)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_skills", x => x.id);
                });
            */

            migrationBuilder.CreateTable(
                name: "user_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    raw_resume = table.Column<string>(type: "jsonb", nullable: true),
                    clean_signal_json = table.Column<ResumeCleanSignal>(type: "jsonb", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(384)", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_profiles", x => x.id);
                });

            /*
            migrationBuilder.CreateTable(
                name: "ref_role_skills",
                columns: table => new
                {
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    skill_id = table.Column<int>(type: "integer", nullable: false),
                    importance = table.Column<double>(type: "double precision", nullable: true),
                    level = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_role_skills", x => new { x.role_id, x.skill_id });
                    table.ForeignKey(
                        name: "FK_ref_role_skills_ref_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "ref_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ref_role_skills_ref_skills_skill_id",
                        column: x => x.skill_id,
                        principalTable: "ref_skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ref_role_skills_skill_id",
                table: "ref_role_skills",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "IX_ref_roles_onet_code",
                table: "ref_roles",
                column: "onet_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ref_skills_name",
                table: "ref_skills",
                column: "name",
                unique: true);
            */
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ref_role_skills");

            migrationBuilder.DropTable(
                name: "user_profiles");

            migrationBuilder.DropTable(
                name: "ref_roles");

            migrationBuilder.DropTable(
                name: "ref_skills");
        }
    }
}
