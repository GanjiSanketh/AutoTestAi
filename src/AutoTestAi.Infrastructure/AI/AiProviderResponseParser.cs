using System.Text.Json;
using AutoTestAi.Application.AI;

namespace AutoTestAi.Infrastructure.AI;

/// <summary>
/// Shared parsing from model JSON into the normalized contract (Slice 4 §3/§13).
/// Accepts the required schema plus small aliases real models emit ("steps",
/// "code"). Anything unparseable becomes a controlled MalformedResponse error —
/// never a partially trusted object.
/// </summary>
internal static class AiProviderResponseParser
{
    internal sealed record ParsedTestOutput(
        string? Title,
        string? Description,
        string? Framework,
        string? Platform,
        List<AiStructuredStep> Steps,
        string SourceCode,
        List<string> Assumptions,
        List<string> Warnings);

    internal static ParsedTestOutput Parse(string provider, string? content, AiGenerationRequest request)
    {
        var json = ExtractJson(provider, content);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw AiProviderException.Malformed(provider,
                $"AI provider '{provider}' returned malformed JSON output. No test was saved.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw AiProviderException.Malformed(provider,
                    $"AI provider '{provider}' returned a non-object JSON payload. No test was saved.");

            var root = document.RootElement;
            var steps = ParseSteps(provider, root);
            var source = GetString(root, "sourceCode") ?? GetString(root, "code") ?? string.Empty;

            return new ParsedTestOutput(
                GetString(root, "title"),
                GetString(root, "description"),
                GetString(root, "framework"),
                GetString(root, "platform"),
                steps,
                source,
                GetStringList(root, "assumptions"),
                GetStringList(root, "warnings"));
        }
    }

    private static List<AiStructuredStep> ParseSteps(string provider, JsonElement root)
    {
        var steps = new List<AiStructuredStep>();
        if (!root.TryGetProperty("structuredSteps", out var array) &&
            !root.TryGetProperty("steps", out array))
            return steps;
        if (array.ValueKind != JsonValueKind.Array)
            throw AiProviderException.Malformed(provider,
                $"AI provider '{provider}' returned 'structuredSteps' that is not an array. No test was saved.");

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var order = 0;
            if (item.TryGetProperty("order", out var orderEl))
            {
                if (orderEl.ValueKind == JsonValueKind.Number && orderEl.TryGetInt32(out var n)) order = n;
                else if (orderEl.ValueKind == JsonValueKind.String && int.TryParse(orderEl.GetString(), out var s)) order = s;
            }
            if (order < 1) order = steps.Count + 1;
            var action = GetString(item, "action") ?? string.Empty;
            // Legacy alias: {order, action, expected} → value.
            var value = GetString(item, "value") ?? GetString(item, "expected");
            steps.Add(new AiStructuredStep(order, action, GetString(item, "target"), value));
        }
        return steps;
    }

    internal static string ExtractJson(string provider, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw AiProviderException.Malformed(provider,
                $"AI provider '{provider}' returned an empty response. No test was saved.");
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }
        if (string.IsNullOrWhiteSpace(text))
            throw AiProviderException.Malformed(provider,
                $"AI provider '{provider}' returned an empty response. No test was saved.");
        return text;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    private static List<string> GetStringList(JsonElement element, string name)
    {
        var list = new List<string>();
        if (!element.TryGetProperty(name, out var value)) return list;
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            if (!string.IsNullOrWhiteSpace(single)) list.Add(single);
            return list;
        }
        if (value.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text!);
            }
        }
        return list;
    }
}
