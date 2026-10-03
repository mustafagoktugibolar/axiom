using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Application.Governance;

/// <summary>Outcome of parsing one governance source file.</summary>
public sealed record ParsedRecord(GovernanceRecord? Record, ImmutableArray<ValidationIssue> Issues)
{
    public bool IsValid => Record is not null && Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>Parses and schema-validates a governance source file into the domain model.</summary>
public interface IGovernanceRecordParser
{
    /// <summary>True when the path is a governance record file (by directory and extension).</summary>
    bool IsRecordPath(string path);

    ParsedRecord Parse(string path, string content);
}
