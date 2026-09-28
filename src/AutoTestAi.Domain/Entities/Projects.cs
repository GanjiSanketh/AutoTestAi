using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

public sealed class Project : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? TargetUrl { get; set; }
    public Guid? DefaultEnvironmentId { get; set; }
    public string? Framework { get; set; }
    public string? Platform { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    public Guid? CreatedBy { get; set; }
}

/// <summary>
/// Named execution environment. Secret *values* are never stored here —
/// only external secret references (docs/05 §environments).
/// </summary>
public sealed class TestEnvironment : EntityBase
{
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
}
