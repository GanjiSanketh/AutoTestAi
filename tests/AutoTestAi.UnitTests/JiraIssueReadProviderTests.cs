using System.Net;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Infrastructure.Jira;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Phase 4 Slice 5: Jira single-issue read — narrow projection,
/// auth, status mapping, minimal parsing, and secrecy.</summary>
public sealed class JiraIssueReadProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"10001","key":"ABC-123","self":"https://jira.test/browse/ABC-123","fields":{"summary":"Guest checkout","description":{"version":1,"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Flow."}]}]},"issuetype":{"name":"Story"},"project":{"key":"ABC"}}}""",
                    Encoding.UTF8, "application/json"),
            };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(Responder(request));
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

    private static JiraIssueRequest Request(string key = "ABC-123") => new("https://jira.test", key);

    [Fact]
    public async Task Success_UsesGet_WithNarrowProjection_AndBasicAuth()
    {
        var handler = new StubHandler();
        var result = await Create(handler).GetIssueAsync(Request(), "qa@example.com", "token-abc", CancellationToken.None);
        Assert.Equal("ABC-123", result.IssueKey);
        Assert.Equal("Guest checkout", result.Summary);
        Assert.Equal("Story", result.IssueTypeName);
        Assert.Equal("ABC", result.ProjectKey);
        Assert.Contains("Flow.", result.DescriptionAdfJson, StringComparison.Ordinal);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        var uri = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("/rest/api/3/issue/ABC-123", uri, StringComparison.Ordinal);
        Assert.Contains("fields=", uri, StringComparison.Ordinal);
        Assert.Contains("summary", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("comment", uri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attachment", uri, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.LastRequest.Content);
        var auth = handler.LastRequest.Headers.Authorization;
        Assert.Equal("Basic", auth!.Scheme);
        Assert.Equal("qa@example.com:token-abc",
            Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
    }

    [Fact]
    public async Task MissingDescription_ReturnsNullAdf()
    {
        var handler = new StubHandler();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"key":"ABC-7","fields":{"summary":"No desc","issuetype":{"name":"Task"},"project":{"key":"ABC"}}}""",
                Encoding.UTF8, "application/json"),
        };
        var result = await Create(handler).GetIssueAsync(Request("ABC-7"), "e", "t", CancellationToken.None);
        Assert.Null(result.DescriptionAdfJson);
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
            () => Create(handler).GetIssueAsync(Request(), "e", "t", CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"key":"ABC-1"}""")]
    [InlineData("""{"key":"ABC-1","fields":{"summary":"","issuetype":{"name":"Story"},"project":{"key":"ABC"}}}""")]
    [InlineData("""{"key":"ABC-1","fields":{"summary":"S","issuetype":{"name":""},"project":{"key":"ABC"}}}""")]
    [InlineData("""{"key":"ABC-1","fields":{"summary":"S","description":"legacy-string","issuetype":{"name":"Story"},"project":{"key":"ABC"}}}""")]
    public async Task MalformedBodies_MapToMalformed(string body)
    {
        var handler = new StubHandler();
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var ex = await Assert.ThrowsAsync<JiraProviderException>(
            () => Create(handler).GetIssueAsync(Request(), "e", "t", CancellationToken.None));
        Assert.Equal(JiraErrorKind.MalformedResponse, ex.Kind);
        Assert.DoesNotContain("legacy-string", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCredentials_MapsToAuthentication()
    {
        var ex = await Assert.ThrowsAsync<JiraProviderException>(
            () => Create(new StubHandler()).GetIssueAsync(Request(), "", "", CancellationToken.None));
        Assert.Equal(JiraErrorKind.Authentication, ex.Kind);
    }

    [Fact]
    public async Task InvalidBaseUrl_MapsToValidation()
    {
        var ex = await Assert.ThrowsAsync<JiraProviderException>(
            () => Create(new StubHandler()).GetIssueAsync(
                new JiraIssueRequest("http://127.0.0.1/x", "ABC-1"), "e", "t", CancellationToken.None));
        Assert.Equal(JiraErrorKind.Validation, ex.Kind);
    }
}
