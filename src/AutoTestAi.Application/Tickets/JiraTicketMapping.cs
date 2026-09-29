using System.Text;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using DomainFailureAnalysis = AutoTestAi.Domain.Entities.FailureAnalysis;

namespace AutoTestAi.Application.Tickets;

/// <summary>
/// Deterministic severity → Jira priority mapping (Slice 7 §11).
/// Never AI-driven; falls back to "Medium" when unmapped.
/// </summary>
public static class JiraSeverityMapper
{
    public static string Map(Severity severity, IReadOnlyDictionary<string, string>? mapping)
    {
        var key = severity.ToString();
        if (mapping is not null && mapping.TryGetValue(key, out var priority) && !string.IsNullOrWhiteSpace(priority))
            return priority.Trim();
        return severity switch
        {
            Severity.Critical => "Highest",
            Severity.High => "High",
            Severity.Medium => "Medium",
            Severity.Low => "Low",
            _ => "Medium",
        };
    }
}

/// <summary>
/// Centralized deterministic defect → Jira content builder (Slice 7 §10).
/// Only includes data that exists; never fabricates URLs, traces, or findings.
/// </summary>
public static class JiraTicketContentBuilder
{
    private const int MaxSummaryLength = 255;
    private const int MaxDescriptionLength = 8000;

    public static string BuildSummary(Defect defect)
    {
        var title = (defect.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = "AutoTest AI defect";
        var summary = $"[AutoTestAI] {title}";
        if (summary.Length > MaxSummaryLength)
            summary = summary[..(MaxSummaryLength - 1)] + "…";
        return summary;
    }

    public static string BuildDescription(
        Defect defect,
        JiraIntegrationConfig config,
        DomainFailureAnalysis? analysis,
        TestInfo? test,
        string? defectReference)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AutoTest AI Defect");
        sb.AppendLine();
        sb.AppendLine("Defect");
        sb.AppendLine($"- ID: {defect.Id}");
        sb.AppendLine($"- Title: {OneLine(defect.Title)}");
        sb.AppendLine($"- Severity: {defect.Severity}");
        sb.AppendLine($"- Status: {defect.Status}");
        if (defect.RootCauseType is not null)
            sb.AppendLine($"- Failure Classification: {defect.RootCauseType}");

        if (test is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Test");
            if (!string.IsNullOrWhiteSpace(test.TestKey))
                sb.AppendLine($"- Test Case: {test.TestKey}");
            if (!string.IsNullOrWhiteSpace(test.TestTitle))
                sb.AppendLine($"- Test Title: {OneLine(test.TestTitle)}");
            if (test.VersionNumber is not null)
                sb.AppendLine($"- Test Version: v{test.VersionNumber}");
            if (test.ExecutionId is not null)
                sb.AppendLine($"- Execution: {test.ExecutionId}");
        }

        if (analysis is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Failure Analysis");
            sb.AppendLine($"- Attempt: {analysis.Attempt}");
            sb.AppendLine($"- Classification: {analysis.Classification}");
            if (!string.IsNullOrWhiteSpace(analysis.Summary))
                sb.AppendLine($"- Summary: {OneLine(analysis.Summary, 500)}");
            sb.AppendLine($"- Likely Defect: {(analysis.IsLikelyDefect ? "yes" : "no")}");
            if (!string.IsNullOrWhiteSpace(analysis.RecommendedAction))
                sb.AppendLine($"- Recommended Action: {OneLine(analysis.RecommendedAction, 500)}");
        }

        if (!string.IsNullOrWhiteSpace(defect.Description))
        {
            sb.AppendLine();
            sb.AppendLine("Description");
            sb.AppendLine(Truncate(defect.Description.Trim(), 2000));
        }

        sb.AppendLine();
        sb.AppendLine("Traceability");
        sb.AppendLine($"- AutoTest AI defect: {defect.Id}");
        if (test?.ExecutionId is not null)
            sb.AppendLine($"- AutoTest AI execution: {test.ExecutionId}");
        if (!string.IsNullOrWhiteSpace(defectReference))
            sb.AppendLine($"- Reference: {defectReference}");

        var description = sb.ToString().TrimEnd();
        return Truncate(description, MaxDescriptionLength);
    }

    public sealed record TestInfo(
        string? TestKey,
        string? TestTitle,
        int? VersionNumber,
        Guid? ExecutionId);

    private static string OneLine(string? value, int max = 200)
    {
        var flat = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return Truncate(flat, max);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
