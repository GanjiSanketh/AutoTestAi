using AutoTestAi.Application.Maintenance;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.UnitTests;

/// <summary>Phase 4 Slice 4: deterministic maintenance detector — triggers,
/// exclusions, grouping, confidence, and determinism. Pure function tests:
/// no HTTP, no database.</summary>
public sealed class MaintenanceDetectorTests
{
    private static readonly Guid Project = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Test = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Version = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static MaintenanceDetector.EvidenceRow Row(
        Guid executionId,
        DateTimeOffset at,
        string originalValue = "#login",
        string proposedValue = "testLoginBtn",
        string originalStrategy = "css",
        string proposedStrategy = "testid",
        int stepOrder = 3,
        Guid? versionId = null,
        bool wasApplied = true,
        SelfHealingStatus status = SelfHealingStatus.Applied,
        bool aiAssisted = false,
        ExecutionStatus executionStatus = ExecutionStatus.Passed,
        FailureClassification classification = FailureClassification.TestFailure,
        string stepAction = "click",
        SelfHealingStrategy healingStrategy = SelfHealingStrategy.TestAttribute)
        => new(
            Guid.NewGuid(), Project, Test, versionId ?? Version,
            stepOrder, stepAction, originalStrategy, originalValue,
            proposedStrategy, proposedValue, aiAssisted, wasApplied, status,
            healingStrategy,
            executionId, executionStatus, classification, at);

    private static IReadOnlyDictionary<Guid, MaintenanceDetector.VersionSnapshot> Versions(
        Guid? versionId = null,
        ReviewStatus review = ReviewStatus.Approved,
        string target = "css=#login",
        int stepOrder = 3,
        string action = "click")
        => new Dictionary<Guid, MaintenanceDetector.VersionSnapshot>
        {
            [Test] = new(versionId ?? Version, review,
                new[] { new MaintenanceDetector.VersionStep(stepOrder, action, target) }),
        };

    private static MaintenanceDetector.DetectionResult Detect(
        IReadOnlyList<MaintenanceDetector.EvidenceRow> rows,
        IReadOnlyDictionary<Guid, MaintenanceDetector.VersionSnapshot>? versions = null,
        IReadOnlySet<Guid>? excluded = null,
        IReadOnlyDictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>? verdicts = null,
        IReadOnlyDictionary<Guid, string?>? bands = null)
        => MaintenanceDetector.Detect(
            Project, Now, rows,
            versions ?? Versions(),
            excluded ?? new HashSet<Guid>(),
            verdicts ?? new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>(),
            bands ?? new Dictionary<Guid, string?>());

    private static List<MaintenanceDetector.EvidenceRow> ThreeHeals(
        string proposedValue = "testLoginBtn",
        DateTimeOffset? start = null)
    {
        var at = start ?? Now.AddDays(-1);
        return new()
        {
            Row(Guid.Parse("11111111-1111-1111-1111-111111111111"), at),
            Row(Guid.Parse("22222222-2222-2222-2222-222222222222"), at.AddHours(1), proposedValue: proposedValue),
            Row(Guid.Parse("33333333-3333-3333-3333-333333333333"), at.AddHours(2), proposedValue: proposedValue),
        };
    }

    [Fact]
    public void ThreeQualifyingOccurrences_YieldCandidate()
    {
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var result = Detect(ThreeHeals(), verdicts: verdicts);
        Assert.Single(result.Candidates);
        var candidate = result.Candidates[0];
        Assert.Equal(Test, candidate.TestCaseId);
        Assert.Equal(Version, candidate.TestCaseVersionId);
        Assert.Equal(3, candidate.StepOrder);
        Assert.Equal("css", candidate.OriginalStrategy);
        Assert.Equal("#login", candidate.OriginalValue);
        Assert.Equal("testid", candidate.ProposedStrategy);
        Assert.Equal("testLoginBtn", candidate.ProposedValue);
        Assert.Equal(3, candidate.OccurrenceCount);
    }

    [Fact]
    public void TwoOccurrences_YieldNothing()
    {
        var rows = ThreeHeals().Take(2).ToList();
        var result = Detect(rows);
        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.SkippedGroups);
    }

    [Fact]
    public void SameOriginal_DifferentProposals_YieldNothing()
    {
        var at = Now.AddDays(-1);
        var rows = new List<MaintenanceDetector.EvidenceRow>
        {
            Row(Guid.NewGuid(), at, proposedValue: "aaa"),
            Row(Guid.NewGuid(), at.AddHours(1), proposedValue: "bbb"),
            Row(Guid.NewGuid(), at.AddHours(2), proposedValue: "aaa"),
            Row(Guid.NewGuid(), at.AddHours(3), proposedValue: "bbb"),
        };
        var result = Detect(rows);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void OnlyQualifyingPairSurvives()
    {
        var at = Now.AddDays(-1);
        var rows = new List<MaintenanceDetector.EvidenceRow>
        {
            Row(Guid.Parse("11111111-1111-1111-1111-111111111111"), at, proposedValue: "aaa"),
            Row(Guid.Parse("22222222-2222-2222-2222-222222222222"), at.AddHours(1), proposedValue: "aaa"),
            Row(Guid.Parse("33333333-3333-3333-3333-333333333333"), at.AddHours(2), proposedValue: "aaa"),
            Row(Guid.Parse("44444444-4444-4444-4444-444444444444"), at.AddHours(3), proposedValue: "bbb"),
            Row(Guid.Parse("55555555-5555-5555-5555-555555555555"), at.AddHours(4), proposedValue: "bbb"),
        };
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var result = Detect(rows, verdicts: verdicts);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("aaa", candidate.ProposedValue);
    }

    [Fact]
    public void EvidenceOutside30Days_Excluded()
    {
        var result = Detect(ThreeHeals(start: Now.AddDays(-31)));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void EvidenceBeyondLatest10Executions_Excluded()
    {
        var rows = new List<MaintenanceDetector.EvidenceRow>();
        for (var i = 0; i < 12; i++)
            rows.Add(Row(Guid.NewGuid(), Now.AddDays(-1).AddMinutes(-i)));
        // Newest 10 executions qualify only if the pair repeats there; here
        // every execution heals, so newest-10 still has 10 occurrences.
        var result = Detect(rows);
        Assert.Single(result.Candidates);
        Assert.Equal(10, result.Candidates[0].OccurrenceCount);
    }

    [Fact]
    public void OldestExecutionsBeyondCap_Ignored()
    {
        // 3 recent heals of pair B + 9 older heals of pair A: only B (n=3)
        // qualifies; A is present but identical-pair counting uses all rows
        // in window — A would also qualify. Build distinct scenario: A pair
        // occurs only in executions 11-12 (outside newest 10 for its group).
        var rows = new List<MaintenanceDetector.EvidenceRow>();
        for (var i = 0; i < 10; i++)
            rows.Add(Row(Guid.NewGuid(), Now.AddDays(-1).AddMinutes(-i), proposedValue: "newbie"));
        rows.Add(Row(Guid.NewGuid(), Now.AddDays(-2), proposedValue: "oldie"));
        rows.Add(Row(Guid.NewGuid(), Now.AddDays(-2).AddHours(-1), proposedValue: "oldie"));
        var result = Detect(rows);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("newbie", candidate.ProposedValue);
        Assert.Equal(10, candidate.OccurrenceCount);
    }

    [Fact]
    public void DuplicateTelemetryInOneExecution_CountsOnce()
    {
        var exec = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var at = Now.AddDays(-1);
        var rows = new List<MaintenanceDetector.EvidenceRow>
        {
            Row(exec, at),
            Row(exec, at.AddMinutes(1)),
            Row(Guid.Parse("22222222-2222-2222-2222-222222222222"), at.AddHours(1)),
            Row(Guid.Parse("33333333-3333-3333-3333-333333333333"), at.AddHours(2)),
        };
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var result = Detect(rows, verdicts: verdicts);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(3, candidate.OccurrenceCount);
    }

    [Fact]
    public void AiOnlyEvidence_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with { IsAiAssisted = true }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void HealingStrategyNone_Excluded()
    {
        // HealingStrategy.None must not be treated as trusted deterministic evidence.
        var at = Now.AddDays(-1);
        var rows = new List<MaintenanceDetector.EvidenceRow>
        {
            Row(Guid.Parse("11111111-1111-1111-1111-111111111111"), at, healingStrategy: SelfHealingStrategy.None),
            Row(Guid.Parse("22222222-2222-2222-2222-222222222222"), at.AddHours(1), healingStrategy: SelfHealingStrategy.None),
            Row(Guid.Parse("33333333-3333-3333-3333-333333333333"), at.AddHours(2), healingStrategy: SelfHealingStrategy.None),
        };
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var result = Detect(rows, verdicts: verdicts);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void HealingStrategyAi_Excluded()
    {
        // HealingStrategy.Ai must not be treated as trusted deterministic evidence.
        var at = Now.AddDays(-1);
        var rows = new List<MaintenanceDetector.EvidenceRow>
        {
            Row(Guid.Parse("11111111-1111-1111-1111-111111111111"), at, healingStrategy: SelfHealingStrategy.Ai),
            Row(Guid.Parse("22222222-2222-2222-2222-222222222222"), at.AddHours(1), healingStrategy: SelfHealingStrategy.Ai),
            Row(Guid.Parse("33333333-3333-3333-3333-333333333333"), at.AddHours(2), healingStrategy: SelfHealingStrategy.Ai),
        };
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var result = Detect(rows, verdicts: verdicts);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ValidDeterministicStrategy_Qualifies()
    {
        // TestAttribute, Role, Text, Structural should all qualify.
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var result = Detect(ThreeHeals(), verdicts: verdicts);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void MobileStrategy_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with
        {
            OriginalStrategy = "accessibilityid",
            RecoveredStrategy = "resourceid",
        }).ToList();
        var versions = Versions(target: "accessibilityid=com.example.login");
        Assert.Empty(Detect(rows, versions).Candidates);
    }

    [Fact]
    public void UnsupportedStrategy_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with { RecoveredStrategy = "uiselector" }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void ApplicationDefect_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with
        {
            Classification = FailureClassification.ApplicationDefect,
        }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void EnvironmentFailure_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with
        {
            Classification = FailureClassification.EnvironmentFailure,
        }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void CancelledExecution_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with { ExecutionStatus = ExecutionStatus.Cancelled }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void TimedOutExecution_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with { ExecutionStatus = ExecutionStatus.TimedOut }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void ErrorExecution_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with { ExecutionStatus = ExecutionStatus.Error }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void NonApprovedSourceVersion_Excluded()
    {
        var result = Detect(ThreeHeals(), Versions(review: ReviewStatus.Pending));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void RedactedLocator_Excluded()
    {
        var rows = ThreeHeals().Select(r => r with { RecoveredValue = "[REDACTED]" }).ToList();
        Assert.Empty(Detect(rows).Candidates);
    }

    [Fact]
    public void StepMismatch_Excluded()
    {
        var result = Detect(ThreeHeals(), Versions(stepOrder: 4));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void OpenAppBugDefect_Excluded()
    {
        var result = Detect(ThreeHeals(), excluded: new HashSet<Guid> { Test });
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void AppEnvFailureVerdict_Excluded()
    {
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(5, true),
        };
        Assert.Empty(Detect(ThreeHeals(), verdicts: verdicts).Candidates);
    }

    [Fact]
    public void ExactPairGrouping()
    {
        // Same everything except proposed value casing on strategy: strategies
        // normalize case-insensitively, values stay ordinal.
        var at = Now.AddDays(-1);
        var rows = new List<MaintenanceDetector.EvidenceRow>
        {
            Row(Guid.NewGuid(), at, originalStrategy: "CSS"),
            Row(Guid.NewGuid(), at.AddHours(1), originalStrategy: "css"),
            Row(Guid.NewGuid(), at.AddHours(2), originalStrategy: "Css"),
        };
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var candidate = Assert.Single(Detect(rows, verdicts: verdicts).Candidates);
        Assert.Equal("css", candidate.OriginalStrategy);
    }

    [Fact]
    public void ConfidenceBase_N3_Is40_BelowGate()
    {
        Assert.Equal(40, MaintenanceDetector.ComputeConfidence(3, 0, null, 0.5));
        var result = Detect(ThreeHeals());
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ConfidenceN6_MaxOccurrenceContribution()
    {
        Assert.Equal(70, MaintenanceDetector.ComputeConfidence(6, 0, null, 0.0));
        Assert.Equal(70, MaintenanceDetector.ComputeConfidence(9, 0, null, 0.0));
    }

    [Fact]
    public void ConfidenceBounds()
    {
        // Formula floor is the 40 base (the MinConfidence gate, not the
        // formula, rejects weak evidence); ceiling clamps at 100.
        Assert.Equal(40, MaintenanceDetector.ComputeConfidence(0, 0, null, 0.0));
        Assert.Equal(100, MaintenanceDetector.ComputeConfidence(100, 99, "High", 1.0));
    }

    [Fact]
    public void FailedVerdictCorroboration_Adds10()
    {
        Assert.Equal(50, MaintenanceDetector.ComputeConfidence(3, 2, null, 0.0));
        Assert.Equal(40, MaintenanceDetector.ComputeConfidence(3, 1, null, 0.0));
    }

    [Fact]
    public void HighForecast_Adds10()
    {
        Assert.Equal(50, MaintenanceDetector.ComputeConfidence(3, 0, "High", 0.0));
        Assert.Equal(40, MaintenanceDetector.ComputeConfidence(3, 0, "Medium", 0.0));
        Assert.Equal(40, MaintenanceDetector.ComputeConfidence(3, 0, null, 0.0));
    }

    [Fact]
    public void HealingRatio_Adds10()
    {
        Assert.Equal(50, MaintenanceDetector.ComputeConfidence(3, 0, null, 0.75));
        Assert.Equal(50, MaintenanceDetector.ComputeConfidence(3, 0, null, 1.0));
        Assert.Equal(40, MaintenanceDetector.ComputeConfidence(3, 0, null, 0.74));
    }

    [Fact]
    public void ConfidenceGate60_WithFullCorroboration()
    {
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(3, false),
        };
        var bands = new Dictionary<Guid, string?> { [Test] = "High" };
        var result = Detect(ThreeHeals(), verdicts: verdicts, bands: bands);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(70, candidate.Confidence);
        Assert.Contains("3 successful recoveries", candidate.ConfidenceFactors[0]);
    }

    [Fact]
    public void ConfidenceCannotBypassMinimumEvidence()
    {
        // Even perfect corroboration cannot promote 2 occurrences.
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(9, false),
        };
        var bands = new Dictionary<Guid, string?> { [Test] = "High" };
        var rows = ThreeHeals().Take(2).ToList();
        Assert.Empty(Detect(rows, verdicts: verdicts, bands: bands).Candidates);
    }

    [Fact]
    public void DeterministicRepeatedRuns_Agree()
    {
        var verdicts = new Dictionary<(Guid, Guid), MaintenanceDetector.FailedVerdictInfo>
        {
            [(Test, Version)] = new(2, false),
        };
        var rows = ThreeHeals();
        var first = Detect(rows, verdicts: verdicts);
        var second = Detect(rows, verdicts: verdicts);
        Assert.Equal(first.Candidates.Count, second.Candidates.Count);
        Assert.Equal(first.Candidates[0].Confidence, second.Candidates[0].Confidence);
        Assert.Equal(first.EvaluatedGroups, second.EvaluatedGroups);
    }

    [Fact]
    public void ParseTarget_MirrorsWorker()
    {
        Assert.Equal(("css", "#login"), MaintenanceDetector.ParseTarget("#login"));
        Assert.Equal(("css", "#login"), MaintenanceDetector.ParseTarget("css=#login"));
        Assert.Equal(("xpath", "//a"), MaintenanceDetector.ParseTarget("xpath=//a"));
        Assert.Equal(("role", "button"), MaintenanceDetector.ParseTarget("role=button|Login"));
        Assert.Equal(("text", "hi"), MaintenanceDetector.ParseTarget("text=hi"));
        Assert.Equal(("testid", "x"), MaintenanceDetector.ParseTarget("TESTID=x"));
        Assert.Null(MaintenanceDetector.ParseTarget(null));
        Assert.Null(MaintenanceDetector.ParseTarget("   "));
        Assert.Null(MaintenanceDetector.ParseTarget("role=|noname"));
    }

    [Fact]
    public void MapHealingStrategy_MatchesWorkerLabels()
    {
        Assert.Equal(SelfHealingStrategy.TestAttribute, MaintenanceDetector.MapHealingStrategy("testid"));
        Assert.Equal(SelfHealingStrategy.Role, MaintenanceDetector.MapHealingStrategy("role"));
        Assert.Equal(SelfHealingStrategy.Text, MaintenanceDetector.MapHealingStrategy("text"));
        Assert.Equal(SelfHealingStrategy.Structural, MaintenanceDetector.MapHealingStrategy("css"));
        Assert.Equal(SelfHealingStrategy.Structural, MaintenanceDetector.MapHealingStrategy("xpath"));
        Assert.Equal(SelfHealingStrategy.None, MaintenanceDetector.MapHealingStrategy("ai"));
        Assert.Equal(SelfHealingStrategy.None, MaintenanceDetector.MapHealingStrategy(null));
    }
}
