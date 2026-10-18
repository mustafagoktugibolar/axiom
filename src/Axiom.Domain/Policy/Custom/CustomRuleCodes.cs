namespace Axiom.Domain.Policy.Custom;

/// <summary>
/// Stable reason codes for a custom rule that could not be evaluated. They are the only detail recorded
/// in the error of a <see cref="PolicyRunResult"/>, so they never quote repository content.
/// </summary>
public static class CustomRuleCodes
{
    public const string Prefix = "CUSTOM_RULE_";

    /// <summary>The rule definition is not valid in the custom rule language.</summary>
    public const string InvalidRule = Prefix + "INVALID_RULE";

    /// <summary>The binding's parameters do not fit the rule's declared parameters.</summary>
    public const string InvalidParameters = Prefix + "INVALID_PARAMETERS";

    /// <summary>The request exceeded the input size cap and was not sent to the runner.</summary>
    public const string InputTooLarge = Prefix + "INPUT_TOO_LARGE";

    /// <summary>The runner produced more output than the cap and was killed.</summary>
    public const string OutputTooLarge = Prefix + "OUTPUT_TOO_LARGE";

    /// <summary>The runner exceeded its wall-clock budget and was killed.</summary>
    public const string Timeout = Prefix + "TIMEOUT";

    /// <summary>The runner exceeded its memory cap and was killed.</summary>
    public const string MemoryLimit = Prefix + "MEMORY_LIMIT";

    /// <summary>The in-process step budget (work done on repository content) was exhausted.</summary>
    public const string StepBudgetExceeded = Prefix + "STEP_BUDGET_EXCEEDED";

    /// <summary>A regular expression exceeded its match timeout.</summary>
    public const string RegexTimeout = Prefix + "REGEX_TIMEOUT";

    /// <summary>The runner exited abnormally.</summary>
    public const string Crash = Prefix + "CRASH";

    /// <summary>The runner's output was not a valid, bounded result.</summary>
    public const string MalformedOutput = Prefix + "MALFORMED_OUTPUT";

    /// <summary>The runner process could not be started.</summary>
    public const string LaunchFailed = Prefix + "LAUNCH_FAILED";

    /// <summary>The runner did not understand the request (version mismatch or malformed request).</summary>
    public const string BadRequest = Prefix + "BAD_REQUEST";
}

/// <summary>A custom rule failed in a way that must be reported as <see cref="PolicyOutcome.Error"/> with a stable code.</summary>
public sealed class PolicyRuleExecutionException : Exception
{
    public PolicyRuleExecutionException()
        : this(CustomRuleCodes.Crash)
    {
    }

    public PolicyRuleExecutionException(string code)
        : base(code)
    {
        Code = code;
    }

    public PolicyRuleExecutionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PolicyRuleExecutionException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Raised by the rule language for an invalid definition or invalid parameters.</summary>
public sealed class CustomRuleException : Exception
{
    public CustomRuleException()
        : this(CustomRuleCodes.InvalidRule, "Invalid custom rule.")
    {
    }

    public CustomRuleException(string message)
        : this(CustomRuleCodes.InvalidRule, message)
    {
    }

    public CustomRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = CustomRuleCodes.InvalidRule;
    }

    public CustomRuleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
