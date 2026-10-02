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
    public DbSet<GridWorker> GridWorkers => Set<GridWorker>();
    public DbSet<GridAssignment> GridAssignments => Set<GridAssignment>();
    public DbSet<Defect> Defects => Set<Defect>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<AutoTicketPolicy> AutoTicketPolicies => Set<AutoTicketPolicy>();
    public DbSet<SelfHealingPolicy> SelfHealingPolicies => Set<SelfHealingPolicy>();
    public DbSet<SelfHealingAttempt> SelfHealingAttempts => Set<SelfHealingAttempt>();
    public DbSet<VariableSet> VariableSets => Set<VariableSet>();
    public DbSet<EnvironmentSecret> EnvironmentSecrets => Set<EnvironmentSecret>();
    public DbSet<ExecutionVariables> ExecutionVariables => Set<ExecutionVariables>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<MobileDevicePool> MobileDevicePools => Set<MobileDevicePool>();
    public DbSet<MobileDevice> MobileDevices => Set<MobileDevice>();
    public DbSet<MobileDeviceSlot> MobileDeviceSlots => Set<MobileDeviceSlot>();
    public DbSet<MobileDeviceSession> MobileDeviceSessions => Set<MobileDeviceSession>();
    public DbSet<MobileApp> MobileApps => Set<MobileApp>();
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
        // Slice 12: every analytics/execution-history range predicate filters
        // (ProjectId, CreatedAt); the composite keeps window scans index-only.
        modelBuilder.Entity<Execution>()
            .HasIndex(e => new { e.ProjectId, e.CreatedAt })
            .HasDatabaseName("IX_executions_Project_Created");
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

        // --- execution grid (Phase 2 Slice 9) ---
        modelBuilder.Entity<GridWorker>().ToTable("grid_workers");
        modelBuilder.Entity<GridWorker>().HasIndex(w => w.WorkerKey).IsUnique();
        modelBuilder.Entity<GridWorker>().HasIndex(w => w.Status);
        modelBuilder.Entity<GridWorker>().HasIndex(w => w.LastHeartbeatAt);
        modelBuilder.Entity<GridWorker>().Property(w => w.WorkerKey).HasMaxLength(100);
        modelBuilder.Entity<GridWorker>().Property(w => w.DisplayName).HasMaxLength(200);
        modelBuilder.Entity<GridWorker>().Property(w => w.WorkerType).HasMaxLength(50);
        modelBuilder.Entity<GridWorker>().Property(w => w.Framework).HasMaxLength(50);
        modelBuilder.Entity<GridWorker>().Property(w => w.Version).HasMaxLength(50);
        modelBuilder.Entity<GridWorker>().Property(w => w.Status).HasConversion<string>();
        modelBuilder.Entity<GridWorker>().Property(w => w.BaseUrl).HasMaxLength(500);
        modelBuilder.Entity<GridWorker>().Property(w => w.CredentialHash).HasMaxLength(128);
        modelBuilder.Entity<GridWorker>().Property(w => w.CredentialSalt).HasMaxLength(64);
        modelBuilder.Entity<GridWorker>().Property(w => w.RowVersion).IsConcurrencyToken();
        modelBuilder.Entity<GridAssignment>().ToTable("grid_assignments");
        modelBuilder.Entity<GridAssignment>().HasIndex(a => a.ExecutionId);
        modelBuilder.Entity<GridAssignment>().HasIndex(a => a.ExecutionTestId);
        modelBuilder.Entity<GridAssignment>().HasIndex(a => a.WorkerId);
        modelBuilder.Entity<GridAssignment>().HasIndex(a => a.Status);
        modelBuilder.Entity<GridAssignment>().HasIndex(a => a.ExpiresAt);
        // One active lease per execution test: concurrent claims collide here.
        modelBuilder.Entity<GridAssignment>()
            .HasIndex(a => a.ExecutionTestId)
            .IsUnique()
            .HasFilter("\"Status\" IN ('Claimed', 'Running')");
        modelBuilder.Entity<GridAssignment>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<GridAssignment>().Property(a => a.WorkerAssignmentRef).HasMaxLength(64);

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
        modelBuilder.Entity<Ticket>().Property(t => t.Origin).HasConversion<string>();
        modelBuilder.Entity<Ticket>().Property(t => t.ExternalKey).HasMaxLength(50);
        modelBuilder.Entity<Ticket>().Property(t => t.LastError).HasMaxLength(2000);
        // Slice 10: automation retry/reconciliation queries.
        modelBuilder.Entity<Ticket>().HasIndex(t => new { t.ProjectId, t.SyncStatus, t.NextAttemptAt });
        // Slice 10: cross-instance automation lease (optimistic compare-and-set).
        modelBuilder.Entity<Ticket>().Property(t => t.RowVersion).IsConcurrencyToken();
        // Slice 10: at most one Pending intent per defect per integration, so
        // concurrent triggers across API instances converge instead of
        // executing Jira twice. Failed rows stay retryable via reuse.
        // (Distinct name: the Slice 7 Synced-only index is untouched.)
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => new { t.DefectId, t.IntegrationId }, "IX_tickets_DefectId_IntegrationId_Pending")
            .IsUnique()
            .HasFilter("\"DefectId\" IS NOT NULL AND \"IntegrationId\" IS NOT NULL AND \"SyncStatus\" = 'Pending'");
        modelBuilder.Entity<Integration>().ToTable("integrations");
        modelBuilder.Entity<Integration>().HasIndex(i => i.ProjectId);
        // Slice 7: at most one Jira integration row per project.
        modelBuilder.Entity<Integration>()
            .HasIndex(i => new { i.ProjectId, i.Provider })
            .IsUnique()
            .HasFilter("\"ProjectId\" IS NOT NULL");
        modelBuilder.Entity<Integration>().Property(i => i.Configuration).HasColumnType("jsonb");
        modelBuilder.Entity<Integration>().Property(i => i.Status).HasConversion<string>();

        // --- auto-ticket policy (Phase 2 Slice 10) ---
        modelBuilder.Entity<AutoTicketPolicy>().ToTable("auto_ticket_policies");
        // One active policy per project: automation scope is always project-local.
        modelBuilder.Entity<AutoTicketPolicy>().HasIndex(p => p.ProjectId).IsUnique();
        modelBuilder.Entity<AutoTicketPolicy>().Property(p => p.Severities).HasMaxLength(200);
        modelBuilder.Entity<AutoTicketPolicy>().Property(p => p.DefectStatuses).HasMaxLength(200);
        modelBuilder.Entity<AutoTicketPolicy>().Property(p => p.Classifications).HasMaxLength(200);

        // --- self-healing (Phase 2 Slice 11) ---
        modelBuilder.Entity<SelfHealingPolicy>().ToTable("self_healing_policies");
        // One policy per project: healing scope is always project-local.
        modelBuilder.Entity<SelfHealingPolicy>().HasIndex(p => p.ProjectId).IsUnique();
        modelBuilder.Entity<SelfHealingPolicy>().Property(p => p.AllowedStrategies).HasMaxLength(200);
        modelBuilder.Entity<SelfHealingAttempt>().ToTable("self_healing_attempts");
        modelBuilder.Entity<SelfHealingAttempt>().HasIndex(a => a.ProjectId);
        // Slice 12: healing analytics filter (ProjectId, CreatedAt) windows.
        modelBuilder.Entity<SelfHealingAttempt>()
            .HasIndex(a => new { a.ProjectId, a.CreatedAt })
            .HasDatabaseName("IX_healing_Project_Created");
        modelBuilder.Entity<SelfHealingAttempt>().HasIndex(a => a.ExecutionId);
        modelBuilder.Entity<SelfHealingAttempt>().HasIndex(a => a.TestCaseVersionId);
        modelBuilder.Entity<SelfHealingAttempt>().HasIndex(a => a.CreatedAt);
        // One authoritative outcome row per (execution test, step): concurrent
        // workers converge instead of duplicating healing state.
        modelBuilder.Entity<SelfHealingAttempt>()
            .HasIndex(a => new { a.ExecutionTestId, a.StepOrder }).IsUnique();
        modelBuilder.Entity<SelfHealingAttempt>().Property(a => a.Status).HasConversion<string>();
        modelBuilder.Entity<SelfHealingAttempt>().Property(a => a.HealingStrategy).HasConversion<string>();
        modelBuilder.Entity<SelfHealingAttempt>().Property(a => a.StepAction).HasMaxLength(200);
        modelBuilder.Entity<SelfHealingAttempt>().Property(a => a.OriginalStrategy).HasMaxLength(50);
        modelBuilder.Entity<SelfHealingAttempt>().Property(a => a.RecoveredStrategy).HasMaxLength(50);
        modelBuilder.Entity<SelfHealingAttempt>().Property(a => a.ErrorMessage).HasMaxLength(2000);

        ApplyInMemoryJsonCompatibility(modelBuilder);

        // --- variables & secrets (Phase 3 Slice 3A, additive) ---
        modelBuilder.Entity<VariableSet>().ToTable("variable_sets");
        modelBuilder.Entity<VariableSet>().HasIndex(v => v.ProjectId);
        modelBuilder.Entity<VariableSet>().HasIndex(v => new { v.ProjectId, v.ScopeType, v.ScopeId });
        // One project-scope set per project.
        modelBuilder.Entity<VariableSet>()
            .HasIndex(v => v.ProjectId)
            .IsUnique()
            .HasDatabaseName("IX_variable_sets_Project_Once")
            .HasFilter("\"ScopeType\" = 'Project'");
        // One set per environment/suite scope.
        modelBuilder.Entity<VariableSet>()
            .HasIndex(v => new { v.ProjectId, v.ScopeType, v.ScopeId })
            .IsUnique()
            .HasDatabaseName("IX_variable_sets_Scope_Once")
            .HasFilter("\"ScopeId\" IS NOT NULL");
        modelBuilder.Entity<VariableSet>().Property(v => v.ScopeType).HasConversion<string>();
        modelBuilder.Entity<VariableSet>().Property(v => v.Name).HasMaxLength(200);
        modelBuilder.Entity<VariableSet>().Property(v => v.RowVersion).IsRowVersion();
        modelBuilder.Entity<EnvironmentSecret>().ToTable("environment_secrets");
        modelBuilder.Entity<EnvironmentSecret>().HasIndex(s => s.ProjectId);
        modelBuilder.Entity<EnvironmentSecret>().HasIndex(s => new { s.ProjectId, s.EnvironmentId });
        modelBuilder.Entity<EnvironmentSecret>()
            .HasIndex(s => new { s.ProjectId, s.EnvironmentId, s.Name }).IsUnique();
        modelBuilder.Entity<EnvironmentSecret>().Property(s => s.Name).HasMaxLength(200);
        modelBuilder.Entity<EnvironmentSecret>().Property(s => s.SecretReference).HasMaxLength(256);
        modelBuilder.Entity<EnvironmentSecret>().Property(s => s.KeyVersion).HasMaxLength(50);
        modelBuilder.Entity<EnvironmentSecret>().Property(s => s.RowVersion).IsRowVersion();
        modelBuilder.Entity<ExecutionVariables>().ToTable("execution_variables");
        modelBuilder.Entity<ExecutionVariables>().HasKey(e => e.ExecutionId);

        // --- CI/CD webhook deliveries (Phase 3 Slice 3B, additive) ---
        modelBuilder.Entity<WebhookDelivery>().ToTable("webhook_deliveries");
        // Authoritative duplicate boundary: provider delivery ids are scoped
        // per integration, never per project alone.
        modelBuilder.Entity<WebhookDelivery>()
            .HasIndex(d => new { d.IntegrationId, d.DeliveryId })
            .IsUnique()
            .HasDatabaseName("IX_webhook_deliveries_Integration_Delivery");
        modelBuilder.Entity<WebhookDelivery>().HasIndex(d => d.ProjectId);
        modelBuilder.Entity<WebhookDelivery>().HasIndex(d => d.IntegrationId);
        modelBuilder.Entity<WebhookDelivery>().HasIndex(d => d.ProcessingStatus);
        modelBuilder.Entity<WebhookDelivery>().HasIndex(d => d.ReceivedAt);
        modelBuilder.Entity<WebhookDelivery>().HasIndex(d => d.ExecutionId);
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.Provider).HasMaxLength(50);
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.DeliveryId).HasMaxLength(200);
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.EventType).HasMaxLength(200);
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.PayloadHash).HasMaxLength(128);
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.FailureReason).HasMaxLength(500);
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.VerificationStatus).HasConversion<string>();
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.ProcessingStatus).HasConversion<string>();
        modelBuilder.Entity<WebhookDelivery>().Property(d => d.RowVersion).IsConcurrencyToken();

        // --- mobile registry (Phase 3 Slice 3C-1/3C-2, additive) ---
        // No cascade deletes: pools/devices/apps will be referenced by
        // executions and sessions, so relationships are Restrict. Registry
        // CRUD uses administrative disable instead of destructive deletion.
        modelBuilder.Entity<MobileDevicePool>().ToTable("mobile_device_pools");
        modelBuilder.Entity<MobileDevicePool>().HasIndex(p => p.ProjectId);
        modelBuilder.Entity<MobileDevicePool>().HasIndex(p => p.Status);
        modelBuilder.Entity<MobileDevicePool>()
            .HasIndex(p => new { p.ProjectId, p.Name })
            .IsUnique()
            .HasDatabaseName("IX_mobile_pools_Project_Name");
        modelBuilder.Entity<MobileDevicePool>().Property(p => p.Name).HasMaxLength(200);
        modelBuilder.Entity<MobileDevicePool>().Property(p => p.Platform).HasConversion<string>();
        modelBuilder.Entity<MobileDevicePool>().Property(p => p.Status).HasConversion<string>();
        modelBuilder.Entity<MobileDevicePool>().Property(p => p.RowVersion).IsRowVersion();

        modelBuilder.Entity<MobileDevice>().ToTable("mobile_devices");
        modelBuilder.Entity<MobileDevice>().HasIndex(d => d.ProjectId);
        modelBuilder.Entity<MobileDevice>().HasIndex(d => d.PoolId);
        modelBuilder.Entity<MobileDevice>().HasIndex(d => new { d.ProjectId, d.Status });
        modelBuilder.Entity<MobileDevice>().HasIndex(d => d.Platform);
        // One UDID per project when supplied (nulls never collide).
        modelBuilder.Entity<MobileDevice>()
            .HasIndex(d => new { d.ProjectId, d.Udid })
            .IsUnique()
            .HasDatabaseName("IX_mobile_devices_Project_Udid")
            .HasFilter("\"Udid\" IS NOT NULL");
        modelBuilder.Entity<MobileDevice>()
            .HasOne<MobileDevicePool>()
            .WithMany()
            .HasForeignKey(d => d.PoolId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MobileDevice>().Property(d => d.Platform).HasConversion<string>();
        modelBuilder.Entity<MobileDevice>().Property(d => d.Status).HasConversion<string>();
        modelBuilder.Entity<MobileDevice>().Property(d => d.PlatformVersion).HasMaxLength(100);
        modelBuilder.Entity<MobileDevice>().Property(d => d.Manufacturer).HasMaxLength(200);
        modelBuilder.Entity<MobileDevice>().Property(d => d.Model).HasMaxLength(200);
        modelBuilder.Entity<MobileDevice>().Property(d => d.Udid).HasMaxLength(200);
        modelBuilder.Entity<MobileDevice>().Property(d => d.AutomationName).HasMaxLength(100);
        modelBuilder.Entity<MobileDevice>().Property(d => d.RowVersion).IsRowVersion();

        modelBuilder.Entity<MobileDeviceSlot>().ToTable("mobile_device_slots");
        modelBuilder.Entity<MobileDeviceSlot>().HasIndex(s => s.PoolId);
        modelBuilder.Entity<MobileDeviceSlot>().HasIndex(s => s.DeviceId);
        modelBuilder.Entity<MobileDeviceSlot>().HasIndex(s => s.Status);
        // Deterministic slot identity: no duplicate slot numbers per device.
        modelBuilder.Entity<MobileDeviceSlot>()
            .HasIndex(s => new { s.DeviceId, s.SlotNumber })
            .IsUnique()
            .HasDatabaseName("IX_mobile_slots_Device_SlotNumber");
        // Slice 3C-3 scheduling scans: eligible slots per pool, reaper
        // scans by status/expiry, and renew/release/reap linkage checks
        // from the linked assignment side.
        modelBuilder.Entity<MobileDeviceSlot>()
            .HasIndex(s => new { s.PoolId, s.Status })
            .HasDatabaseName("IX_mobile_slots_Pool_Status");
        modelBuilder.Entity<MobileDeviceSlot>()
            .HasIndex(s => new { s.Status, s.ClaimExpiresAt })
            .HasDatabaseName("IX_mobile_slots_Status_ClaimExpiresAt");
        modelBuilder.Entity<MobileDeviceSlot>()
            .HasIndex(s => s.AssignmentId)
            .HasDatabaseName("IX_mobile_slots_AssignmentId");
        modelBuilder.Entity<MobileDeviceSlot>()
            .HasOne<MobileDevice>()
            .WithMany()
            .HasForeignKey(s => s.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MobileDeviceSlot>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<MobileDeviceSlot>().Property(s => s.RowVersion).IsRowVersion();

        modelBuilder.Entity<MobileDeviceSession>().ToTable("mobile_device_sessions");
        modelBuilder.Entity<MobileDeviceSession>().HasIndex(s => s.DeviceSlotId);
        modelBuilder.Entity<MobileDeviceSession>().HasIndex(s => s.ExecutionId);
        modelBuilder.Entity<MobileDeviceSession>().HasIndex(s => s.AssignmentId);
        modelBuilder.Entity<MobileDeviceSession>().HasIndex(s => s.Status);
        modelBuilder.Entity<MobileDeviceSession>()
            .HasOne<MobileDeviceSlot>()
            .WithMany()
            .HasForeignKey(s => s.DeviceSlotId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MobileDeviceSession>().Property(s => s.AppiumSessionId).HasMaxLength(200);
        modelBuilder.Entity<MobileDeviceSession>().Property(s => s.Status).HasConversion<string>();
        modelBuilder.Entity<MobileDeviceSession>().Property(s => s.RowVersion).IsRowVersion();

        modelBuilder.Entity<MobileApp>().ToTable("mobile_apps");
        modelBuilder.Entity<MobileApp>().HasIndex(a => a.ProjectId);
        modelBuilder.Entity<MobileApp>().HasIndex(a => new { a.ProjectId, a.Platform });
        // One Android package / iOS bundle per project when supplied.
        modelBuilder.Entity<MobileApp>()
            .HasIndex(a => new { a.ProjectId, a.Platform, a.PackageId })
            .IsUnique()
            .HasDatabaseName("IX_mobile_apps_Project_Package")
            .HasFilter("\"PackageId\" IS NOT NULL");
        modelBuilder.Entity<MobileApp>()
            .HasIndex(a => new { a.ProjectId, a.Platform, a.BundleId })
            .IsUnique()
            .HasDatabaseName("IX_mobile_apps_Project_Bundle")
            .HasFilter("\"BundleId\" IS NOT NULL");
        modelBuilder.Entity<MobileApp>().Property(a => a.Platform).HasConversion<string>();
        modelBuilder.Entity<MobileApp>().Property(a => a.InstallPolicy).HasConversion<string>();
        modelBuilder.Entity<MobileApp>().Property(a => a.Name).HasMaxLength(200);
        modelBuilder.Entity<MobileApp>().Property(a => a.PackageId).HasMaxLength(300);
        modelBuilder.Entity<MobileApp>().Property(a => a.BundleId).HasMaxLength(300);
        modelBuilder.Entity<MobileApp>().Property(a => a.Version).HasMaxLength(100);
        modelBuilder.Entity<MobileApp>().Property(a => a.StorageKey).HasMaxLength(500);
        modelBuilder.Entity<MobileApp>().Property(a => a.LaunchActivity).HasMaxLength(500);
        modelBuilder.Entity<MobileApp>().Property(a => a.DeepLink).HasMaxLength(2000);
        modelBuilder.Entity<MobileApp>().Property(a => a.RowVersion).IsRowVersion();

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
