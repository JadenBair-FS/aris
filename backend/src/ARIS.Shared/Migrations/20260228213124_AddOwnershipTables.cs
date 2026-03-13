using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ARIS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnershipTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "seeker_user_id",
                table: "user_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "recruiter_user_id",
                table: "job_postings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "recruiter_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clerk_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recruiter_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "seeker_users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    clerk_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seeker_users", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_profiles_seeker_user_id",
                table: "user_profiles",
                column: "seeker_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_job_postings_recruiter_user_id",
                table: "job_postings",
                column: "recruiter_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_recruiter_users_clerk_id",
                table: "recruiter_users",
                column: "clerk_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_seeker_users_clerk_id",
                table: "seeker_users",
                column: "clerk_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_job_postings_recruiter_users_recruiter_user_id",
                table: "job_postings",
                column: "recruiter_user_id",
                principalTable: "recruiter_users",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_user_profiles_seeker_users_seeker_user_id",
                table: "user_profiles",
                column: "seeker_user_id",
                principalTable: "seeker_users",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_job_postings_recruiter_users_recruiter_user_id",
                table: "job_postings");

            migrationBuilder.DropForeignKey(
                name: "FK_user_profiles_seeker_users_seeker_user_id",
                table: "user_profiles");

            migrationBuilder.DropTable(
                name: "recruiter_users");

            migrationBuilder.DropTable(
                name: "seeker_users");

            migrationBuilder.DropIndex(
                name: "IX_user_profiles_seeker_user_id",
                table: "user_profiles");

            migrationBuilder.DropIndex(
                name: "IX_job_postings_recruiter_user_id",
                table: "job_postings");

            migrationBuilder.DropColumn(
                name: "seeker_user_id",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "recruiter_user_id",
                table: "job_postings");
        }
    }
}
