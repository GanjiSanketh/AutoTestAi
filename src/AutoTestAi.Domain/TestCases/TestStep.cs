using System.Text.Json;

namespace AutoTestAi.Domain.TestCases;

/// <summary>
/// A single structured test step. Persisted as JSONB (docs/05); the model
/// stays open (action/target/value strings) so future AI generation can
/// extend it without schema changes.
/// </summary>
public sealed record TestStep(int Order, string Action, string? Target, string? Value)
{
    public const int MaxSteps = 500;
    public const int MaxActionLength = 200;
    public const int MaxTargetLength = 2000;
    public const int MaxValueLength = 8000;

    /// <summary>Validates raw step payloads, returning one message per problem.</summary>
    public static IReadOnlyList<string> Validate(JsonElement? steps)
    {
        var problems = new List<string>();
        if (steps is not { ValueKind: JsonValueKind.Array } array)
        {
            problems.Add("structuredSteps must be a JSON array.");
            return problems;
        }

        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            count++;
            if (count > MaxSteps)
            {
                problems.Add($"structuredSteps must not exceed {MaxSteps} steps.");
                break;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"Step {count}: must be a JSON object.");
                continue;
            }
            if (!item.TryGetProperty("order", out var order) ||
                order.ValueKind != JsonValueKind.Number ||
                !order.TryGetInt32(out var orderValue) ||
                orderValue < 1)
                problems.Add($"Step {count}: 'order' must be an integer >= 1.");
            if (!item.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(action.GetString()))
                problems.Add($"Step {count}: 'action' is required.");
            else if (action.GetString()!.Length > MaxActionLength)
                problems.Add($"Step {count}: 'action' must be at most {MaxActionLength} characters.");
            if (item.TryGetProperty("target", out var target) &&
                target.ValueKind == JsonValueKind.String &&
                target.GetString()!.Length > MaxTargetLength)
                problems.Add($"Step {count}: 'target' must be at most {MaxTargetLength} characters.");
            if (item.TryGetProperty("value", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString()!.Length > MaxValueLength)
                problems.Add($"Step {count}: 'value' must be at most {MaxValueLength} characters.");
        }
        return problems;
    }

    /// <summary>Parses validated step payloads into canonical form (ordered by 'order').</summary>
    public static IReadOnlyList<TestStep> Parse(JsonElement? steps)
    {
        if (steps is not { ValueKind: JsonValueKind.Array } array)
            return Array.Empty<TestStep>();
        return array.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object)
            .Select(i => new TestStep(
                i.TryGetProperty("order", out var o) && o.TryGetInt32(out var n) ? n : 0,
                i.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? string.Empty : string.Empty,
                i.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                i.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null))
            .OrderBy(s => s.Order)
            .ToList();
    }

    /// <summary>Semantic equality ignoring key order and formatting.</summary>
    public static bool ContentEquals(JsonElement? left, JsonElement? right)
    {
        var l = Parse(left);
        var r = Parse(right);
        return l.SequenceEqual(r);
    }
}
