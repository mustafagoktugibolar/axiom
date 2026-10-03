using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;
using Axiom.UnitTests.Governance;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Axiom.UnitTests.Resolution;

/// <summary>Property tests for the core correctness properties P1, P3, P4, P5 and P6 (task 3.9).</summary>
public class ResolverPropertyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Repositories = ["gateway", "billing", "orders"];
    private static readonly string[] Paths = ["src/a/x.cs", "src/b/y.cs", "docs/readme.md"];
    private static readonly string[] Globs = ["src/**", "src/a/**", "docs/**", "**/*.cs"];

    public sealed record World(GovernanceRecord[] Records, EvaluationScope Change, bool HardGate)
    {
        public ResolutionResult Resolve() => DecisionResolver.Resolve(new ResolutionInput(
            [.. Records.Select(r => new RecordRevision(r, new SourceProvenance("gov", "c1", r.Id, "b"), 1))], Change, Now, HardGate));
    }

    private static Gen<Scope> ScopeGen =>
        from repos in Gen.SubListOf(Repositories)
        from globs in Gen.SubListOf(Globs)
        select Scope.Of((ScopeDimension.Repository, [.. repos]), (ScopeDimension.Path, [.. globs]));

    private static Gen<GovernanceRecord> GoverningGen(int index, int total) =>
        from status in Gen.Elements(Enum.GetValues<LifecycleStatus>())
        from scope in ScopeGen
        from exemptable in Gen.Elements(true, true, false)
        from kind in Gen.Elements(RecordKind.Decision, RecordKind.Standard, RecordKind.Principle, RecordKind.Goal)
        from verdict in Gen.Elements(Enum.GetValues<EnforcementLevel>())
        from conflictsWith in Gen.SubListOf(Enumerable.Range(0, total).Where(i => i != index).Select(i => $"REC-{i:000}"))
        select TestRecords.Decision($"REC-{index:000}", status, scope, exemptable, verdict: verdict, kind: kind,
            relations: conflictsWith.Take(1).Select(id => new Relation(RelationKind.ConflictsWith, id)));

    private static Gen<GovernanceRecord> ExceptionGen(int index, int governing) =>
        from target in Gen.Choose(0, governing - 1)
        from scope in ScopeGen
        from status in Gen.Elements(LifecycleStatus.Accepted, LifecycleStatus.Accepted, LifecycleStatus.Proposed, LifecycleStatus.Rejected)
        from startOffset in Gen.Choose(-10, 5)
        from length in Gen.Choose(1, 12)
        select TestRecords.Exception($"EXC-{index:000}", [$"REC-{target:000}"], scope, Now.AddDays(startOffset), Now.AddDays(startOffset + length), status);

    private static Gen<World> WorldGen =>
        from governing in Gen.Choose(1, 6)
        from records in Gen.CollectToArray(Enumerable.Range(0, governing).Select(i => GoverningGen(i, governing)))
        from exceptionCount in Gen.Choose(0, 4)
        from exceptions in Gen.CollectToArray(Enumerable.Range(0, exceptionCount).Select(i => ExceptionGen(i, governing)))
        from repo in Gen.Elements(Repositories)
        from paths in Gen.NonEmptyListOf(Gen.Elements(Paths))
        from pathsKnown in Gen.Elements(true, true, false)
        from hardGate in Gen.Elements(true, false)
        let change = EvaluationScope.Empty.With(ScopeDimension.Repository, $"repo:{repo}")
        select new World([.. records, .. exceptions], pathsKnown ? change.With(ScopeDimension.Path, paths) : change, hardGate);

    private static Property ForAllWorlds(Func<World, bool> property) => Prop.ForAll(WorldGen.ToArbitrary(), property);

    [Property(MaxTest = 500)]
    public Property P1_only_accepted_records_govern() => ForAllWorlds(world =>
        world.Resolve().Applicable.All(a => a.Record.Status == LifecycleStatus.Accepted && a.Record.Kind != RecordKind.Exception));

    [Property(MaxTest = 300)]
    public Property P3_resolution_is_deterministic_and_order_independent() => ForAllWorlds(world =>
    {
        var forward = world.Resolve();
        var reversed = (world with { Records = [.. world.Records.Reverse()] }).Resolve();
        return Describe(forward) == Describe(reversed) && Describe(forward) == Describe(world.Resolve());
    });

    [Property(MaxTest = 500)]
    public Property P4_unresolved_conflict_never_yields_silent_allow() => ForAllWorlds(world =>
    {
        var result = world.Resolve();
        var inForce = result.Applicable.Where(a => a.WaivedBy.IsEmpty && a.RefinedBy.IsEmpty).ToDictionary(a => a.Revision.Id);
        var declared = inForce.Values
            .SelectMany(a => a.Record.RelatedIds(RelationKind.ConflictsWith).Where(inForce.ContainsKey).Select(b => (a, b: inForce[b])))
            .ToArray();

        return declared.All(pair =>
        {
            var conflict = result.Conflicts.SingleOrDefault(c =>
                (c.FirstRecordId == pair.a.Revision.Id && c.SecondRecordId == pair.b.Revision.Id)
                || (c.FirstRecordId == pair.b.Revision.Id && c.SecondRecordId == pair.a.Revision.Id));
            if (conflict is null)
            {
                return false;
            }

            return conflict.IsResolved
                ? pair.a.Rank != pair.b.Rank
                : conflict.Severity >= EnforcementLevel.RequireReview && (!world.HardGate || conflict.Severity == EnforcementLevel.Block);
        });
    });

    [Property(MaxTest = 500)]
    public Property P5_exception_only_affects_its_scope_and_validity_window() => ForAllWorlds(world =>
    {
        var result = world.Resolve();
        var byId = world.Records.ToDictionary(r => r.Id);
        return result.Applicable.All(applied => applied.WaivedBy.Concat(applied.PartiallyWaivedBy).All(exceptionId =>
        {
            var exception = byId[exceptionId];
            var containment = ScopeMatcher.Contain(exception.Scope, world.Change).Containment;
            return exception.Status == LifecycleStatus.Accepted
                && exception.Exception!.IsActiveAt(Now)
                && exception.Exception.Targets.Contains(applied.Revision.Id)
                && containment == (applied.WaivedBy.Contains(exceptionId) ? Containment.Full : Containment.Partial);
        }));
    });

    [Property(MaxTest = 500)]
    public Property P6_non_exemptable_controls_survive_every_exception_and_refinement() => ForAllWorlds(world =>
        world.Resolve().Applicable
            .Where(a => a.Record.IsNonExemptable)
            .All(a => a.IsEffective && a.PartiallyWaivedBy.IsEmpty && a.Rank == PrecedenceRank.HardControl));

    private static string Describe(ResolutionResult result) => string.Join("\n",
        result.Applicable.Select(a => $"A {a.Revision.Id} {a.Rank} {a.IsEffective} {string.Join(",", a.WaivedBy)} {string.Join(",", a.PartiallyWaivedBy)} {string.Join("|", a.Reasons)}")
            .Concat(result.Exceptions.Select(e => $"E {e.Revision.Id} {e.Coverage}"))
            .Concat(result.Conflicts.Select(c => $"C {c.Code} {c.FirstRecordId} {c.SecondRecordId} {c.Severity} {c.PrevailingRecordId}"))
            .Concat(result.Trace.Select(t => $"T {t.RecordId} {t.Selected} {string.Join("|", t.Reasons)}")));
}
