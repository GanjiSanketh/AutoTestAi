using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.FailureAnalysis;

/// <summary>
/// Bounded redacted evidence package for one failed execution test (Slice 6 §8).
/// Deterministic: same failure yields the same package; truncation is marked.
/// </summary>
public sealed record FailureEvidence(
    Guid ExecutionId,
    Guid ExecutionTestId,
    string TestKey,
    string TestTitle,
    string Framework,
    string Platform,
    string Browser,
    string ExecutionClassification,
    string? FailedStepSummary,
    string? ErrorType,
    string? ErrorMessage,
    IReadOnlyList<AiFailureEvidenceStep> FailedSteps,
    IReadOnlyList<AiFailureEvidenceLog> Logs,
    IReadOnlyList<string> ArtifactNames,
    int Attempt,
    bool Truncated,
    IReadOnlyList<string> TruncationNotes);

public interface IFailureEvidenceService
{
    Task<FailureEvidence> BuildAsync(
        Execution execution, ExecutionTest test, CancellationToken cancellationToken);
}

/// <summary>
/// Aggregates failure evidence with hard bounds and redaction (Slice 6 §8-10).
/// Prefers failure-relevant rows: failed steps first, most recent logs last.
/// </summary>
public sealed class FailureEvidenceService : IFailureEvidenceService
{
    private const string TruncationMarker = "[truncated: original content exceeded configured limit]";

    private readonly IExecutionStore _executions;
    private readonly ITestCaseStore _cases;
    private readonly IOptions<FailureAnalysisOptions> _options;

    public FailureEvidenceService(
        IExecutionStore executions,
        ITestCaseStore cases,
        IOptions<FailureAnalysisOptions> options)
    {
        _executions = executions;
        _cases = cases;
        _options = options;
    }

    public async Task<FailureEvidence> BuildAsync(
        Execution execution, ExecutionTest test, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(test);
        var limits = _options.Value;
        var notes = new List<string>();
        var truncated = false;
        void Mark(string note) { truncated = true; notes.Add(note); }

        var testCase = await _cases.GetByIdAsync(test.TestCaseId, cancellationToken);
        TestCaseVersion? version = test.TestCaseVersionId is not null
            ? await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, cancellationToken)
            : null;

        var allSteps = (await _executions.ListStepResultsAsync(test.Id, cancellationToken))
            .OrderBy(s => s.StepOrder).ToList();
        var failedSteps = allSteps
            .Where(s => s.Status is ExecutionTestStatus.Failed or ExecutionTestStatus.Error)
            .Take(limits.MaxFailedSteps)
            .Select(s => new AiFailureEvidenceStep(
                s.StepOrder, s.Action, s.Target, s.Status.ToString(),
                Bound(s.ErrorMessage, limits.MaxErrorChars, "step error", notes, ref truncated)))
            .ToList();
        if (allSteps.Count(s => s.Status is ExecutionTestStatus.Failed or ExecutionTestStatus.Error) > failedSteps.Count)
            Mark($"{allSteps.Count} failed steps exceeded the evidence bound; only the first {failedSteps.Count} are included.");

        var allLogs = (await _executions.ListLogsAsync(test.Id, null, int.MaxValue, cancellationToken))
            .OrderBy(l => l.Id).ToList();
        var tail = allLogs.TakeLast(Math.Max(0, limits.MaxLogLines)).ToList();
        if (tail.Count < allLogs.Count)
            Mark($"Log history exceeded the evidence bound; only the last {tail.Count} of {allLogs.Count} lines are included.");
        var logChars = 0;
        var evidenceLogs = new List<AiFailureEvidenceLog>();
        foreach (var log in tail)
        {
            var message = SensitiveDataRedactor.Redact(log.Message ?? string.Empty);
            if (message.Length > limits.MaxLogCharsPerMessage)
            {
                message = message[..limits.MaxLogCharsPerMessage] + " " + TruncationMarker;
                Mark("One or more log messages were truncated to the per-message bound.");
            }
            if (logChars + message.Length > limits.MaxTotalLogChars)
            {
                Mark("Log evidence exceeded the total character bound; remaining lines omitted.");
                break;
            }
            logChars += message.Length;
            evidenceLogs.Add(new AiFailureEvidenceLog(log.Level, message));
        }

        var artifacts = (await _executions.ListArtifactsAsync(test.Id, cancellationToken))
            .OrderBy(a => a.CreatedAt).ToList();
        var artifactNames = artifacts
            .Take(Math.Max(0, limits.MaxArtifactRefs))
            .Select(a => a.FileName ?? a.ArtifactType)
            .ToList();
        if (artifactNames.Count < artifacts.Count)
            Mark($"Artifact references exceeded the evidence bound; only {artifactNames.Count} of {artifacts.Count} are included.");

        var errorMessage = SensitiveDataRedactor.Redact(test.ErrorMessage ?? string.Empty);
        if (errorMessage.Length > limits.MaxErrorChars)
        {
            errorMessage = errorMessage[..limits.MaxErrorChars] + " " + TruncationMarker;
            Mark("The error message was truncated to the configured bound.");
        }

        var failedStepSummary = failedSteps.Count == 0 ? null : string.Join("; ",
            failedSteps.Select(s => $"step {s.Order} ({s.Action}) {s.Status}: {s.ErrorMessage}"));

        return new FailureEvidence(
            execution.Id, test.Id,
            testCase?.TestKey ?? "(unknown)", testCase?.Title ?? "(unknown)",
            test.Framework ?? testCase?.Framework ?? "playwright",
            testCase?.Platform ?? "web",
            test.Browser ?? "chromium",
            test.FailureClassification.ToString(),
            failedStepSummary,
            test.ErrorType,
            string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage,
            failedSteps, evidenceLogs, artifactNames,
            test.Attempt, truncated, notes);
    }

    private static string? Bound(
        string? value, int max, string what,
        List<string> notes, ref bool truncated)
    {
        var redacted = SensitiveDataRedactor.Redact(value ?? string.Empty);
        if (redacted.Length <= max) return string.IsNullOrWhiteSpace(redacted) ? null : redacted;
        truncated = true;
        notes.Add($"The {what} was truncated to the configured bound.");
        return redacted[..max] + " " + TruncationMarker;
    }
}
