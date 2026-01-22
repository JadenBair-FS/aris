using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;

namespace ARIS.Ingestor.Data;

public class ArisDbContext : DbContext
{
    public ArisDbContext(DbContextOptions<ArisDbContext> options) : base(options)
    {
    }

    public DbSet<RefSkill> Skills { get; set; }
    public DbSet<RefRole> Roles { get; set; }
    public DbSet<RefRoleSkill> RoleSkills { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enable pgvector extension
        modelBuilder.HasPostgresExtension("vector");

        // Force lowercase table names for Postgres compatibility
        modelBuilder.Entity<RefSkill>().ToTable("ref_skills");
        modelBuilder.Entity<RefRole>().ToTable("ref_roles");
        modelBuilder.Entity<RefRoleSkill>().ToTable("ref_role_skills");

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
            
        // Unique constraints for names to prevent duplicates
        modelBuilder.Entity<RefSkill>()
            .HasIndex(s => s.Name)
            .IsUnique();

        modelBuilder.Entity<RefRole>()
            .HasIndex(r => r.OnetCode)
            .IsUnique();
    }
}
