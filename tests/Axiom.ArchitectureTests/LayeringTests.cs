using System.Reflection;
using NetArchTest.Rules;

namespace Axiom.ArchitectureTests;

/// <summary>Structural constraints from .kiro/steering/structure.md and docs/06-delivery/test-strategy.md.</summary>
public class LayeringTests
{
    private static readonly Assembly Domain = typeof(Axiom.Domain.Governance.GovernanceRecord).Assembly;
    private static readonly Assembly Application = typeof(Axiom.Application.Governance.IGovernanceRecordParser).Assembly;
    private static readonly Assembly Infrastructure = typeof(Axiom.Infrastructure.Governance.Parsing.GovernanceRecordParser).Assembly;

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must not depend on [{string.Join(", ", forbidden)}]: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Domain_depends_on_no_other_axiom_layer() =>
        AssertNoDependency(Domain, "Axiom.Application", "Axiom.Infrastructure", "Axiom.Api", "Axiom.Mcp", "Axiom.Workers");

    [Fact]
    public void Domain_references_no_third_party_or_framework_integration_assemblies()
    {
        var allowed = new[] { "System", "netstandard", "Microsoft.CSharp" };
        var offending = Domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !allowed.Any(prefix => name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_hosts() =>
        AssertNoDependency(Application, "Axiom.Infrastructure", "Axiom.Api", "Axiom.Mcp", "Axiom.Workers",
            "Microsoft.EntityFrameworkCore", "Npgsql", "LibGit2Sharp", "YamlDotNet", "Microsoft.AspNetCore");

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts() =>
        AssertNoDependency(Infrastructure, "Axiom.Api", "Axiom.Mcp", "Axiom.Workers");

    [Fact]
    public void Mcp_reaches_the_system_only_through_application_use_cases()
    {
        var mcp = Assembly.Load("Axiom.Mcp");
        AssertNoDependency(mcp, "Axiom.Infrastructure", "Microsoft.EntityFrameworkCore", "Npgsql");
    }

    [Fact]
    public void Policy_runner_cannot_reach_governance_state()
    {
        var runner = Assembly.Load("Axiom.PolicyRunner");
        AssertNoDependency(runner, "Axiom.Application", "Axiom.Infrastructure", "Microsoft.EntityFrameworkCore", "Npgsql");
    }
}
