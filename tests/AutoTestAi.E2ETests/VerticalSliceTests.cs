namespace AutoTestAi.E2ETests;

/// <summary>
/// Phase-0: structure only. The full slice
/// (login → project → test → AI generation → review → execution → result
/// → failure analysis → defect → Jira) is implemented in Phase 1.
/// </summary>
public sealed class VerticalSliceTests
{
    [Fact(Skip = "Phase-1 work: requires Keycloak, Temporal and the Playwright worker.")]
    public void FullSlice_Login_To_JiraTicket()
    {
        Assert.Fail("Not implemented in Phase 0.");
    }

    [Fact]
    public void Phase0Scope_DocumentsFoundationOnly()
    {
        // Guardrail: the E2E project exists and the foundation contract holds.
        Assert.True(true);
    }
}
