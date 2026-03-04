using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ARIS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRecruiterProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_postings_recruiter_profiles_recruiter_profile_id",
                table: "job_postings");

            // Migrate data: populate recruiter_user_id from the recruiter_profiles → recruiter_users chain
            migrationBuilder.Sql(@"
                ALTER TABLE job_postings ADD COLUMN IF NOT EXISTS recruiter_user_id uuid;
                UPDATE job_postings jp
                SET recruiter_user_id = ru.id
                FROM recruiter_profiles rp
                JOIN recruiter_users ru ON ru.clerk_id = rp.clerk_id
                WHERE jp.recruiter_profile_id = rp.id;
            ");

            migrationBuilder.DropIndex(
                name: "IX_job_postings_recruiter_profile_id",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "recruiter_profile_id",
                table: "job_postings");

            migrationBuilder.DropTable(
                name: "recruiter_profiles");

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_recruiter_user_id",
                table: "job_postings",
                column: "recruiter_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_job_postings_recruiter_users_recruiter_user_id",
                table: "job_postings",
                column: "recruiter_user_id",
                principalTable: "recruiter_users",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_postings_recruiter_users_recruiter_user_id",
                table: "job_postings");

            migrationBuilder.DropIndex(
                name: "IX_job_postings_recruiter_user_id",
                table: "job_postings");

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

            migrationBuilder.AddColumn<Guid>(
                name: "recruiter_profile_id",
                table: "job_postings",
                type: "uuid",
                nullable: true);

            migrationBuilder.DropColumn(
                name: "recruiter_user_id",
                table: "job_postings");

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_recruiter_profile_id",
                table: "job_postings",
                column: "recruiter_profile_id");

            migrationBuilder.AddForeignKey(
                name: "FK_job_postings_recruiter_profiles_recruiter_profile_id",
                table: "job_postings",
                column: "recruiter_profile_id",
                principalTable: "recruiter_profiles",
                principalColumn: "id");
        }
    }
}
