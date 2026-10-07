using System.Text.Json;
using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

public sealed class TestCase : EntityBase
{
    public Guid ProjectId { get; set; }
    public string TestKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Module { get; set; }
    public string? Framework { get; set; }
    public string? Platform { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public TestCaseStatus Status { get; set; } = TestCaseStatus.Draft;
    public string? SourceType { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class TestCaseVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TestCaseId { get; set; }
    public int VersionNumber { get; set; }
    public string? SourceCode { get; set; }

    /// <summary>Structured test steps (JSONB).</summary>
    public JsonDocument? StructuredSteps { get; set; }

    /// <summary>Original AI generation request payload (JSONB).</summary>
    public JsonDocument? GenerationRequest { get; set; }

    public string? GenerationProvider { get; set; }
    public string? GenerationModel { get; set; }
    public long? GenerationLatencyMs { get; set; }
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TestSuite : EntityBase
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public Guid? CreatedBy { get; set; }
}

public sealed class SuiteTestCase
{
    public Guid SuiteId { get; set; }
    public Guid TestCaseId { get; set; }
    public int ExecutionOrder { get; set; }
}

/// <summary>
/// Recurring execution cadence for a test suite (Phase 4 Slice 9B).
/// Temporal owns firing/next-run; this row owns identity, cadence config,
/// lifecycle, and last-run pointers. Never carries secrets or credentials.
/// </summary>
public sealed class TestSuiteSchedule : EntityBase
{
    public Guid ProjectId { get; set; }
    public Guid SuiteId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Active;
    public ScheduleOverlapPolicy OverlapPolicy { get; set; } = ScheduleOverlapPolicy.Skip;
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public Guid? LastExecutionId { get; set; }
    public Guid? CreatedBy { get; set; }
}
