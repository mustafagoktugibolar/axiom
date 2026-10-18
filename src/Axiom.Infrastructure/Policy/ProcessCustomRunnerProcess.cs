using System.Diagnostics;
using System.Globalization;
using Axiom.Application.Policy.Custom;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Policy;

/// <summary>
/// Runs the policy runner as a child OS process with a scrubbed environment (no inherited credentials),
/// a wall-clock timeout, an output cap and a memory cap. A breach kills the whole process tree.
/// </summary>
public sealed class ProcessCustomRunnerProcess : ICustomRunnerProcess
{
    private static readonly string[] InheritedVariables =
        ["PATH", "SystemRoot", "windir", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "TEMP", "TMP", "TMPDIR", "HOME"];

    private const long WorkingSetAllowanceBytes = 96L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly string _command;
    private readonly IReadOnlyList<string> _arguments;

    public ProcessCustomRunnerProcess(IOptions<CustomRunnerOptions> options)
        : this(options?.Value.Command ?? "dotnet", options?.Value.Arguments ?? [])
    {
    }

    public ProcessCustomRunnerProcess(string command, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        _command = command;
        _arguments = arguments;
    }

    public RunnerProcessResult Run(RunnerProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = new ProcessStartInfo(_command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in _arguments)
        {
            info.ArgumentList.Add(Resolve(argument));
        }

        info.Environment.Clear();
        foreach (var name in InheritedVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                info.Environment[name] = value;
            }
        }

        info.Environment["DOTNET_GCHeapHardLimit"] = request.MaxMemoryBytes.ToString("X", CultureInfo.InvariantCulture);
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["AXIOM_RUNNER_MAX_OUTPUT_BYTES"] = request.MaxOutputBytes.ToString(CultureInfo.InvariantCulture);
        info.Environment["AXIOM_RUNNER_MAX_INPUT_BYTES"] = request.Input.Length.ToString(CultureInfo.InvariantCulture);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new RunnerProcessResult(RunnerProcessStatus.LaunchFailed, -1, []);
        }

        using (process)
        {
            return Supervise(process, request);
        }
    }

    private static RunnerProcessResult Supervise(Process process, RunnerProcessRequest request)
    {
        var overflow = false;
        var input = Task.Run(() =>
        {
            try
            {
                process.StandardInput.BaseStream.Write(request.Input, 0, request.Input.Length);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process exited or was killed before reading everything; the supervisor reports why.
            }
        });
        var output = Task.Run(() =>
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var stream = process.StandardOutput.BaseStream;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > request.MaxOutputBytes)
                {
                    Volatile.Write(ref overflow, true);
                    return buffer.ToArray();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        });
        var errors = Task.Run(() =>
        {
            var chunk = new byte[4096];
            var stream = process.StandardError.BaseStream;
            while (stream.Read(chunk, 0, chunk.Length) > 0)
            {
                // Diagnostics are discarded: they could contain repository content.
            }
        });

        var clock = Stopwatch.StartNew();
        var status = RunnerProcessStatus.Exited;
        while (!process.WaitForExit(PollInterval))
        {
            if (clock.Elapsed > request.Timeout)
            {
                status = RunnerProcessStatus.TimedOut;
            }
            else if (Volatile.Read(ref overflow))
            {
                status = RunnerProcessStatus.OutputLimitExceeded;
            }
            else if (WorkingSet(process) > request.MaxMemoryBytes + WorkingSetAllowanceBytes)
            {
                status = RunnerProcessStatus.MemoryLimitExceeded;
            }

            if (status != RunnerProcessStatus.Exited)
            {
                Kill(process);
                break;
            }
        }

        // The pipes close when the process (tree) is gone; bound the wait in case a grandchild holds them.
        if (!Task.WaitAll([input, output, errors], TimeSpan.FromSeconds(2)))
        {
            Kill(process);
            Task.WaitAll([input, output, errors], TimeSpan.FromSeconds(2));
        }

        if (status == RunnerProcessStatus.Exited && Volatile.Read(ref overflow))
        {
            status = RunnerProcessStatus.OutputLimitExceeded;
            Kill(process);
        }

        var bytes = output.IsCompletedSuccessfully ? output.Result : [];
        return new RunnerProcessResult(status, process.HasExited ? process.ExitCode : -1, bytes);
    }

    private static long WorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static string Resolve(string argument) =>
        !Path.IsPathRooted(argument) && argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, argument)
            : argument;
}
