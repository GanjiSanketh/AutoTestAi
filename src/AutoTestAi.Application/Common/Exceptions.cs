namespace AutoTestAi.Application.Common;

/// <summary>Single field-level validation failure, serialized into the error envelope details.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>Request failed validation. Maps to 400 VALIDATION_ERROR with field details.</summary>
public sealed class ValidationException : Exception
{
    public IReadOnlyList<FieldError> Errors { get; }

    public ValidationException(string message, IReadOnlyList<FieldError>? errors = null)
        : base(message)
        => Errors = errors ?? Array.Empty<FieldError>();

    public static void ThrowIfInvalid(List<FieldError> errors, string summary = "One or more validation errors occurred.")
    {
        if (errors.Count > 0)
            throw new ValidationException(summary, errors);
    }
}

/// <summary>State conflict (duplicate key, duplicate member). Maps to 409 CONFLICT.</summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>Resource genuinely not found (caller is already authorized). Maps to 404 NOT_FOUND.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}
