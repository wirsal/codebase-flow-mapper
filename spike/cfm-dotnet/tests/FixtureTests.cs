// Checks cfm-dotnet against the hand-written expectations of each fixture (CFM-503).
using CfmDotnet;
using Xunit;

public class FixtureTests
{
    static readonly string Testdata = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../testdata"));

    static Graph Analyze(string fixture) => new Analyzer(Loader.Load(Path.Combine(Testdata, fixture), Array.Empty<string>(), null)).Run();

    static string Label(Node n) => n.Kind switch
    {
        "endpoint" => $"{n.Method} {n.Path}",
        "external" => $"ext:{(n.Pkg == null ? "" : n.Pkg + ".")}{n.Name}",
        "unresolved" => $"unresolved:{n.Name}",
        _ => n.Name,
    };

    [Theory]
    [InlineData("asmx-legacy")]
    public void MatchesHandWrittenExpectations(string fixture)
    {
        var g = Analyze(fixture);
        var byId = g.Nodes.ToDictionary(n => n.Id);
        var edges = g.Edges.Select(e => (src: Label(byId[e.Source]), dst: Label(byId[e.Target]), e.Method, e.Confidence)).ToList();
        var fails = new List<string>();
        foreach (var raw in File.ReadLines(Path.Combine(Testdata, fixture, fixture + ".expected.txt")))
        {
            var line = raw.Trim();
            if (line == "" || line.StartsWith('#')) continue;
            if (line.StartsWith("! file "))
            {
                var prefix = line[7..];
                if (g.Nodes.Any(n => n.Loc?.File.StartsWith(prefix, StringComparison.Ordinal) == true)) fails.Add("unexpected node under " + prefix);
            }
            else if (line.StartsWith("! "))
            {
                var parts = line[2..].Split(" -> ");
                var hit = parts.Length == 2 ? edges.Any(e => e.src == parts[0] && e.dst == parts[1]) : g.Nodes.Any(n => Label(n) == parts[0]);
                if (hit) fails.Add("unexpected: " + line[2..]);
            }
            else if (line.StartsWith("body "))
            {
                var f = line.Split(' ');
                var fn = g.Nodes.FirstOrDefault(n => n.Kind == "function" && n.Name == f[1]);
                if (fn?.Body is not { } b || b.Type != f[2] || b.Binder != f[3]) fails.Add($"body: {line} (got {fn?.Body?.Type} {fn?.Body?.Binder})");
            }
            else if (line.StartsWith("test "))
            {
                var rest = line[5..];
                var name = rest[..rest.IndexOf(' ')];
                var cond = rest[(name.Length + 1)..];
                var fn = g.Nodes.FirstOrDefault(n => n.Kind == "function" && n.Name == name);
                if (fn == null || !Tests(fn.Flow).Any(t => t.label == cond && t.test is { Op: not "unknown" })) fails.Add("test: " + line);
            }
            else
            {
                var arrow = line.Split(" -> ");
                var tail = arrow[1].Split(' ');
                var want = (arrow[0], string.Join(' ', tail[..^2]), tail[^2], tail[^1]);
                if (!edges.Contains(want)) fails.Add("missing: " + line);
            }
        }
        Assert.True(fails.Count == 0, string.Join("\n", fails));
    }

    static IEnumerable<(string label, Test? test)> Tests(List<Step>? steps)
    {
        foreach (var s in steps ?? new())
        {
            if (s.K == "if") yield return (s.Cond!, s.Test);
            foreach (var c in s.Cases ?? new()) yield return (c.Label, c.Test);
            foreach (var x in Tests(s.Then).Concat(Tests(s.Else)).Concat(Tests(s.Body)).Concat((s.Cases ?? new()).SelectMany(c => Tests(c.Body)))) yield return x;
        }
    }

    // GRAPH_SCHEMA §7.3: every calls edge has a flow step at its site, and every step has an edge.
    [Theory]
    [InlineData("asmx-legacy")]
    public void FlowSitesMatchCallEdges(string fixture)
    {
        var g = Analyze(fixture);
        foreach (var n in g.Nodes.Where(n => n.Kind == "function"))
        {
            var steps = new HashSet<string>();
            void Walk(List<Step>? ss)
            {
                foreach (var s in ss ?? new())
                {
                    if (s.K == "call") steps.Add(s.Site!);
                    Walk(s.Then); Walk(s.Else); Walk(s.Body);
                    foreach (var c in s.Cases ?? new()) Walk(c.Body);
                }
            }
            Walk(n.Flow);
            var sites = g.Edges.Where(e => e.Source == n.Id && e.Kind == "calls").Select(e => e.SiteId!).ToHashSet();
            Assert.Equal(sites.OrderBy(x => x), steps.OrderBy(x => x));
        }
    }

    [Fact]
    public void FixtureCompilesCleanAndOutputIsDeterministic()
    {
        var a = Analyze("asmx-legacy");
        Assert.DoesNotContain(a.Diagnostics, d => d.Severity == "error" || d.Code.StartsWith("DOTNET_REF"));
        var b = Analyze("asmx-legacy");
        a.Stats.ElapsedMs = b.Stats.ElapsedMs = 0;
        Assert.Equal(GraphJson.Serialize(a), GraphJson.Serialize(b));
    }
}
