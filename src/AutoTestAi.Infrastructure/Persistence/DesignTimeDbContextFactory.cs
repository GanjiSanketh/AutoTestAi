using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AutoTestAi.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core migrations (scripts/new-migration.ps1).
/// Uses a placeholder connection string — migrations do not need a live database.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AutoTestAiDbContext>
{
    public AutoTestAiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AutoTestAiDbContext>()
            .UseNpgsql("Host=localhost;Database=autotestai;Username=postgres;Password=postgres")
            .Options;
        return new AutoTestAiDbContext(options);
    }
}
