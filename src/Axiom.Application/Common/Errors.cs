namespace Axiom.Application.Common;

public enum ErrorKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict,

    /// <summary>Governance could not be evaluated. Callers must never treat this as success (MCP contract).</summary>
    Unavailable,
}

/// <summary>Stable machine-readable error codes shared by REST, MCP and the CLI.</summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string NotFound = "NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string Conflict = "CONFLICT";
    public const string GovernanceUnavailable = "GOVERNANCE_UNAVAILABLE";
    public const string NoPublishedSnapshot = "NO_PUBLISHED_GOVERNANCE_SNAPSHOT";
    public const string ScmUnavailable = "SCM_UNAVAILABLE";
}

/// <summary>A structured application failure. Hosts translate it to their transport's error shape.</summary>
public sealed class AxiomException : Exception
{
    public AxiomException()
        : this(ErrorKind.Unavailable, ErrorCodes.GovernanceUnavailable, "Axiom could not complete the request.")
    {
    }

    public AxiomException(string message)
        : this(ErrorKind.Unavailable, ErrorCodes.GovernanceUnavailable, message)
    {
    }

    public AxiomException(string message, Exception innerException)
        : this(ErrorKind.Unavailable, ErrorCodes.GovernanceUnavailable, message, innerException: innerException)
    {
    }

    public AxiomException(ErrorKind kind, string code, string message, bool retryable = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        Retryable = retryable;
    }

    public ErrorKind Kind { get; }

    public string Code { get; }

    public bool Retryable { get; }

    public static AxiomException Invalid(string message) => new(ErrorKind.Validation, ErrorCodes.InvalidRequest, message);

    public static AxiomException NotFound(string what) => new(ErrorKind.NotFound, ErrorCodes.NotFound, $"{what} was not found.");

    public static AxiomException Forbidden(string message) => new(ErrorKind.Forbidden, ErrorCodes.Forbidden, message);

    public static AxiomException Unavailable(string code, string message, Exception? inner = null) =>
        new(ErrorKind.Unavailable, code, message, retryable: true, inner);
}
