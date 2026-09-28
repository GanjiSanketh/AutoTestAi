namespace AutoTestAi.Application.Common;

/// <summary>Abstraction over UTC time to keep application services testable.</summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Minimal paged result envelope used by list use-cases.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
