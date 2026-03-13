using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace ARIS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddIsTechToRefSkill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "user_profiles",
                type: "vector(1024)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(384)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "ref_skills",
                type: "vector(1024)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(384)",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_tech",
                table: "ref_skills",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "ref_roles",
                type: "vector(1024)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(384)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "job_postings",
                type: "vector(1024)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(384)",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_tech",
                table: "ref_skills");

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "user_profiles",
                type: "vector(384)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(1024)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "ref_skills",
                type: "vector(384)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(1024)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "ref_roles",
                type: "vector(384)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(1024)",
                oldNullable: true);

            migrationBuilder.AlterColumn<Vector>(
                name: "embedding",
                table: "job_postings",
                type: "vector(384)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(1024)",
                oldNullable: true);
        }
    }
}
