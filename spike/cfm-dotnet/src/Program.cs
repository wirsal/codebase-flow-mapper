// cfm-dotnet: throwaway spike for CFM-503 (ADR-108). Reads a C# project with Roslyn and writes a
// GRAPH_SCHEMA 2.x graph for `cfm render`. It never builds or runs the target: no MSBuild, no
// source generators, no restore; only .cs text and DLL metadata are read.
//
// usage: cfm-dotnet <project dir> [-o graph.json] [--exclude glob]... [--refs <reference assembly dir>]
// exit:  0 graph written, 1 written but partial (compiler errors), 3 usage error
using CfmDotnet;

const string Usage = "usage: cfm-dotnet <project dir> [-o graph.json] [--exclude glob]... [--refs dir]";

string? dir = null, output = null, refs = null;
var excludes = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    try
    {
        switch (args[i])
        {
            case "-o": output = Next(); break;
            case "--exclude": excludes.Add(Next()); break;
            case "--refs": refs = Next(); break;
            default:
                if (args[i].StartsWith('-') || dir != null) throw new ArgumentException($"unexpected argument {args[i]}");
                dir = args[i];
                break;
        }
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine($"cfm-dotnet: {e.Message}\n{Usage}");
        return 3;
    }
}
if (dir == null || !Directory.Exists(dir))
{
    Console.Error.WriteLine(dir == null ? Usage : $"cfm-dotnet: {dir}: not a directory");
    return 3;
}

var ws = Loader.Load(dir, excludes, refs);
var g = new Analyzer(ws).Run();
var json = GraphJson.Serialize(g);
if (output != null) File.WriteAllText(output, json);
else Console.Out.Write(json);

var s = g.Stats;
Console.Error.WriteLine($"cfm-dotnet: {s.Endpoints} endpoints, {s.Functions} functions, {s.Edges} edges, {s.Unresolved} unresolved, {g.Diagnostics.Count} diagnostics in {s.ElapsedMs} ms ({ws.Trees.Count} files, {g.GoVersion})");
foreach (var i in s.Blockers) Console.Error.WriteLine($"cfm-dotnet: blocker {g.Diagnostics[i].Code}: run \"{g.Diagnostics[i].Details!["fix"]}\"");
return g.Diagnostics.Any(d => d.Severity == "error") ? 1 : 0;
