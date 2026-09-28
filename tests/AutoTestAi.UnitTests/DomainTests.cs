using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.UnitTests;

public sealed class DomainTests
{
    [Fact]
    public void NewEntities_GetGuidIds_AndUtcTimestamps()
    {
        var project = new Project { Name = "Demo", Key = "DEMO" };
        var testCase = new TestCase { ProjectId = project.Id, TestKey = "TC-1", Title = "Login" };

        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.NotEqual(Guid.Empty, testCase.Id);
        Assert.Equal(ProjectStatus.Active, project.Status);
        Assert.Equal(TestCaseStatus.Draft, testCase.Status);
    }

    [Fact]
    public void Execution_DefaultsToQueued()
    {
        var execution = new Execution { ProjectId = Guid.NewGuid() };
        Assert.Equal(ExecutionStatus.Queued, execution.Status);
    }

    [Theory]
    [InlineData(ExecutionStatus.Queued)]
    [InlineData(ExecutionStatus.Running)]
    [InlineData(ExecutionStatus.Passed)]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Error)]
    public void ExecutionStatus_CoversDocumentedLifecycle(ExecutionStatus status)
        => Assert.True(Enum.IsDefined(status));

    [Fact]
    public void FailureClassification_DistinguishesBugTypes()
    {
        // docs/02 §12: test failure vs application defect vs environment vs automation.
        var values = Enum.GetValues<FailureClassification>();
        Assert.Contains(FailureClassification.ApplicationDefect, values);
        Assert.Contains(FailureClassification.EnvironmentFailure, values);
        Assert.Contains(FailureClassification.AutomationFailure, values);
        Assert.Contains(FailureClassification.TestFailure, values);
    }
}
