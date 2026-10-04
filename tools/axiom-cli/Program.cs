using Axiom.Cli;

return await AxiomCli.RunAsync(args, Console.Out, Console.Error, Environment.GetEnvironmentVariable, handler: null, CancellationToken.None);
