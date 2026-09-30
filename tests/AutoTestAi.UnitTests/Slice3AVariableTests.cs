using AutoTestAi.Application.Common;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3A: variable entry validation, precedence, substitution, and
/// secret-aware masking — all deterministic and side-effect free.</summary>
public sealed class Slice3AVariableTests
{
    // ---------- entry format ----------

    [Fact]
    public void ParseEntries_AcceptsValueAndSecretRefShapes()
    {
        var (plain, refs) = VariableModel.ParseEntries(
            """{"BASE_URL":{"value":"https://qa.example.com"},"API_TOKEN":{"secretRef":"env_secret:11111111-1111-1111-1111-111111111111"}}""");
        Assert.Equal("https://qa.example.com", plain["BASE_URL"]);
        Assert.Equal("env_secret:11111111-1111-1111-1111-111111111111", refs["API_TOKEN"]);
    }

    [Fact]
    public void ParseEntries_RejectsRawStringShape()
    {
        var ex = Assert.Throws<ValidationException>(() =>
            VariableModel.ParseEntries("""{"API_TOKEN":"actual-secret"}"""));
        Assert.Contains(ex.Errors, e => e.Field.Contains("API_TOKEN"));
    }

    [Fact]
    public void ParseEntries_RejectsBothValueAndSecretRef()
    {
        Assert.Throws<ValidationException>(() =>
            VariableModel.ParseEntries("""{"K":{"value":"a","secretRef":"env_secret:11111111-1111-1111-1111-111111111111"}}"""));
    }

    [Fact]
    public void ParseEntries_RejectsNeitherValueNorSecretRef()
    {
        Assert.Throws<ValidationException>(() => VariableModel.ParseEntries("""{"K":{}}"""));
    }

    [Fact]
    public void ParseEntries_RejectsLowercaseKey()
    {
        var ex = Assert.Throws<ValidationException>(() =>
            VariableModel.ParseEntries("""{"base_url":{"value":"x"}}"""));
        Assert.Contains(ex.Errors, e => e.Field.Contains("base_url"));
    }

    [Fact]
    public void ParseEntries_RejectsRawSecretRef()
    {
        var ex = Assert.Throws<ValidationException>(() =>
            VariableModel.ParseEntries("""{"API_TOKEN":{"secretRef":"hunter2-plaintext"}}"""));
        Assert.Contains(ex.Errors, e => e.Field.Contains("API_TOKEN"));
    }

    [Fact]
    public void ParseEntries_RejectsOversizedKey()
    {
        var key = new string('A', 65);
        Assert.Throws<ValidationException>(() =>
            VariableModel.ParseEntries($"{{\"{key}\":{{\"value\":\"x\"}}}}"));
    }

    [Fact]
    public void ParseEntries_EmptyObject_YieldsEmptyMaps()
    {
        var (plain, refs) = VariableModel.ParseEntries("{}");
        Assert.Empty(plain);
        Assert.Empty(refs);
    }

    [Fact]
    public void SerializeEntries_RoundTrips()
    {
        var plain = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" };
        var refs = new Dictionary<string, string> { ["T"] = "env_secret:11111111-1111-1111-1111-111111111111" };
        var json = VariableModel.SerializeEntries(plain, refs);
        var (plain2, refs2) = VariableModel.ParseEntries(json);
        Assert.Equal(plain, plain2);
        Assert.Equal(refs, refs2);
    }

    // ---------- precedence ----------

    [Fact]
    public void Merge_HigherScopeWins()
    {
        var merged = VariableModel.Merge(
            new Dictionary<string, string> { ["K"] = "system", ["S"] = "system" },
            new Dictionary<string, string> { ["K"] = "project" },
            new Dictionary<string, string> { ["K"] = "env" },
            new Dictionary<string, string> { ["K"] = "suite" },
            new Dictionary<string, string> { ["K"] = "override" },
            new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.Equal("override", merged.Plain["K"]);
        Assert.Equal("system", merged.Plain["S"]);
    }

    [Fact]
    public void Merge_SecretRefShadowsPlainValue()
    {
        var merged = VariableModel.Merge(
            null,
            new Dictionary<string, string> { ["T"] = "plain-project" },
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["T"] = "env_secret:11111111-1111-1111-1111-111111111111" },
            new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.False(merged.Plain.ContainsKey("T"));
        Assert.True(merged.SecretRefs.ContainsKey("T"));
    }

    // ---------- substitution ----------

    [Fact]
    public void Substitute_ReplacesPlaceholder()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BASE_URL"] = "https://qa.example.com",
        };
        Assert.Equal("https://qa.example.com/login",
            VariableModel.Substitute("${{ BASE_URL }}/login", values));
    }

    [Fact]
    public void Substitute_MissingKey_FailsDeterministically()
    {
        var ex = Assert.Throws<ConflictException>(() =>
            VariableModel.Substitute("${{ MISSING }}/x", new Dictionary<string, string>()));
        Assert.Contains("MISSING", ex.Message);
    }

    [Fact]
    public void Substitute_NoPlaceholders_ReturnsLiteral()
    {
        Assert.Equal("plain", VariableModel.Substitute("plain", new Dictionary<string, string>()));
    }

    // ---------- secret-aware masking ----------

    [Fact]
    public void MaskSecrets_ReplacesExactValuesRegardlessOfAction()
    {
        const string secret = "s3cr3t-assert-expected-value";
        var masked = VariableModel.MaskSecrets(
            $"Expected value '{secret}' but found 'other'",
            new[] { secret });
        Assert.DoesNotContain(secret, masked);
        Assert.Contains("[REDACTED]", masked);
    }

    [Fact]
    public void MaskSecrets_LongestFirst_PreventsPartialLeak()
    {
        var masked = VariableModel.MaskSecrets("abcdef abc", new[] { "abc", "abcdef" });
        Assert.DoesNotContain("abcdef", masked);
        Assert.DoesNotContain("abc", masked!.Replace("[REDACTED]", string.Empty));
    }

    [Fact]
    public void MaskSecrets_ShortTokensIgnored()
    {
        Assert.Equal("ab", VariableModel.MaskSecrets("ab", new[] { "ab" }));
    }

    // ---------- secret references ----------

    [Fact]
    public void SecretReference_RoundTrips()
    {
        var id = Guid.NewGuid();
        var reference = SecretReference.Create(id);
        Assert.True(SecretReference.IsValid(reference));
        Assert.True(SecretReference.TryParseSecretId(reference, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void SecretReference_RejectsRawValues()
    {
        Assert.False(SecretReference.IsValid("hunter2"));
        Assert.False(SecretReference.IsValid("Bearer abcdefgh"));
        Assert.False(SecretReference.IsValid(null));
        Assert.False(SecretReference.IsValid("env_secret:not-a-guid"));
    }

    // ---------- execution command ----------

    [Fact]
    public void ExecutionCommandFactory_RejectsRawSecretOverride()
    {
        var ex = Assert.Throws<ValidationException>(() => ExecutionCommandFactory.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "chromium",
            null,
            new Dictionary<string, string> { ["API_TOKEN"] = "raw-plaintext-secret" },
            null));
        Assert.Contains(ex.Errors, e => e.Field.Contains("secretRefOverrides"));
    }

    [Fact]
    public void ExecutionCommandFactory_AcceptsSecretRefOverride()
    {
        var command = ExecutionCommandFactory.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "chromium",
            new Dictionary<string, string> { ["BASE_URL"] = "https://staging.example.com" },
            new Dictionary<string, string> { ["API_TOKEN"] = "env_secret:11111111-1111-1111-1111-111111111111" },
            "key-1");
        Assert.Equal("https://staging.example.com", command.VariableOverrides["BASE_URL"]);
        Assert.Equal("env_secret:11111111-1111-1111-1111-111111111111", command.SecretRefOverrides["API_TOKEN"]);
    }

    [Fact]
    public void ExecutionCommandFactory_RejectsBadOverrideKey()
    {
        Assert.Throws<ValidationException>(() => ExecutionCommandFactory.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "chromium",
            new Dictionary<string, string> { ["bad-key"] = "x" }, null, null));
    }
}
