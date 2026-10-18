using Axiom.Domain.Policy.Custom;

namespace Axiom.Application.Policy.Custom;

/// <summary>One organization-owned custom rule definition (declared in configuration or supplied by a source).</summary>
public sealed record CustomRuleDefinition(string Name, string Version, string Description, string SpecJson)
{
    /// <summary>Rule IDs of custom rules always live under this namespace, so they can never shadow a built-in rule.</summary>
    public const string Namespace = "custom/";

    public string RuleId => Namespace + Name;
}

public sealed class CustomRuleDefinitionOptions
{
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = "1.0.0";

    public string Description { get; set; } = string.Empty;

    /// <summary>The rule in the custom rule language (JSON text, see docs/custom-policy-runner.md).</summary>
    public string Spec { get; set; } = string.Empty;
}

/// <summary>
/// Settings of the isolated custom rule runner, bound from <c>Axiom:Policy:CustomRunner</c>.
/// Off by default: with <see cref="Enabled"/> false no custom rule is ever registered or executed.
/// </summary>
public sealed class CustomRunnerOptions
{
    public const string SectionName = "Axiom:Policy:CustomRunner";

    public bool Enabled { get; set; }

    /// <summary>Executable that runs the runner. Wrap it (container runtime, <c>unshare -n</c>) to add OS-level isolation.</summary>
    public string Command { get; set; } = "dotnet";

    /// <summary>Arguments of <see cref="Command"/>; relative paths resolve against the application base directory.</summary>
    public List<string> Arguments { get; set; } = ["Axiom.PolicyRunner.dll"];

    public int TimeoutSeconds { get; set; } = 10;

    public int MaxInputBytes { get; set; } = RunnerProtocol.DefaultMaxInputBytes;

    public int MaxOutputBytes { get; set; } = RunnerProtocol.DefaultMaxOutputBytes;

    /// <summary>Memory cap of the runner process (managed heap hard limit; the parent also watches the working set).</summary>
    public int MaxMemoryMegabytes { get; set; } = 256;

    public List<CustomRuleDefinitionOptions> Rules { get; set; } = [];
}

public enum RunnerProcessStatus
{
    /// <summary>The process ended by itself before any limit was hit.</summary>
    Exited,

    /// <summary>The wall-clock limit was exceeded; the process was killed.</summary>
    TimedOut,

    /// <summary>The output cap was exceeded; the process was killed.</summary>
    OutputLimitExceeded,

    /// <summary>The memory cap was exceeded; the process was killed.</summary>
    MemoryLimitExceeded,

    /// <summary>The process could not be started.</summary>
    LaunchFailed,
}

public sealed record RunnerProcessRequest(byte[] Input, TimeSpan Timeout, int MaxOutputBytes, long MaxMemoryBytes);

public sealed record RunnerProcessResult(RunnerProcessStatus Status, int ExitCode, byte[] Output);

/// <summary>
/// Launches the runner as a separate OS process: writes <see cref="RunnerProcessRequest.Input"/> to its
/// stdin, reads at most <see cref="RunnerProcessRequest.MaxOutputBytes"/> from stdout, and kills the whole
/// process tree on any limit breach before returning the matching non-<see cref="RunnerProcessStatus.Exited"/> status.
/// </summary>
public interface ICustomRunnerProcess
{
    RunnerProcessResult Run(RunnerProcessRequest request);
}
