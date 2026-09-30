using System.Diagnostics;
using System.Text.Json;
using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.SelfHealing;

/// <summary>
/// System-driven self-healing orchestrator (Phase 2 Slice 11).
/// Deterministic validation is authoritative: AI suggests locator DATA only,
/// every candidate is validated (here for shape, worker-side against the live
/// DOM), and recovered locators never mutate the stored test version.
/// Assignment fencing mirrors Slice 9: only the live lease holder may request
/// AI candidates or persist authoritative healing outcomes.
/// </summary>
public sealed class SelfHealingService : ISelfHealingService
{
    private static readonly IReadOnlyList<string> DefaultStrategies =
        new[] { "css", "xpath", "role", "text", "testid" };

    private readonly ISelfHealingPolicyStore _policies;
    private readonly ISelfHealingAttemptStore _attempts;
    private readonly IExecutionStore _executions;
    private readonly IGridAssignmentStore _assignments;
    private readonly IAiProviderResolver _resolver;
    private readonly SelfHealingAiPromptBuilder _prompts;
    private readonly IOptions<SelfHealingOptions> _options;
    private readonly IAuditService _audit;
    private readonly IExecutionEventPublisher _events;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<SelfHealingService> _logger;

    public SelfHealingService(
        ISelfHealingPolicyStore policies,
        ISelfHealingAttemptStore attempts,
        IExecutionStore executions,
        IGridAssignmentStore assignments,
        IAiProviderResolver resolver,
        SelfHealingAiPromptBuilder prompts,
        IOptions<SelfHealingOptions> options,
        IAuditService audit,
        IExecutionEventPublisher events,
        IDateTimeProvider clock,
        ILogger<SelfHealingService> logger)
    {
        _policies = policies;
        _attempts = attempts;
        _executions = executions;
        _assignments = assignments;
        _resolver = resolver;
        _prompts = prompts;
        _options = options;
        _audit = audit;
        _events = events;
        _clock = clock;
        _logger = logger;
    }

    public async Task<TestExecution.WorkerHealingPolicyDto> ResolvePolicyAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await _executions.GetExecutionByIdAsync(executionId, ct);
        if (execution is null)
            return Disabled();
        var row = await _policies.GetByProjectAsync(execution.ProjectId, ct);
        if (row is null || !row.Enabled)
            return Disabled();
        return new TestExecution.WorkerHealingPolicyDto(
            true, row.AiFallbackEnabled, 1,
            row.MinDeterministicScore,
            row.MinAiConfidence,
            ParseStrategies(row.AllowedStrategies));
    }

    public async Task<IReadOnlyList<HealingCandidateDto>> SuggestCandidatesAsync(
        Guid executionId, HealingEvidenceDto evidence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var (execution, _) = await LoadSingleTestAsync(executionId, ct);
        var policy = await _policies.GetByProjectAsync(execution.ProjectId, ct);
        if (policy is null || !policy.Enabled || !policy.AiFallbackEnabled)
            throw new ConflictException("AI healing fallback is not enabled for this project.");

        var bounded = BoundEvidence(evidence, _options.Value.MaxEvidenceChars);
        var allowed = ParseStrategies(policy.AllowedStrategies);
        var request = new AiHealingRequest(
            new AiHealingEvidence(
                bounded.Action, bounded.OriginalTarget, bounded.DomFragment,
                bounded.Attributes, bounded.NearbyText),
            allowed.Count == 0 ? DefaultStrategies : allowed,
            policy.MinAiConfidence);

        var provider = _resolver.Resolve();
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Value.AiTimeout);
        AiHealingResult result;
        try
        {
            result = await provider.SuggestHealingCandidatesAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Bounded AI timeout: controlled healing failure, never an execution hang.
            _logger.LogWarning(ex, "AI healing suggestion timed out for execution {ExecutionId}.", executionId);
            await _audit.RecordAsync("self-healing.ai_timeout", "self-healing",
                executionId.ToString(), execution.ProjectId,
                SafeMeta(executionId, providerName: null, "ai-timeout"), ct);
            return Array.Empty<HealingCandidateDto>();
        }
        catch (AiProviderException ex)
        {
            // Provider failure isolation: healing degrades to deterministic-only.
            _logger.LogWarning(ex, "AI healing provider failed for execution {ExecutionId} ({Kind}).",
                executionId, ex.Kind);
            await _audit.RecordAsync("self-healing.ai_failed", "self-healing",
                executionId.ToString(), execution.ProjectId,
                SafeMeta(executionId, providerName: null, $"provider:{ex.Kind}"), ct);
            return Array.Empty<HealingCandidateDto>();
        }
        stopwatch.Stop();
        result = result with { LatencyMs = stopwatch.ElapsedMilliseconds };

        var (accepted, rejected) = SelfHealingAiValidator.Validate(
            result, allowed, policy.MinAiConfidence, _options.Value.MaxCandidates);
        // Log metadata only — never evidence bodies or provider secrets.
        _logger.LogInformation(
            "AI healing suggestion for execution {ExecutionId} accepted {Accepted} of {Total} candidates.",
            executionId, accepted.Count, result.Candidates.Count);
        await _audit.RecordAsync("self-healing.ai_suggested", "self-healing",
            executionId.ToString(), execution.ProjectId,
            SafeMeta(executionId, result.Provider, "ai-suggested",
                accepted.Count, result.Candidates.Count, rejected), ct);
        return accepted;
    }

    public async Task<int> RecordAttemptsAsync(
        Guid executionId, Guid? assignmentId,
        IReadOnlyList<WorkerHealingAttemptDto> attempts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var (execution, test) = await LoadSingleTestAsync(executionId, ct);

        // Fencing (§23): only the live lease holder persists authoritative results.
        Guid? authoritativeAssignment = null;
        try
        {
            var active = await _assignments.FindActiveByTestAsync(test.Id, ct);
            if (active is null)
            {
                // Lease-free executions (legacy standalone worker) may only
                // record when the caller also holds no lease.
                if (assignmentId.HasValue)
                    throw new ConflictException(
                        $"Stale healing report rejected for execution test {test.Id}: assignment ID mismatch.");
            }
            else
            {
                if (!assignmentId.HasValue || active.Id != assignmentId.Value ||
                    test.StartedAssignmentId != active.Id)
                {
                    _logger.LogWarning(
                        "Stale healing report rejected for execution test {TestId}: assignment mismatch.",
                        test.Id);
                    throw new ConflictException(
                        $"Stale healing report rejected for execution test {test.Id}: assignment ID mismatch.");
                }
                authoritativeAssignment = active.Id;
            }
        }
        catch (InvalidOperationException)
        {
            // No database/grid store configured: lease-free mode only.
            if (assignmentId.HasValue)
                throw new ConflictException(
                    $"Stale healing report rejected for execution test {test.Id}: assignment ID mismatch.");
        }

        var persisted = 0;
        foreach (var attempt in attempts.Take(500))
        {
            if (!TryMap(attempt, execution, test, authoritativeAssignment, out var entity))
            {
                _logger.LogWarning("Invalid healing attempt ignored for execution {ExecutionId} step {Step}.",
                    executionId, attempt.StepOrder);
                continue;
            }
            // Concurrency (§S): one authoritative row per step; the second
            // writer loses instead of duplicating state.
            var existing = await _attempts.FindByTestAndStepAsync(test.Id, entity.StepOrder, ct);
            if (existing is not null)
                continue;
            await _attempts.AddAsync(entity, ct);
            persisted++;
        }
        if (persisted > 0)
        {
            try
            {
                await _attempts.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Unique-index collision under PostgreSQL: a concurrent worker
                // won the row. Not corruption — the authoritative state stands.
                _logger.LogWarning(ex, "Concurrent healing persistence converged for execution {ExecutionId}.",
                    executionId);
            }
            await _audit.RecordAsync("self-healing.recorded", "self-healing",
                executionId.ToString(), execution.ProjectId,
                SafeMeta(executionId, providerName: null, "recorded", persisted, attempts.Count, null), ct);
            await PublishHealingEventsAsync(executionId, test.Id, ct);
        }
        return persisted;
    }

    // ---------- helpers ----------

    private async Task<(Execution Execution, ExecutionTest Test)> LoadSingleTestAsync(
        Guid executionId, CancellationToken ct)
    {
        var execution = await _executions.GetExecutionByIdAsync(executionId, ct)
            ?? throw new NotFoundException("Execution not found.");
        var test = (await _executions.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");
        return (execution, test);
    }

    private static TestExecution.WorkerHealingPolicyDto Disabled()
        => new(false, false, 1, null, null, DefaultStrategies);

    internal static IReadOnlyList<string> ParseStrategies(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return DefaultStrategies;
        var parsed = stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => SelfHealingAiValidator.AllowedStrategies.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parsed.Count == 0 ? DefaultStrategies : parsed;
    }

    internal static HealingEvidenceDto BoundEvidence(HealingEvidenceDto evidence, int maxChars)
    {
        static string Cap(string? value, int max)
            => string.IsNullOrEmpty(value) ? string.Empty
                : value.Length <= max ? value : value[..max];
        var cap = Math.Clamp(maxChars, 500, 8000);
        return new HealingEvidenceDto(
            Cap(evidence.Action, 50),
            SensitiveDataRedactor.Redact(Cap(evidence.OriginalTarget, 500)),
            SensitiveDataRedactor.Redact(Cap(evidence.DomFragment, cap)),
            evidence.Attributes.Take(20).Select(a => SensitiveDataRedactor.Redact(Cap(a, 300))).ToList(),
            evidence.NearbyText.Take(20).Select(t => SensitiveDataRedactor.Redact(Cap(t, 200))).ToList());
    }

    private bool TryMap(
        TestExecution.WorkerHealingAttemptDto attempt, Execution execution, ExecutionTest test,
        Guid? assignmentId, out SelfHealingAttempt entity)
    {
        entity = null!;
        if (attempt.StepOrder < 1 || attempt.StepOrder > 500)
            return false;
        if (!SelfHealingEligibility.IsHealableAction(attempt.StepAction))
            return false;
        if (!Enum.TryParse<SelfHealingStatus>(attempt.Status, ignoreCase: true, out var status))
            return false;
        if (status is not (SelfHealingStatus.Applied or SelfHealingStatus.Failed))
            return false;
        if (!Enum.TryParse<SelfHealingStrategy>(attempt.HealingStrategy, ignoreCase: true, out var strategy))
            strategy = SelfHealingStrategy.None;
        if (attempt.CandidateCount < 0 || attempt.CandidateCount > 8)
            return false;
        // The backend re-checks eligibility shape: a healed step must name a
        // healable action; assertion outcomes can never arrive as Applied.
        if (status == SelfHealingStatus.Applied && !attempt.WasApplied)
            return false;
        entity = new SelfHealingAttempt
        {
            ProjectId = execution.ProjectId,
            ExecutionId = execution.Id,
            ExecutionTestId = test.Id,
            TestCaseId = test.TestCaseId,
            TestCaseVersionId = test.TestCaseVersionId,
            StepOrder = attempt.StepOrder,
            StepAction = attempt.StepAction.Trim()[..Math.Min(200, attempt.StepAction.Trim().Length)],
            OriginalStrategy = Cap(attempt.OriginalStrategy, 50),
            OriginalValue = Cap(SensitiveDataRedactor.Redact(attempt.OriginalValue), 2000),
            RecoveredStrategy = Cap(attempt.RecoveredStrategy, 50),
            RecoveredValue = Cap(SensitiveDataRedactor.Redact(attempt.RecoveredValue), 2000),
            HealingStrategy = strategy,
            Status = status,
            CandidateCount = attempt.CandidateCount,
            WasApplied = attempt.WasApplied,
            IsAiAssisted = attempt.IsAiAssisted,
            ErrorMessage = attempt.ErrorMessage is null ? null
                : SensitiveDataRedactor.Redact(attempt.ErrorMessage)[..Math.Min(2000, SensitiveDataRedactor.Redact(attempt.ErrorMessage).Length)],
            AssignmentId = assignmentId,
            CreatedAt = _clock.UtcNow,
        };
        return true;

        static string? Cap(string? value, int max)
            => string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
    }

    private async Task PublishHealingEventsAsync(Guid executionId, Guid testId, CancellationToken ct)
    {
        try
        {
            var rows = await _attempts.ListByTestAsync(testId, ct);
            foreach (var row in rows.Where(r => r.WasApplied))
            {
                await _events.PublishAsync(executionId, ExecutionEvents.SelfHealingApplied,
                    new
                    {
                        executionId,
                        executionTestId = testId,
                        stepOrder = row.StepOrder,
                        healingStrategy = row.HealingStrategy.ToString(),
                        aiAssisted = row.IsAiAssisted,
                    }, ct);
            }
        }
        catch (Exception ex)
        {
            // Healing events are supplementary; persistence already won.
            _logger.LogWarning(ex, "Self-healing events for execution {ExecutionId} were not published.",
                executionId);
        }
    }

    private static string SafeMeta(
        Guid executionId, string? providerName, string outcome,
        int? accepted = null, int? total = null, IReadOnlyList<string>? rejected = null)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            executionId,
            provider = providerName,
            promptVersion = AiPromptVersions.SelfHealingV1,
            outcome,
            accepted,
            total,
            rejectedCount = rejected?.Count,
        }));
}
