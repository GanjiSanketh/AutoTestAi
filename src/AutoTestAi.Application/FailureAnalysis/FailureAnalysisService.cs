using System.Diagnostics;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Options;
using DomainFailureAnalysis = AutoTestAi.Domain.Entities.FailureAnalysis;

namespace AutoTestAi.Application.FailureAnalysis;

/// <summary>
/// Failure-analysis orchestrator (Slice 6). Evidence is bounded and redacted
/// before any provider sees it; provider output is validated before anything
/// persists. Analysis never changes execution status or classification, never
/// modifies tests, and never creates defects.
/// </summary>
public sealed class FailureAnalysisService : IFailureAnalysisService
{
    private static readonly IReadOnlySet<ExecutionTestStatus> Analyzable =
        new HashSet<ExecutionTestStatus>
        {
            ExecutionTestStatus.Failed,
            ExecutionTestStatus.Error,
            ExecutionTestStatus.TimedOut,
        };

    private readonly IExecutionStore _store;
    private readonly IFailureEvidenceService _evidence;
    private readonly IAiProviderResolver _resolver;
    private readonly AiAnalysisValidator _validator;
    private readonly AiGenerationRateLimiter _rateLimiter;
    private readonly IOptions<FailureAnalysisOptions> _options;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditService _audit;
    private readonly IExecutionEventPublisher _events;
    private readonly IDateTimeProvider _clock;

    public FailureAnalysisService(
        IExecutionStore store,
        IFailureEvidenceService evidence,
        IAiProviderResolver resolver,
        AiAnalysisValidator validator,
        AiGenerationRateLimiter rateLimiter,
        IOptions<FailureAnalysisOptions> options,
        IAuthorizationService authorization,
        IAuditService audit,
        IExecutionEventPublisher events,
        IDateTimeProvider clock)
    {
        _store = store;
        _evidence = evidence;
        _resolver = resolver;
        _validator = validator;
        _rateLimiter = rateLimiter;
        _options = options;
        _authorization = authorization;
        _audit = audit;
        _events = events;
        _clock = clock;
    }

    public async Task<FailureAnalysisDto> AnalyzeAsync(
        Guid executionId, CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty)
            throw new ValidationException("Execution id is required.",
                new[] { new FieldError("executionId", "Execution id is required.") });

        var (execution, test) = await RequireAnalyzableAsync(
            executionId, Permissions.ExecutionsAnalyze, cancellationToken);

        var attempts = await _store.ListAnalysesAsync(test.Id, cancellationToken);
        if (attempts.Any(a => a.Status == AnalysisStatus.Running))
            throw new ConflictException(
                "A failure analysis is already running for this execution. Wait for it to finish or cancel the request.");
        var attemptNumber = attempts.Count == 0 ? 1 : attempts.Max(a => a.Attempt) + 1;

        var evidence = await _evidence.BuildAsync(execution, test, cancellationToken);

        var attempt = new DomainFailureAnalysis
        {
            ExecutionTestId = test.Id,
            Attempt = attemptNumber,
            Status = AnalysisStatus.Running,
            Classification = FailureClassification.Unknown,
            CreatedAt = _clock.UtcNow,
        };
        try
        {
            await _store.AddAnalysisAsync(attempt, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch (ConflictException)
        {
            // Real concurrency guard (unique filtered index in the store):
            // a second writer won the Running slot.
            throw new ConflictException(
                "A failure analysis is already running for this execution.");
        }

        await _audit.RecordAsync("failure-analysis.requested", "failure_analysis",
            attempt.Id.ToString(), execution.ProjectId,
            SafeMeta(execution, test, attemptNumber, "requested"), cancellationToken);

        var provider = _resolver.Resolve();
        _rateLimiter.CheckOperationOrThrow(
            execution.ProjectId, "analysis", provider.Name,
            _options.Value.MaxAnalysesPerMinutePerProject,
            "AI analysis rate limit exceeded for this project");

        var context = new AiFailureAnalysisContext(
            execution.Id, test.Id,
            evidence.TestKey, evidence.TestTitle,
            evidence.Framework, evidence.Platform, evidence.Browser,
            evidence.ExecutionClassification,
            evidence.FailedStepSummary,
            evidence.FailedSteps, evidence.Logs,
            evidence.ArtifactNames.Count, evidence.ArtifactNames,
            attemptNumber, evidence.Truncated);
        var analysisRequest = new AiFailureAnalysisRequest(
            test.Id, test.ErrorType, test.ErrorMessage, evidence.TestTitle, context);

        AiAnalysisResult result;
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Value.Timeout);
        try
        {
            result = await provider.AnalyzeFailureAsync(analysisRequest, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkTerminalAsync(attempt, AnalysisStatus.Cancelled,
                "Analysis was cancelled.", execution, test, cancellationToken);
            await _audit.RecordAsync("failure-analysis.cancelled", "failure_analysis",
                attempt.Id.ToString(), execution.ProjectId,
                SafeMeta(execution, test, attemptNumber, "cancelled"), cancellationToken);
            throw;
        }
        catch (AiProviderException ex)
        {
            await MarkTerminalAsync(attempt, AnalysisStatus.Failed,
                ex.Message, execution, test, cancellationToken);
            await _audit.RecordAsync("failure-analysis.failed", "failure_analysis",
                attempt.Id.ToString(), execution.ProjectId,
                SafeMeta(execution, test, attemptNumber, $"provider:{ex.Kind}"), cancellationToken);
            throw;
        }
        stopwatch.Stop();
        result = result with { LatencyMs = stopwatch.ElapsedMilliseconds };

        try
        {
            _validator.ValidateOrThrow(result);
        }
        catch (ValidationException)
        {
            await MarkTerminalAsync(attempt, AnalysisStatus.Failed,
                "The provider returned malformed analysis output.", execution, test, cancellationToken);
            await _audit.RecordAsync("failure-analysis.failed", "failure_analysis",
                attempt.Id.ToString(), execution.ProjectId,
                SafeMeta(execution, test, attemptNumber, "validation-failed"), cancellationToken);
            throw;
        }

        attempt.Status = AnalysisStatus.Completed;
        attempt.Classification = ParseClassification(result.Classification);
        attempt.Summary = result.Summary?.Trim();
        attempt.RootCause = result.RootCause.Trim();
        attempt.Confidence = result.Confidence;
        attempt.Evidence = BuildEvidenceSnapshot(evidence, result);
        attempt.Assumptions = result.Assumptions?.ToList() ?? new List<string>();
        attempt.Warnings = result.Warnings?.ToList() ?? new List<string>();
        attempt.RecommendedAction = result.RecommendedAction?.Trim();
        attempt.IsLikelyDefect = result.IsLikelyDefect;
        attempt.Provider = result.Provider;
        attempt.Model = result.Model;
        attempt.PromptVersion = string.IsNullOrWhiteSpace(result.PromptVersion)
            ? AiPromptVersions.FailureAnalysisV1
            : result.PromptVersion.Trim();
        attempt.LatencyMs = result.LatencyMs;
        attempt.InputTokens = result.InputTokens;
        attempt.OutputTokens = result.OutputTokens;
        attempt.TotalTokens = result.TotalTokens;
        await _store.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("failure-analysis.completed", "failure_analysis",
            attempt.Id.ToString(), execution.ProjectId,
            SafeMeta(execution, test, attemptNumber, "completed", result), cancellationToken);
        await _events.PublishAsync(execution.Id, ExecutionEvents.FailureAnalysisCompleted,
            new { executionId = execution.Id, executionTestId = test.Id, analysisId = attempt.Id }, cancellationToken);

        return Map(attempt, execution.Id);
    }

    public async Task<FailureAnalysisDto> GetLatestAsync(
        Guid executionId, CancellationToken cancellationToken)
    {
        var (execution, test) = await RequireAnalyzableAsync(
            executionId, Permissions.ExecutionsRead, cancellationToken, requireFailed: false);
        var latest = (await _store.ListAnalysesAsync(test.Id, cancellationToken))
            .OrderByDescending(a => a.Attempt).FirstOrDefault()
            ?? throw new NotFoundException("No failure analysis exists for this execution.");
        return Map(latest, execution.Id);
    }

    public async Task<IReadOnlyList<FailureAnalysisDto>> ListAttemptsAsync(
        Guid executionId, CancellationToken cancellationToken)
    {
        var (execution, test) = await RequireAnalyzableAsync(
            executionId, Permissions.ExecutionsRead, cancellationToken, requireFailed: false);
        return (await _store.ListAnalysesAsync(test.Id, cancellationToken))
            .OrderByDescending(a => a.Attempt)
            .Select(a => Map(a, execution.Id)).ToList();
    }

    // ---------- helpers ----------

    private async Task<(Execution Execution, ExecutionTest Test)> RequireAnalyzableAsync(
        Guid executionId, string permission, CancellationToken ct, bool requireFailed = true)
    {
        var execution = await _store.GetExecutionByIdAsync(executionId, ct);
        await _authorization.RequireProjectAccessAsync(
            execution?.ProjectId ?? executionId, permission, ct);
        if (execution is null)
            throw new NotFoundException("Execution not found.");
        var test = (await _store.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");
        if (requireFailed && !Analyzable.Contains(test.Status))
            throw new ConflictException(
                $"Failure analysis requires a failed execution (current status '{test.Status}').");
        return (execution, test);
    }

    private async Task MarkTerminalAsync(
        DomainFailureAnalysis attempt, AnalysisStatus status, string? error,
        Execution execution, ExecutionTest test, CancellationToken ct)
    {
        // Reload to avoid clobbering a concurrently finished attempt.
        var fresh = await _store.GetAnalysisByIdAsync(attempt.Id, ct);
        if (fresh is null || fresh.Status != AnalysisStatus.Running)
            return;
        fresh.Status = status;
        fresh.ErrorMessage = error is null ? null
            : error.Length <= 2000 ? error : error[..2000];
        await _store.SaveChangesAsync(ct);
        _ = execution; _ = test;
    }

    private static FailureClassification ParseClassification(string? value)
        => Enum.TryParse<FailureClassification>(value?.Trim(), ignoreCase: true, out var parsed)
            ? parsed : FailureClassification.Unknown;

    private static JsonDocument BuildEvidenceSnapshot(FailureEvidence evidence, AiAnalysisResult result)
    {
        // Bounded redacted evidence + provider-quoted facts only. Never raw prompts.
        var payload = JsonSerializer.Serialize(new
        {
            testKey = evidence.TestKey,
            executionClassification = evidence.ExecutionClassification,
            failedStepSummary = evidence.FailedStepSummary,
            errorType = evidence.ErrorType,
            logsConsidered = evidence.Logs.Count,
            artifactsConsidered = evidence.ArtifactNames,
            attempt = evidence.Attempt,
            truncated = evidence.Truncated,
            truncationNotes = evidence.TruncationNotes,
            quotedEvidence = result.Evidence ?? Array.Empty<string>(),
            promptVersion = result.PromptVersion ?? AiPromptVersions.FailureAnalysisV1,
        });
        return JsonDocument.Parse(SensitiveDataRedactor.Redact(payload));
    }

    private static string SafeMeta(
        Execution execution, ExecutionTest test, int attempt, string outcome,
        AiAnalysisResult? result = null)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            executionId = execution.Id,
            executionTestId = test.Id,
            attempt,
            provider = result?.Provider,
            model = result?.Model,
            promptVersion = result?.PromptVersion ?? AiPromptVersions.FailureAnalysisV1,
            latencyMs = result?.LatencyMs,
            classification = result?.Classification,
            confidence = result?.Confidence,
            outcome,
        }));

    private static FailureAnalysisDto Map(DomainFailureAnalysis attempt, Guid executionId) => new(
        attempt.Id, executionId, attempt.ExecutionTestId, attempt.Attempt,
        attempt.Status.ToString(), attempt.Classification.ToString(),
        attempt.Summary, attempt.RootCause, attempt.Confidence,
        ExtractStrings(attempt.Evidence, "quotedEvidence"),
        attempt.Assumptions, attempt.Warnings,
        attempt.RecommendedAction, attempt.IsLikelyDefect,
        attempt.Provider, attempt.Model, attempt.PromptVersion,
        attempt.LatencyMs, attempt.InputTokens, attempt.OutputTokens, attempt.TotalTokens,
        attempt.CreatedAt);

    private static IReadOnlyList<string> ExtractStrings(JsonDocument? evidence, string property)
    {
        if (evidence is null) return Array.Empty<string>();
        try
        {
            if (evidence.RootElement.TryGetProperty(property, out var array) &&
                array.ValueKind == JsonValueKind.Array)
                return array.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
        }
        catch (JsonException)
        {
            // Corrupt snapshot: surface nothing rather than failing the read.
        }
        return Array.Empty<string>();
    }
}
