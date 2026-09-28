using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AutoTestAi.Infrastructure.Executions;

/// <summary>EF Core implementation of the execution seam. No authorization here.</summary>
public sealed class EfExecutionStore : IExecutionStore
{
    private readonly AutoTestAiDbContext _db;

    public EfExecutionStore(AutoTestAiDbContext db) => _db = db;

    public Task<int> CountAsync(Guid projectId, string? status, Guid? testCaseId, CancellationToken ct)
        => ApplyFilters(projectId, status, testCaseId).CountAsync(ct);

    public async Task<IReadOnlyList<ExecutionListRow>> ListAsync(
        Guid projectId, string? status, Guid? testCaseId, int skip, int take, CancellationToken ct)
    {
        var executions = await ApplyFilters(projectId, status, testCaseId)
            .OrderByDescending(e => e.CreatedAt)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);
        if (executions.Count == 0) return Array.Empty<ExecutionListRow>();

        var ids = executions.Select(e => e.Id).ToList();
        var tests = await _db.ExecutionTests
            .Where(t => ids.Contains(t.ExecutionId))
            .AsNoTracking()
            .ToListAsync(ct);
        var firstByExecution = tests
            .GroupBy(t => t.ExecutionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.CreatedAt).First());

        var caseIds = firstByExecution.Values.Select(t => t.TestCaseId).Distinct().ToList();
        var cases = await _db.TestCases
            .Where(c => caseIds.Contains(c.Id))
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Id, ct);

        var versionIds = firstByExecution.Values
            .Select(t => t.TestCaseVersionId)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToList();
        var versions = await _db.TestCaseVersions
            .Where(v => versionIds.Contains(v.Id))
            .AsNoTracking()
            .ToDictionaryAsync(v => v.Id, ct);

        var rows = new List<ExecutionListRow>();
        foreach (var execution in executions)
        {
            if (!firstByExecution.TryGetValue(execution.Id, out var test)) continue;
            cases.TryGetValue(test.TestCaseId, out var testCase);
            var versionNumber = test.TestCaseVersionId is not null &&
                versions.TryGetValue(test.TestCaseVersionId.Value, out var version)
                ? version.VersionNumber : 0;
            rows.Add(new ExecutionListRow(
                execution, test,
                testCase?.TestKey ?? "(unknown)",
                testCase?.Title ?? "(unknown)",
                versionNumber));
        }
        return rows;
    }

    public Task<Execution?> GetExecutionByIdAsync(Guid executionId, CancellationToken ct)
        => _db.Executions.FirstOrDefaultAsync(e => e.Id == executionId, ct);

    public Task<ExecutionTest?> GetExecutionTestByIdAsync(Guid executionTestId, CancellationToken ct)
        => _db.ExecutionTests.FirstOrDefaultAsync(t => t.Id == executionTestId, ct);

    public async Task<IReadOnlyList<ExecutionTest>> ListTestsByExecutionAsync(Guid executionId, CancellationToken ct)
        => await _db.ExecutionTests
            .Where(t => t.ExecutionId == executionId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public Task<Execution?> FindByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken ct)
        => _db.Executions.FirstOrDefaultAsync(
            e => e.ProjectId == projectId && e.IdempotencyKey == idempotencyKey, ct);

    public Task AddExecutionAsync(Execution execution, CancellationToken ct)
        => _db.Executions.AddAsync(execution, ct).AsTask();

    public Task AddExecutionTestAsync(ExecutionTest test, CancellationToken ct)
        => _db.ExecutionTests.AddAsync(test, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<ExecutionStepResult>> ListStepResultsAsync(Guid executionTestId, CancellationToken ct)
        => await _db.ExecutionStepResults
            .Where(s => s.ExecutionTestId == executionTestId)
            .OrderBy(s => s.StepOrder)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task AddStepResultsAsync(IEnumerable<ExecutionStepResult> rows, CancellationToken ct)
    {
        await _db.ExecutionStepResults.AddRangeAsync(rows, ct);
    }

    public async Task DeleteStepResultsAsync(Guid executionTestId, CancellationToken ct)
    {
        var rows = await _db.ExecutionStepResults
            .Where(s => s.ExecutionTestId == executionTestId)
            .ToListAsync(ct);
        _db.ExecutionStepResults.RemoveRange(rows);
    }

    public async Task<IReadOnlyList<ExecutionLog>> ListLogsAsync(
        Guid executionTestId, long? afterId, int take, CancellationToken ct)
        => await _db.ExecutionLogs
            .Where(l => l.ExecutionTestId == executionTestId && (!afterId.HasValue || l.Id > afterId.Value))
            .OrderBy(l => l.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task AppendLogsAsync(IEnumerable<ExecutionLog> rows, CancellationToken ct)
    {
        await _db.ExecutionLogs.AddRangeAsync(rows, ct);
    }

    public async Task DeleteLogsAsync(Guid executionTestId, CancellationToken ct)
    {
        var rows = await _db.ExecutionLogs
            .Where(l => l.ExecutionTestId == executionTestId)
            .ToListAsync(ct);
        _db.ExecutionLogs.RemoveRange(rows);
    }

    public async Task<IReadOnlyList<ExecutionArtifact>> ListArtifactsAsync(Guid executionTestId, CancellationToken ct)
        => await _db.ExecutionArtifacts
            .Where(a => a.ExecutionTestId == executionTestId)
            .OrderBy(a => a.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<ExecutionArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken ct)
        => _db.ExecutionArtifacts.FirstOrDefaultAsync(a => a.Id == artifactId, ct);

    public Task AddArtifactAsync(ExecutionArtifact artifact, CancellationToken ct)
        => _db.ExecutionArtifacts.AddAsync(artifact, ct).AsTask();

    public async Task DeleteArtifactsAsync(Guid executionTestId, CancellationToken ct)
    {
        var rows = await _db.ExecutionArtifacts
            .Where(a => a.ExecutionTestId == executionTestId)
            .ToListAsync(ct);
        _db.ExecutionArtifacts.RemoveRange(rows);
    }

    public async Task<IReadOnlyList<FailureAnalysis>> ListAnalysesAsync(Guid executionTestId, CancellationToken ct)
        => await _db.FailureAnalyses
            .Where(a => a.ExecutionTestId == executionTestId)
            .OrderBy(a => a.Attempt)
            .AsNoTracking()
            .ToListAsync(ct);

    public Task<FailureAnalysis?> GetAnalysisByIdAsync(Guid analysisId, CancellationToken ct)
        => _db.FailureAnalyses.FirstOrDefaultAsync(a => a.Id == analysisId, ct);

    public async Task AddAnalysisAsync(FailureAnalysis analysis, CancellationToken ct)
    {
        await _db.FailureAnalyses.AddAsync(analysis, ct);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException(
                "A failure analysis is already running for this execution test.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pg && pg.SqlState == "23505";

    private IQueryable<Execution> ApplyFilters(Guid projectId, string? status, Guid? testCaseId)
    {
        IQueryable<Execution> query = _db.Executions.Where(e => e.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(e => e.Status.ToString() == status);
        if (testCaseId.HasValue)
        {
            var id = testCaseId.Value;
            query = query.Where(e => _db.ExecutionTests
                .Any(t => t.ExecutionId == e.Id && t.TestCaseId == id));
        }
        return query;
    }
}
