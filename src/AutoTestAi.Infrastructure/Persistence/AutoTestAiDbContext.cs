using System.Text.Json;
using AutoTestAi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AutoTestAi.Infrastructure.Persistence;

/// <summary>
/// EF Core model for docs/05-Database-Design.md.
/// Table names mirror the logical design; enums are stored as strings for readability.
/// </summary>
public sealed class AutoTestAiDbContext : DbContext
{
    public AutoTestAiDbContext(DbContextOptions<AutoTestAiDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<TestEnvironment> Environments => Set<TestEnvironment>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<TestCaseVersion> TestCaseVersions => Set<TestCaseVersion>();
    public DbSet<TestSuite> TestSuites => Set<TestSuite>();
    public DbSet<SuiteTestCase> SuiteTestCases => Set<SuiteTestCase>();
    public DbSet<Execution> Executions => Set<Execution>();
    public DbSet<ExecutionTest> ExecutionTests => Set<ExecutionTest>();
    public DbSet<ExecutionStepResult> ExecutionStepResults => Set<ExecutionStepResult>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();
    public DbSet<ExecutionArtifact> ExecutionArtifacts => Set<ExecutionArtifact>();
    public DbSet<FailureAnalysis> FailureAnalyses => Set<FailureAnalysis>();
    public DbSet<Defect> Defects => Set<Defect>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- identity ---
        modelBuilder.Entity<User>().ToTable("users");
        modelBuilder.Entity<User>().HasIndex(u => u.ExternalIdentityId).IsUnique();
        modelBuilder.Entity<Role>().ToTable("roles");
        modelBuilder.Entity<Role>().HasIndex(r => r.Name).IsUnique();
        modelBuilder.Entity<Permission>().ToTable("permissions");
        modelBuilder.Entity<Permission>().HasIndex(p => p.Code).IsUnique();
        modelBuilder.Entity<UserRole>().ToTable("user_roles").HasKey(x => new { x.UserId, x.RoleId });

        // --- projects ---
        modelBuilder.Entity<Project>().ToTable("projects");
        modelBuilder.Entity<Project>().HasIndex(p => p.Key).IsUnique();
        modelBuilder.Entity<Project>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<ProjectMember>().ToTable("project_members")
            .HasKey(x => new { x.ProjectId, x.UserId, x.RoleId });
        modelBuilder.Entity<TestEnvironment>().ToTable("environments");
        modelBuilder.Entity<TestEnvironment>().HasIndex(e => e.ProjectId);

        // --- test repository ---
        modelBuilder.Entity<TestCase>().ToTable("test_cases");
        modelBuilder.Entity<TestCase>().HasIndex(t => new { t.ProjectId, t.TestKey }).IsUnique();
        modelBuilder.Entity<TestCase>().HasIndex(t => new { t.ProjectId, t.Status });
        modelBuilder.Entity<TestCase>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<TestCase>().Property(t => t.Priority).HasConversion<string>();
        modelBuilder.Entity<TestCaseVersion>().ToTable("test_case_versions");
        modelBuilder.Entity<TestCaseVersion>()
            .HasIndex(v => new { v.TestCaseId, v.VersionNumber }).IsUnique();
        modelBuilder.Entity<TestCaseVersion>().Property(v => v.StructuredSteps).HasColumnType("jsonb");
        modelBuilder.Entity<TestCaseVersion>().Property(v => v.GenerationRequest).HasColumnType("jsonb");
        modelBuilder.Entity<TestCaseVersion>().Property(v => v.ReviewStatus).HasConversion<string>();
        modelBuilder.Entity<TestSuite>().ToTable("test_suites");
        modelBuilder.Entity<TestSuite>().HasIndex(s => s.ProjectId);
        modelBuilder.Entity<TestSuite>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<SuiteTestCase>().ToTable("suite_test_cases")
            .HasKey(x => new { x.SuiteId, x.TestCaseId });

        // --- execution ---
        modelBuilder.Entity<Execution>().ToTable("executions");
        modelBuilder.Entity<Execution>().HasIndex(e => new { e.ProjectId, e.Status });
        modelBuilder.Entity<Execution>()
            .HasIndex(e => new { e.ProjectId, e.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        modelBuilder.Entity<Execution>().Property(e => e.Status).HasConversion<string>();
        modelBuilder.Entity<Execution>().Property(e => e.TriggerType).HasConversion<string>();
        modelBuilder.Entity<Execution>().Property(e => e.IdempotencyKey).HasMaxLength(100);
        modelBuilder.Entity<ExecutionTest>().ToTable("execution_tests");
        modelBuilder.Entity<ExecutionTest>().HasIndex(e => e.ExecutionId);
        modelBuilder.Entity<ExecutionTest>().HasIndex(e => e.Status);
        modelBuilder.Entity<ExecutionTest>().Property(e => e.Status).HasConversion<string>();
        modelBuilder.Entity<ExecutionTest>().Property(e => e.FailureClassification).HasConversion<string>();
        modelBuilder.Entity<ExecutionTest>().Property(e => e.Framework).HasMaxLength(100);
        modelBuilder.Entity<ExecutionTest>().Property(e => e.Browser).HasMaxLength(50);
        modelBuilder.Entity<ExecutionStepResult>().ToTable("execution_step_results");
        modelBuilder.Entity<ExecutionStepResult>()
            .HasIndex(s => new { s.ExecutionTestId, s.StepOrder });
        modelBuilder.Entity<ExecutionStepResult>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<ExecutionStepResult>().Property(s => s.Action).HasMaxLength(200);
        modelBuilder.Entity<ExecutionLog>().ToTable("execution_logs");
        modelBuilder.Entity<ExecutionLog>().Property(e => e.Id).UseIdentityByDefaultColumn();
        modelBuilder.Entity<ExecutionLog>()
            .HasIndex(e => new { e.ExecutionTestId, e.Timestamp });
        modelBuilder.Entity<ExecutionLog>().Property(e => e.Metadata).HasColumnType("jsonb");
        modelBuilder.Entity<ExecutionArtifact>().ToTable("execution_artifacts");
        modelBuilder.Entity<ExecutionArtifact>().HasIndex(a => a.ExecutionTestId);
        modelBuilder.Entity<ExecutionArtifact>().Property(a => a.FileName).HasMaxLength(255);
        modelBuilder.Entity<FailureAnalysis>().ToTable("failure_analyses");
        modelBuilder.Entity<FailureAnalysis>().HasIndex(f => f.ExecutionTestId);
        modelBuilder.Entity<FailureAnalysis>()
            .HasIndex(f => f.ExecutionTestId)
            .IsUnique()
            .HasFilter("\"Status\" = 'Running'");
        modelBuilder.Entity<FailureAnalysis>().HasIndex(f => new { f.ExecutionTestId, f.Attempt }).IsUnique();
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.Status).HasConversion<string>();
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.Classification).HasConversion<string>();
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.Evidence).HasColumnType("jsonb");
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.Summary).HasMaxLength(500);
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.RootCause).HasMaxLength(2000);
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.RecommendedAction).HasMaxLength(500);
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.PromptVersion).HasMaxLength(100);
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.Model).HasMaxLength(200);
        modelBuilder.Entity<FailureAnalysis>().Property(f => f.Provider).HasMaxLength(100);

        // --- defects / tickets / integrations ---
        modelBuilder.Entity<Defect>().ToTable("defects");
        modelBuilder.Entity<Defect>().HasIndex(d => new { d.ProjectId, d.Status });
        modelBuilder.Entity<Defect>().HasIndex(d => d.ExecutionTestId);
        modelBuilder.Entity<Defect>().Property(d => d.Status).HasConversion<string>();
        modelBuilder.Entity<Defect>().Property(d => d.Severity).HasConversion<string>();
        modelBuilder.Entity<Defect>().Property(d => d.RootCauseType).HasConversion<string>();
        modelBuilder.Entity<Ticket>().ToTable("tickets");
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => new { t.ProjectId, t.Provider, t.ExternalTicketId }).IsUnique();
        modelBuilder.Entity<Ticket>().HasIndex(t => new { t.ProjectId, t.SyncStatus });
        modelBuilder.Entity<Ticket>().HasIndex(t => t.DefectId);
        // Slice 7: one successful Jira ticket per defect per integration.
        // Only Synced rows participate so failed attempts remain retryable.
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => new { t.DefectId, t.IntegrationId })
            .IsUnique()
            .HasFilter("\"DefectId\" IS NOT NULL AND \"IntegrationId\" IS NOT NULL AND \"SyncStatus\" = 'Synced'");
        modelBuilder.Entity<Ticket>().Property(t => t.SyncStatus).HasConversion<string>();
        modelBuilder.Entity<Ticket>().Property(t => t.ExternalKey).HasMaxLength(50);
        modelBuilder.Entity<Ticket>().Property(t => t.LastError).HasMaxLength(2000);
        modelBuilder.Entity<Integration>().ToTable("integrations");
        modelBuilder.Entity<Integration>().HasIndex(i => i.ProjectId);
        // Slice 7: at most one Jira integration row per project.
        modelBuilder.Entity<Integration>()
            .HasIndex(i => new { i.ProjectId, i.Provider })
            .IsUnique()
            .HasFilter("\"ProjectId\" IS NOT NULL");
        modelBuilder.Entity<Integration>().Property(i => i.Configuration).HasColumnType("jsonb");
        modelBuilder.Entity<Integration>().Property(i => i.Status).HasConversion<string>();

        ApplyInMemoryJsonCompatibility(modelBuilder);

        // --- audit ---
        modelBuilder.Entity<AuditEvent>().ToTable("audit_events");
        modelBuilder.Entity<AuditEvent>().Property(e => e.Id).UseIdentityByDefaultColumn();
        modelBuilder.Entity<AuditEvent>().HasIndex(e => new { e.ProjectId, e.CreatedAt });
        modelBuilder.Entity<AuditEvent>().Property(e => e.MetadataJson).HasColumnType("jsonb");
    }

    /// <summary>
    /// The InMemory provider used by integration tests has no JsonDocument mapping
    /// (production Npgsql maps these properties to jsonb, configured above).
    /// This compat shim only activates for the InMemory provider.
    /// </summary>
    private void ApplyInMemoryJsonCompatibility(ModelBuilder modelBuilder)
    {
        if (!string.Equals(
                Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
            return;

        var converter = new ValueConverter<JsonDocument, string?>(
            document => document == null ? null : document.RootElement.GetRawText(),
            json => string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json));
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entityType.GetProperties())
                if (property.ClrType == typeof(JsonDocument))
                    property.SetValueConverter(converter);
    }
}
