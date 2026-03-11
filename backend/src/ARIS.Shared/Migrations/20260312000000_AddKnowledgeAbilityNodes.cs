using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using ARIS.Shared.Data;

#nullable disable

namespace ARIS.Shared.Migrations
{
    [DbContext(typeof(ArisDbContext))]
    [Migration("20260312000000_AddKnowledgeAbilityNodes")]
    public partial class AddKnowledgeAbilityNodes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "job_zone",
                table: "ref_roles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ref_knowledge",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    onet_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Pgvector.Vector>(type: "vector(1024)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_knowledge", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ref_ability",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    onet_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    embedding = table.Column<Pgvector.Vector>(type: "vector(1024)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_ability", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ref_knowledge_onet_id",
                table: "ref_knowledge",
                column: "onet_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ref_ability_onet_id",
                table: "ref_ability",
                column: "onet_id",
                unique: true);

            migrationBuilder.CreateTable(
                name: "ref_role_knowledge",
                columns: table => new
                {
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    knowledge_id = table.Column<int>(type: "integer", nullable: false),
                    importance = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_role_knowledge", x => new { x.role_id, x.knowledge_id });
                    table.ForeignKey(
                        name: "FK_ref_role_knowledge_ref_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "ref_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ref_role_knowledge_ref_knowledge_knowledge_id",
                        column: x => x.knowledge_id,
                        principalTable: "ref_knowledge",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ref_role_ability",
                columns: table => new
                {
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    ability_id = table.Column<int>(type: "integer", nullable: false),
                    importance = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ref_role_ability", x => new { x.role_id, x.ability_id });
                    table.ForeignKey(
                        name: "FK_ref_role_ability_ref_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "ref_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ref_role_ability_ref_ability_ability_id",
                        column: x => x.ability_id,
                        principalTable: "ref_ability",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ref_role_knowledge_knowledge_id",
                table: "ref_role_knowledge",
                column: "knowledge_id");

            migrationBuilder.CreateIndex(
                name: "IX_ref_role_ability_ability_id",
                table: "ref_role_ability",
                column: "ability_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ref_role_knowledge");
            migrationBuilder.DropTable(name: "ref_role_ability");
            migrationBuilder.DropTable(name: "ref_knowledge");
            migrationBuilder.DropTable(name: "ref_ability");

            migrationBuilder.DropColumn(
                name: "job_zone",
                table: "ref_roles");
        }
    }
}
