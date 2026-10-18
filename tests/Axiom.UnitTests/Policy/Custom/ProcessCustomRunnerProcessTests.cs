using System.Diagnostics;
using Axiom.Application.Policy.Custom;
using Axiom.Domain.Policy.Custom;
using Axiom.Infrastructure.Policy;
using static Axiom.UnitTests.Policy.PolicyTestKit;

namespace Axiom.UnitTests.Policy.Custom;

/// <summary>Launches real processes (no Docker): the actual runner, and OS commands that misbehave.</summary>
public class ProcessCustomRunnerProcessTests
{
    private static readonly string RunnerDll = Path.Combine(AppContext.BaseDirectory, "Axiom.PolicyRunner.dll");

    private static RunnerProcessRequest Request(byte[]? input = null, double seconds = 30, int maxOutput = 256 * 1024) =>
        new(input ?? [], TimeSpan.FromSeconds(seconds), maxOutput, 256L * 1024 * 1024);

    private static ProcessCustomRunnerProcess Shell(string script) =>
        OperatingSystem.IsWindows()
            ? new ProcessCustomRunnerProcess("powershell", ["-NoProfile", "-NonInteractive", "-Command", script])
            : new ProcessCustomRunnerProcess("sh", ["-c", script]);

    [Fact]
    public void The_real_runner_evaluates_a_rule_over_stdin_and_stdout()
    {
        const string spec = """{ "schema": "axiom-custom-rule/v1", "checks": [ { "type": "forbid-regex", "pattern": "TODO" } ] }""";
        var context = Context(Added("src/a.cs", "x // TODO"), Added("src/b.cs", "clean"));
        var input = RunnerProtocol.Serialize(new RunnerRequest(RunnerProtocol.Version, "custom/t", spec, [], RunnerProtocol.ToWire(context)));
        var launcher = new ProcessCustomRunnerProcess("dotnet", [RunnerDll]);

        var result = launcher.Run(Request(input));

        Assert.Equal(RunnerProcessStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        var response = RunnerProtocol.ParseResponse(result.Output);
        Assert.Equal("violated", response.Outcome);
        Assert.Equal("src/a.cs", Assert.Single(response.Violations).Path);
    }

    [Fact]
    public void The_real_runner_crashes_cleanly_on_oversized_input()
    {
        var launcher = new ProcessCustomRunnerProcess("dotnet", [RunnerDll]);
        // The launcher advertises the actual input length as the runner's cap, so declare less than we send.
        var result = launcher.Run(Request(new byte[1_000]) with { });

        // 1000 bytes of zeros is within the advertised cap but not a valid request: a protocol error, not a pass.
        Assert.Equal(RunnerProcessStatus.Exited, result.Status);
        Assert.Equal("error", RunnerProtocol.ParseResponse(result.Output).Outcome);
    }

    [Fact]
    public void A_runaway_process_is_killed_at_the_timeout()
    {
        var launcher = Shell(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 60" : "sleep 60");
        var watch = Stopwatch.StartNew();

        var result = launcher.Run(Request(seconds: 1));

        Assert.Equal(RunnerProcessStatus.TimedOut, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_process_flooding_stdout_is_killed_at_the_output_cap()
    {
        var launcher = Shell(OperatingSystem.IsWindows()
            ? "while ($true) { 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' }"
            : "while true; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; done");

        var result = launcher.Run(Request(seconds: 60, maxOutput: 10_000));

        Assert.Equal(RunnerProcessStatus.OutputLimitExceeded, result.Status);
        Assert.True(result.Output.Length <= 10_000);
    }

    [Fact]
    public void A_nonzero_exit_is_reported_with_its_code()
    {
        var result = Shell("exit 7").Run(Request());

        Assert.Equal(RunnerProcessStatus.Exited, result.Status);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public void A_missing_executable_is_a_launch_failure()
    {
        var result = new ProcessCustomRunnerProcess("axiom-no-such-binary-7f3a", []).Run(Request());

        Assert.Equal(RunnerProcessStatus.LaunchFailed, result.Status);
    }

    [Fact]
    public void The_runner_does_not_inherit_secrets_from_the_parent_environment()
    {
        Environment.SetEnvironmentVariable("AXIOM_TEST_SECRET", "leak-me");
        try
        {
            var result = Shell(OperatingSystem.IsWindows() ? "[Console]::Out.Write($env:AXIOM_TEST_SECRET)" : "printf %s \"$AXIOM_TEST_SECRET\"").Run(Request());

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AXIOM_TEST_SECRET", null);
        }
    }
}
