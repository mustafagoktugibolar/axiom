using Axiom.Workers;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAxiomWorkers(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
