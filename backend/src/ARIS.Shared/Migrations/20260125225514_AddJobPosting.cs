using System;
using ARIS.Shared.Models.CleanSignal;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace ARIS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddJobPosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_postings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recruiter_id = table.Column<string>(type: "text", nullable: false),
                    raw_description = table.Column<string>(type: "text", nullable: true),
                    clean_signal_json = table.Column<JobPostingCleanSignal>(type: "jsonb", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(384)", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_postings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_postings");
        }
    }
}
