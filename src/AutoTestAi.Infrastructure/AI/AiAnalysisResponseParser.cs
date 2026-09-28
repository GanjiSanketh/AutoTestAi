using System.Text.Json;
using AutoTestAi.Application.AI;

namespace AutoTestAi.Infrastructure.AI;

/// <summary>
/// Shared parsing from model JSON into the normalized analysis contract
/// (Slice 6 §5). Lenient about aliases, strict about required fields:
/// missing classification/summary/probable cause is malformed output.
/// </summary>
internal static class AiAnalysisResponseParser
{
    internal static AiAnalysisResult Parse(
        string provider,
        string? model,
        string? content,
        string promptVersion,
        long? inputTokens,
        long? outputTokens,
        long? totalTokens)
    {
        var json = AiProviderResponseParser.ExtractJson(provider, content);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Top-level JSON value is not an object.");
            var root = document.RootElement;

            var classification = GetString(root, "classification");
            var summary = GetString(root, "summary");
            var probableCause = GetString(root, "probableCause") ?? GetString(root, "rootCause");
            if (string.IsNullOrWhiteSpace(classification) ||
                string.IsNullOrWhiteSpace(summary) ||
                string.IsNullOrWhiteSpace(probableCause))
                throw AiProviderException.Malformed(provider,
                    $"AI provider '{provider}' returned analysis output with missing required fields. No analysis was saved.");

            decimal confidence = 0m;
            var confidenceReported = false;
            if (root.TryGetProperty("confidence", out var confidenceEl))
            {
                if (confidenceEl.ValueKind == JsonValueKind.Number && confidenceEl.TryGetDecimal(out var value))
                {
                    confidence = value;
                    confidenceReported = true;
                }
                else if (confidenceEl.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(confidenceEl.GetString(), out var parsed))
                {
                    confidence = parsed;
                    confidenceReported = true;
                }
            }

            var warnings = GetStringList(root, "warnings");
            if (!confidenceReported)
                warnings.Add("The provider did not report a confidence value.");

            var isLikelyDefect = false;
            if (root.TryGetProperty("isLikelyDefect", out var defectEl))
            {
                if (defectEl.ValueKind == JsonValueKind.True) isLikelyDefect = true;
                else if (defectEl.ValueKind == JsonValueKind.String &&
                    bool.TryParse(defectEl.GetString(), out var parsedBool))
                    isLikelyDefect = parsedBool;
            }

            return new AiAnalysisResult(
                Provider: provider,
                Model: model,
                Classification: classification!,
                RootCause: probableCause!,
                Confidence: confidence,
                Summary: summary,
                Evidence: GetStringList(root, "evidence"),
                Assumptions: GetStringList(root, "assumptions"),
                Warnings: warnings,
                RecommendedAction: GetString(root, "recommendedAction"),
                IsLikelyDefect: isLikelyDefect,
                PromptVersion: promptVersion,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                TotalTokens: totalTokens);
        }
        catch (JsonException ex)
        {
            throw AiProviderException.Malformed(provider,
                $"AI provider '{provider}' returned malformed analysis JSON. No analysis was saved.", ex);
        }
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
