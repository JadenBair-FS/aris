using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ARIS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddRecruiterProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_postings_recruiter_users_recruiter_user_id",
                table: "job_postings");

            migrationBuilder.RenameColumn(
                name: "recruiter_user_id",
                table: "job_postings",
                newName: "recruiter_profile_id");

            migrationBuilder.RenameIndex(
                name: "IX_job_postings_recruiter_user_id",
                table: "job_postings",
                newName: "IX_job_postings_recruiter_profile_id");

            migrationBuilder.CreateTable(
                name: "recruiter_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clerk_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recruiter_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recruiter_profiles_clerk_id",
                table: "recruiter_profiles",
                column: "clerk_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_job_postings_recruiter_profiles_recruiter_profile_id",
                table: "job_postings",
                column: "recruiter_profile_id",
                principalTable: "recruiter_profiles",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_postings_recruiter_profiles_recruiter_profile_id",
                table: "job_postings");

            migrationBuilder.DropTable(
                name: "recruiter_profiles");

            migrationBuilder.RenameColumn(
                name: "recruiter_profile_id",
                table: "job_postings",
                newName: "recruiter_user_id");

            migrationBuilder.RenameIndex(
                name: "IX_job_postings_recruiter_profile_id",
                table: "job_postings",
                newName: "IX_job_postings_recruiter_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_job_postings_recruiter_users_recruiter_user_id",
                table: "job_postings",
                column: "recruiter_user_id",
                principalTable: "recruiter_users",
                principalColumn: "id");
        }
    }
}
