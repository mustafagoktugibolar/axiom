using System.Globalization;
using Axiom.Domain.Policy.Custom;

// Axiom custom policy runner. Protocol: one JSON request on stdin, one JSON result on stdout, exit code 0.
// Any non-zero exit code means the runner crashed; the parent reports that as an error, never a pass.
// This process has no HTTP client, opens no files and no sockets, and loads no code from rule definitions.
// See docs/custom-policy-runner.md.
const int ExitInputTooLarge = 3;
const int ExitInternalError = 70;

var maxInput = ReadLimit("AXIOM_RUNNER_MAX_INPUT_BYTES", RunnerProtocol.DefaultMaxInputBytes);
var maxOutput = ReadLimit("AXIOM_RUNNER_MAX_OUTPUT_BYTES", RunnerProtocol.DefaultMaxOutputBytes);

try
{
    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    var chunk = new byte[16 * 1024];
    int read;
    while ((read = stdin.Read(chunk, 0, chunk.Length)) > 0)
    {
        buffer.Write(chunk, 0, read);
        if (buffer.Length > maxInput)
        {
            return ExitInputTooLarge;
        }
    }

    var response = CustomRuleRunnerCore.Run(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    if (response.Length > maxOutput)
    {
        response = RunnerProtocol.Serialize(new RunnerResponse(
            RunnerProtocol.Version, "error", [], CustomRuleCodes.OutputTooLarge, "The result exceeds the output cap."));
    }

    using var stdout = Console.OpenStandardOutput();
    stdout.Write(response, 0, response.Length);
    stdout.Flush();
    return 0;
}
#pragma warning disable CA1031 // The process boundary: any failure becomes a non-zero exit code, with no repository content in the output.
catch (Exception)
#pragma warning restore CA1031
{
    return ExitInternalError;
}

static int ReadLimit(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
        ? value
        : fallback;
