using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace ARIS.Shared.Data;

public class ArisDbContext : DbContext
{
    public ArisDbContext(DbContextOptions<ArisDbContext> options) : base(options)
    {
    }

    public DbSet<RefSkill> Skills { get; set; }
    public DbSet<RefRole> Roles { get; set; }
    public DbSet<RefRoleSkill> RoleSkills { get; set; }
    public DbSet<UserProfile> UserProfiles { get; set; }
    public DbSet<JobPosting> JobPostings { get; set; }
    public DbSet<SeekerUser> SeekerUsers { get; set; }
    public DbSet<RecruiterUser> RecruiterUsers { get; set; }
    public DbSet<CandidateSkill> CandidateSkills { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<RefSkill>().ToTable("ref_skills");
        modelBuilder.Entity<RefRole>().ToTable("ref_roles");
        modelBuilder.Entity<RefRoleSkill>().ToTable("ref_role_skills");
        modelBuilder.Entity<UserProfile>().ToTable("user_profiles");
        modelBuilder.Entity<JobPosting>().ToTable("job_postings");
        modelBuilder.Entity<SeekerUser>().ToTable("seeker_users");
        modelBuilder.Entity<RecruiterUser>().ToTable("recruiter_users");
        modelBuilder.Entity<CandidateSkill>().ToTable("candidate_skills");

        modelBuilder.Entity<CandidateSkill>()
            .HasIndex(c => c.Status)
            .HasDatabaseName("IX_candidate_skills_status");

        modelBuilder.Entity<RefRoleSkill>()
            .HasKey(rs => new { rs.RoleId, rs.SkillId });

        modelBuilder.Entity<RefRoleSkill>()
            .HasOne(rs => rs.Role)
            .WithMany(r => r.RoleSkills)
            .HasForeignKey(rs => rs.RoleId);

        modelBuilder.Entity<RefRoleSkill>()
            .HasOne(rs => rs.Skill)
            .WithMany()
            .HasForeignKey(rs => rs.SkillId);
            
        modelBuilder.Entity<RefSkill>()
            .HasIndex(s => s.Name)
            .IsUnique();

        modelBuilder.Entity<RefRole>()
            .HasIndex(r => r.OnetCode)
            .IsUnique();

        modelBuilder.Entity<SeekerUser>()
            .HasIndex(s => s.ClerkId)
            .IsUnique();

        modelBuilder.Entity<RecruiterUser>()
            .HasIndex(r => r.ClerkId)
            .IsUnique();

        modelBuilder.Entity<UserProfile>()
            .HasOne(u => u.SeekerUser)
            .WithMany()
            .HasForeignKey(u => u.SeekerUserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<JobPosting>()
            .HasOne(j => j.RecruiterUser)
            .WithMany()
            .HasForeignKey(j => j.RecruiterUserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
