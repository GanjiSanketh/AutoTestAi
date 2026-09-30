using System.Text.Json;
using AutoTestAi.Application.AI;

namespace AutoTestAi.Application.SelfHealing;

/// <summary>
/// Structured prompt for locator recovery (Slice 11 §12). The model receives
/// bounded redacted evidence only and must return locator DATA in a fixed
/// schema. The prompt explicitly forbids code, scripts, and explanations
/// outside the schema — and the response is validated regardless.
/// </summary>
public sealed class SelfHealingAiPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public (string SystemPrompt, string UserPrompt, string PromptVersion) Build(AiHealingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        const string systemPrompt =
            "You recover Playwright locators after minor DOM changes. " +
            "Return ONLY JSON: {\"candidates\": [{\"strategy\": \"css|xpath|role|text|testid\", \"value\": \"...\", \"reason\": \"...\"}]}. " +
            "Rules: suggest locators ONLY from the supplied evidence; never invent JavaScript, eval, shell commands, or browser scripts; " +
            "prefer stable test attributes, accessibility roles, and exact visible text; never use indexes, nth-child, or generated CSS classes; " +
            "at most 8 candidates ordered most-likely first.";
        var user = new
        {
            action = request.Evidence.Action,
            originalTarget = request.Evidence.OriginalTarget,
            allowedStrategies = request.AllowedStrategies,
            domFragment = request.Evidence.DomFragment,
            attributes = request.Evidence.Attributes,
            nearbyText = request.Evidence.NearbyText,
        };
        return (systemPrompt, JsonSerializer.Serialize(user, JsonOptions), AiPromptVersions.SelfHealingV1);
    }
}
