using System.Net;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Infrastructure.Jira;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 7 §38: Jira HTTP adapter — transport, auth, mapping, secrecy.</summary>
public sealed class JiraProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = req =>
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"id":"10001","key":"ABC-123","self":"https://jira.test/browse/ABC-123"}""",
                    Encoding.UTF8, "application/json"),
            };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);
            return Responder(request);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static JiraTicketProvider Create(StubHandler handler, int timeoutSeconds = 30)
        => new(new StubFactory(handler),
            Options.Create(new JiraOptions { TimeoutSeconds = timeoutSeconds }),
            NullLogger<JiraTicketProvider>.Instance);

    private static JiraCreateRequest Request() => new(
        "https://jira.test", "ABC", "Bug", "[AutoTestAI] Login 500", "AutoTest AI Defect", "High");

    [Fact]
    public async Task Success_SendsCorrectShape_WithBasicAuth_AndParses()
    {
        var handler = new StubHandler();
        var provider = Create(handler);
        var result = await provider.CreateIssueAsync(Request(), "qa@example.com", "token-abc", CancellationToken.None);
        Assert.Equal("ABC-123", result.ExternalKey);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("/rest/api/3/issue", handler.LastRequest.RequestUri!.ToString());
        var auth = handler.LastRequest.Headers.Authorization;
        Assert.Equal("Basic", auth!.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal("qa@example.com:token-abc", decoded);
        var body = handler.LastBody!;
        using var doc = JsonDocument.Parse(body);
        var fields = doc.RootElement.GetProperty("fields");
        Assert.Equal("ABC", fields.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("Bug", fields.GetProperty("issuetype").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, JiraErrorKind.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, JiraErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, JiraErrorKind.Permission)]
    [InlineData(HttpStatusCode.NotFound, JiraErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, JiraErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, JiraErrorKind.Unavailable)]
    public async Task StatusCodes_MapCorrectly(HttpStatusCode status, JiraErrorKind kind)
    {
        var handler = new StubHandler();
        handler.Responder = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"errorMessages":["nope"]}""", Encoding.UTF8, "application/json"),
        };
        var ex = await Assert.ThrowsAsync<JiraProviderException>(
            () => Create(handler).CreateIssueAsync(Request(), "e", "t", CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain("token", ex.Message);
    }

    [Fact]
    public async Task MalformedJson_MapsToMalformed()
    {
        var handler = new StubHandler();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
        };
        var ex = await Assert.ThrowsAsync<JiraProviderException>(
            () => Create(handler).CreateIssueAsync(Request(), "e", "t", CancellationToken.None));
        Assert.Equal(JiraErrorKind.MalformedResponse, ex.Kind);
    }

    [Fact]
    public async Task MissingFields_MapsToMalformed()
    {
        var handler = new StubHandler();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":null}""", Encoding.UTF8, "application/json"),
        };
        await Assert.ThrowsAsync<JiraProviderException>(
            () => Create(handler).CreateIssueAsync(Request(), "e", "t", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var handler = new StubHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(handler).CreateIssueAsync(Request(), "e", "t", cts.Token));
    }
}
