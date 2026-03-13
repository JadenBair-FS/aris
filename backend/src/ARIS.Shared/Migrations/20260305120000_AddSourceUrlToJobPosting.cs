using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace ARIS.Shared.Migrations
{
    [DbContext(typeof(ARIS.Shared.Data.ArisDbContext))]
    [Migration("20260305120000_AddSourceUrlToJobPosting")]
    public partial class AddSourceUrlToJobPosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_url",
                table: "job_postings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_url",
                table: "job_postings");
        }
    }
}
