using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Custom;

namespace Axiom.Application.Policy.Custom;

/// <summary>
/// An <see cref="IPolicyRule"/> for one custom rule definition. It never evaluates anything itself: the
/// definition and a bounded slice of the context go to the isolated runner process, and only a validated,
/// bounded result comes back. Every failure surfaces as an exception the policy engine reports as
/// <see cref="PolicyOutcome.Error"/> (P9), with a stable <see cref="CustomRuleCodes"/> reason.
/// </summary>
public sealed class CustomRuleExecutor : IPolicyRule
{
    private readonly CustomRuleDefinition _definition;
    private readonly ICustomRunnerProcess _process;
    private readonly CustomRunnerOptions _options;
    private readonly CustomRuleSpec? _spec;
    private readonly string? _specError;

    public CustomRuleExecutor(CustomRuleDefinition definition, ICustomRunnerProcess process, CustomRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        _definition = definition;
        _process = process;
        _options = options;

        // Version carries a hash of the definition text, so a changed definition is a different, traceable rule version (5.9).
        var specHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition.SpecJson)))[..16];
        Version = $"{definition.Version}+spec.{specHash}";

        try
        {
            _spec = CustomRuleSpec.Parse(definition.SpecJson);
        }
        catch (CustomRuleException ex)
        {
            _specError = ex.Message;
        }
    }

    public string RuleId => _definition.RuleId;

    public string Version { get; }

    public string Description => _definition.Description;

    public IReadOnlyDictionary<string, string> Parameters =>
        _spec is null
            ? ImmutableSortedDictionary<string, string>.Empty
            : _spec.Parameters.ToImmutableSortedDictionary(
                p => p.Key,
                p => p.Value is null ? "Required." : $"Optional. Default: {p.Value}",
                StringComparer.Ordinal);

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameters);

        if (_spec is null)
        {
            throw new PolicyParameterException($"Custom rule '{RuleId}' is invalid: {_specError}");
        }

        try
        {
            _ = _spec.Bind(parameters);
        }
        catch (CustomRuleException ex)
        {
            throw new PolicyParameterException(ex.Message);
        }

        var request = new RunnerRequest(
            RunnerProtocol.Version,
            RuleId,
            _definition.SpecJson,
            new Dictionary<string, string>(parameters, StringComparer.Ordinal),
            RunnerProtocol.ToWire(context));
        var input = RunnerProtocol.Serialize(request);
        if (input.Length > _options.MaxInputBytes)
        {
            throw new PolicyRuleExecutionException(CustomRuleCodes.InputTooLarge);
        }

        var result = _process.Run(new RunnerProcessRequest(
            input,
            TimeSpan.FromSeconds(_options.TimeoutSeconds),
            _options.MaxOutputBytes,
            _options.MaxMemoryMegabytes * 1024L * 1024L));

        var code = result.Status switch
        {
            RunnerProcessStatus.TimedOut => CustomRuleCodes.Timeout,
            RunnerProcessStatus.OutputLimitExceeded => CustomRuleCodes.OutputTooLarge,
            RunnerProcessStatus.MemoryLimitExceeded => CustomRuleCodes.MemoryLimit,
            RunnerProcessStatus.LaunchFailed => CustomRuleCodes.LaunchFailed,
            _ when result.ExitCode != 0 => CustomRuleCodes.Crash,
            _ when result.Output is null || result.Output.Length > _options.MaxOutputBytes => CustomRuleCodes.OutputTooLarge,
            _ => null,
        };
        if (code is not null)
        {
            throw new PolicyRuleExecutionException(code);
        }

        var response = RunnerProtocol.ParseResponse(result.Output);
        if (response.Outcome == "error")
        {
            if (response.Code is CustomRuleCodes.InvalidRule or CustomRuleCodes.InvalidParameters)
            {
                throw new PolicyParameterException($"Custom rule '{RuleId}': {response.Message}");
            }

            throw new PolicyRuleExecutionException(
                response.Code!.StartsWith(CustomRuleCodes.Prefix, StringComparison.Ordinal) ? response.Code : CustomRuleCodes.Crash);
        }

        return [.. response.Violations.Select(v => new PolicyViolation(v.Message, v.Path, v.Line, v.Evidence))];
    }
}
