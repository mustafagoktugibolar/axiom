using Axiom.Domain.Governance;

namespace Axiom.UnitTests.Governance;

public class GlobPatternTests
{
    [Theory]
    [InlineData("src/Legacy/Homepage/**", "src/Legacy/Homepage/a.cs", true)]
    [InlineData("src/Legacy/Homepage/**", "src/Legacy/Homepage/x/y/a.cs", true)]
    [InlineData("src/Legacy/Homepage/**", "src/Legacy/Homepage", true)]
    [InlineData("src/Legacy/Homepage/**", "src/Legacy/Other/a.cs", false)]
    [InlineData("**/*.cs", "a.cs", true)]
    [InlineData("**/*.cs", "src/deep/a.cs", true)]
    [InlineData("**/*.cs", "src/deep/a.ts", false)]
    [InlineData("src/*/Program.cs", "src/Api/Program.cs", true)]
    [InlineData("src/*/Program.cs", "src/Api/Sub/Program.cs", false)]
    [InlineData("src/?.cs", "src/a.cs", true)]
    [InlineData("src/?.cs", "src/ab.cs", false)]
    [InlineData("src/Payments.Infrastructure/**", "src\\Payments.Infrastructure\\Db.cs", true)]
    [InlineData("SRC/a.cs", "src/a.cs", false)]
    public void IsMatch_follows_segment_glob_semantics(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GlobPattern.Parse(pattern).IsMatch(path));

    [Theory]
    [InlineData("src/**", "src/Homepage/**", true)]
    [InlineData("src/Homepage/**", "src/**/*.cs", true)]
    [InlineData("src/a/**", "src/b/**", false)]
    [InlineData("**/*.cs", "**/*.ts", false)]
    [InlineData("docs/*.md", "docs/readme.md", true)]
    public void MayIntersect_detects_shared_paths(string a, string b, bool expected)
    {
        Assert.Equal(expected, GlobPattern.Parse(a).MayIntersect(GlobPattern.Parse(b)));
        Assert.Equal(expected, GlobPattern.Parse(b).MayIntersect(GlobPattern.Parse(a)));
    }

    [Theory]
    [InlineData("src/**", "src/Legacy/Homepage/**", true)]
    [InlineData("src/Legacy/Homepage/**", "src/**", false)]
    [InlineData("src/**", "src/a.cs", true)]
    [InlineData("src/*/a.cs", "src/x/a.cs", true)]
    [InlineData("src/*.cs", "src/**", false)]
    [InlineData("**", "anything/**/at/all", true)]
    [InlineData("src/a/**", "src/b/**", false)]
    public void Contains_is_sound(string outer, string inner, bool expected) =>
        Assert.Equal(expected, GlobPattern.Parse(outer).Contains(GlobPattern.Parse(inner)));
}
