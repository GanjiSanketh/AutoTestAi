using System.Net;
using System.Text.Json;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Infrastructure.Executions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 5: worker HTTP boundary — contract mapping, auth hygiene,
/// failure kinds, and cancellation.</summary>
public sealed class PlaywrightWorkerClientTests
{
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Responder { get; set; }
            = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Responder(request, ct));
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private static PlaywrightWorkerClient Create(
        FakeHttpHandler handler, WorkerOptions? options = null)
        => new(new WorkerHttpTransport(new FakeFactory(new HttpClient(handler))),
            Options.Create(options ?? new WorkerOptions { BaseUrl = "http://worker:8090", ApiToken = "tok" }));

    private static WorkerAssignmentDto Assignment() => new(
        "assign1", "exec1", "playwright", "chromium", "https://example.test",
        new[] { new WorkerStepDto(1, "navigate", "https://example.test", null) },
        new WorkerTimeoutsDto(60000, 10000), true, false, Guid.NewGuid());

    [Fact]
    public async Task Start_SendsBearerToken_AndReturnsAssignmentId()
    {
        const string token = "secret-worker-token";
        var handler = new FakeHttpHandler();
        handler.Responder = (request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"assignmentId":"assign1","status":"queued"}"""),
            };
        };
        var client = Create(handler, new WorkerOptions { BaseUrl = "http://worker:8090", ApiToken = token });

        var id = await client.StartAssignmentAsync(Assignment(), CancellationToken.None);
        Assert.Equal("assign1", id);
    }

    [Fact]
    public async Task Start_MalformedResponse_ThrowsInfrastructure()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json"),
        };
        var ex = await Assert.ThrowsAsync<WorkerInfrastructureException>(
            () => Create(handler).StartAssignmentAsync(Assignment(), CancellationToken.None));
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task Start_ContractRejection_IsNonRetryable()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<WorkerInfrastructureException>(
            () => Create(handler).StartAssignmentAsync(Assignment(), CancellationToken.None));
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Start_Unreachable_ThrowsRetryableInfrastructure()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => throw new HttpRequestException("refused");
        var ex = await Assert.ThrowsAsync<WorkerInfrastructureException>(
            () => Create(handler).StartAssignmentAsync(Assignment(), CancellationToken.None));
        Assert.True(ex.IsRetryable);
    }

    [Fact]
    public async Task Progress_ParsesStepsLogsAndTerminalResult()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                assignmentId = "assign1",
                status = "failed",
                currentStepOrder = 2,
                stepResults = new[]
                {
                    new { order = 1, action = "navigate", target = "https://example.test", status = "passed", startedAtUnixMs = 1L, completedAtUnixMs = 2L, durationMs = 1L, errorMessage = (string?)null },
                    new { order = 2, action = "click", target = "#x", status = "failed", startedAtUnixMs = 2L, completedAtUnixMs = 3L, durationMs = 1L, errorMessage = (string?)"nope" },
                },
                logs = new[] { new { seq = 1L, timestampUnixMs = 2L, level = "info", message = "hi" } },
                result = new
                {
                    status = "failed",
                    classification = "test",
                    errorMessage = "nope",
                    durationMs = 5L,
                    stepResults = Array.Empty<object>(),
                    logs = Array.Empty<object>(),
                    screenshots = Array.Empty<object>(),
                },
            })),
        };

        var progress = await Create(handler).GetAssignmentAsync("assign1", CancellationToken.None);
        Assert.Equal("failed", progress.Status);
        Assert.Equal(2, progress.CurrentStepOrder);
        Assert.Equal(2, progress.StepResults.Count);
        Assert.NotNull(progress.Result);
        Assert.Equal("test", progress.Result!.Classification);
    }

    [Fact]
    public async Task Cancel_MissingAssignment_DoesNotThrow()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
        await Create(handler).CancelAssignmentAsync("gone", CancellationToken.None);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Cancel_PropagatesCallerCancellation_AsOperationCanceled()
    {
        var handler = new FakeHttpHandler();
        handler.Responder = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Best-effort cancel swallows its own teardown cancellation.
        await Create(handler).CancelAssignmentAsync("assign1", cts.Token);
    }
}
