using AiDevControlPanel.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiDevControlPanel.Core.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Tsa> Tsas => Set<Tsa>();

    public DbSet<Issue> Issues => Set<Issue>();

    public DbSet<IssueDependency> IssueDependencies => Set<IssueDependency>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(e =>
        {
            e.Property(p => p.OnboardingState).HasConversion<string>();
            e.HasIndex(p => p.LocalPath).IsUnique();
        });

        modelBuilder.Entity<Tsa>(e =>
        {
            e.Property(t => t.State).HasConversion<string>();
            e.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Issue>(e =>
        {
            e.Property(i => i.State).HasConversion<string>();
            e.Property(i => i.ErrorPhase).HasConversion<string>();
            e.Property(i => i.Type).HasConversion<string>();
            e.HasOne<Project>().WithMany().HasForeignKey(i => i.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Tsa>().WithMany().HasForeignKey(i => i.TsaId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Issue>().WithMany().HasForeignKey(i => i.ParentIssueId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(i => new { i.ProjectId, i.Number });
        });

        modelBuilder.Entity<IssueDependency>(e =>
        {
            e.HasKey(d => new { d.IssueId, d.BlockedByIssueId });
            e.HasOne<Issue>().WithMany().HasForeignKey(d => d.IssueId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Issue>().WithMany().HasForeignKey(d => d.BlockedByIssueId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public static DbContextOptions<AppDbContext> CreateOptions(string databasePath) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={databasePath}").Options;
}

/// <summary>Used by <c>dotnet ef</c> to create migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(AppDbContext.CreateOptions("design-time.db"));
}
