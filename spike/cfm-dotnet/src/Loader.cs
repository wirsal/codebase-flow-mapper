// Loads a .NET Framework or SDK-style C# project into one Roslyn compilation without MSBuild:
// no design-time build, no source generators, no NuGet restore of the target. Only .cs text and
// DLL metadata are read, so nothing from the analyzed project ever runs (ADR-105 for .NET).
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CfmDotnet;

public sealed class Workspace
{
    public string Root = "";
    public string Module = "";
    public string TargetFramework = "";
    public List<SyntaxTree> Trees = new();
    public CSharpCompilation Compilation = null!;
    public List<Diag> Diagnostics = new();
    public List<string> Asmx = new(), Ashx = new();
    public string? GlobalAsax;
    // Type that holds the Global.asax event handlers: Inherits="…" or the synthetic Global_asax.
    public string? GlobalType;

    public string Rel(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}

public static partial class Loader
{
    // Directories that never hold the code that runs. Backup/ is what a Visual Studio project upgrade
    // leaves behind: an older copy of the same classes, which would otherwise be defined twice.
    static readonly string[] SkipDirs = { "bin", "obj", "backup", "packages", ".vs", ".git", "node_modules", "testresults", "_upgradereport_files" };
    // Same intent as the Go analyzer's always-excluded packages: fakes and mocks never become call targets.
    static readonly string[] SkipGlobs = { "*fakes", "*mocks", "mock*", "testutil*", "*.tests", "*.unittests" };

    // Namespaces ASP.NET imports into every .asax/.ashx/.asmx code block.
    const string AspNetUsings =
        "using System; using System.Collections; using System.Collections.Specialized; using System.Configuration; " +
        "using System.Text; using System.Text.RegularExpressions; using System.Web; using System.Web.Caching; " +
        "using System.Web.SessionState; using System.Web.Security; using System.Web.Profile; using System.Web.UI; " +
        "using System.Web.UI.WebControls; using System.Web.UI.HtmlControls;";

    static readonly CSharpParseOptions Parse = new(LanguageVersion.Latest, DocumentationMode.None);

    public static Workspace Load(string dir, IReadOnlyList<string> excludes, string? refsDir)
    {
        var ws = new Workspace { Root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) };
        ws.Module = ModuleName(ws.Root);

        var cs = new List<string>();
        var projects = new List<string>();
        var dlls = new List<string>();
        Walk(ws, ws.Root, excludes, cs, projects, dlls);
        cs = FilterByProjects(cs, projects);

        foreach (var f in cs.OrderBy(x => x, StringComparer.Ordinal))
        {
            ws.Trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f), Encoding.UTF8), Parse, f));
        }
        foreach (var f in ws.Asmx.Concat(ws.Ashx)) AddInlineCode(ws, f);
        if (ws.GlobalAsax != null) AddGlobalAsax(ws, ws.GlobalAsax);

        ws.TargetFramework = DetectFramework(ws.Root, projects);
        var refs = References(ws, refsDir, dlls, projects);
        ws.Compilation = CSharpCompilation.Create(
            "cfm-target", ws.Trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
        CompileDiagnostics(ws);
        return ws;
    }

    static void Walk(Workspace ws, string dir, IReadOnlyList<string> excludes, List<string> cs, List<string> projects, List<string> dlls)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            switch (ext)
            {
                case ".cs": cs.Add(f); break;
                case ".csproj": projects.Add(f); break;
                case ".asmx": ws.Asmx.Add(f); break;
                case ".ashx": ws.Ashx.Add(f); break;
                case ".asax": if (Path.GetFileName(f).Equals("Global.asax", StringComparison.OrdinalIgnoreCase)) ws.GlobalAsax ??= f; break;
                case ".vbproj":
                    ws.Diagnostics.Add(new Diag { Severity = "warning", Code = "DOTNET_LANGUAGE_UNSUPPORTED", Message = "VB.NET project skipped; only C# is read", Loc = new Loc { File = ws.Rel(f), Line = 1 } });
                    break;
            }
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                // Deployed dependencies: read as metadata only. Subfolders hold satellites and roslyn.
                dlls.AddRange(Directory.EnumerateFiles(d, "*.dll"));
                continue;
            }
            if (SkipDirs.Contains(name.ToLowerInvariant())) continue;
            if (SkipGlobs.Any(g => Glob(g, name))) continue;
            var rel = ws.Rel(d);
            if (excludes.Any(g => Glob(g, name) || Glob(g, rel))) continue;
            Walk(ws, d, excludes, cs, projects, dlls);
        }
    }

    static bool Glob(string pattern, string text) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, text, ignoreCase: true);

    // An old-style .csproj lists its sources in <Compile Include>; files on disk it doesn't list are
    // not part of the build (stale copies, drafts). SDK-style projects compile every .cs under them.
    static List<string> FilterByProjects(List<string> cs, List<string> projects)
    {
        var listed = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var p in projects)
        {
            var dir = Path.GetDirectoryName(p)!;
            HashSet<string>? set = null;
            try
            {
                var doc = XDocument.Load(p);
                var compile = doc.Descendants().Where(e => e.Name.LocalName == "Compile").Select(e => (string?)e.Attribute("Include")).Where(x => x != null).ToList();
                if (compile.Count > 0 && !compile.Any(c => c!.Contains('*')))
                {
                    set = new HashSet<string>(compile.Select(c => Path.GetFullPath(Path.Combine(dir, c!.Replace('\\', Path.DirectorySeparatorChar)))), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (System.Xml.XmlException) { }
            listed[dir] = set;
        }
        return cs.Where(f =>
        {
            for (var d = Path.GetDirectoryName(f); d != null; d = Path.GetDirectoryName(d))
            {
                if (listed.TryGetValue(d, out var set)) return set == null || set.Contains(f);
            }
            return true;
        }).ToList();
    }

    // .asmx/.ashx may carry their code inline after the directive.
    static void AddInlineCode(Workspace ws, string file)
    {
        var text = File.ReadAllText(file);
        var m = DirectiveRx().Match(text);
        if (!m.Success) return;
        var code = text[(m.Index + m.Length)..];
        if (string.IsNullOrWhiteSpace(code)) return;
        if (!IsCSharp(m.Value))
        {
            ws.Diagnostics.Add(new Diag { Severity = "warning", Code = "DOTNET_LANGUAGE_UNSUPPORTED", Message = "inline code is not C#; skipped", Loc = new Loc { File = ws.Rel(file), Line = 1 } });
            return;
        }
        var line = LineOf(text, m.Index + m.Length);
        // #line keeps every location pointing at the real file and line.
        var src = AspNetUsings + "\n#line " + line + "\n" + code;
        ws.Trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(src, Encoding.UTF8), Parse, file));
    }

    // Global.asax: event handlers live in <script runat="server">, or in the code-behind class it Inherits.
    static void AddGlobalAsax(Workspace ws, string file)
    {
        var text = File.ReadAllText(file);
        var d = DirectiveRx().Match(text);
        var inherits = d.Success ? Attr(d.Value, "Inherits") : null;
        var script = ScriptRx().Match(text);
        if (!script.Success)
        {
            ws.GlobalType = inherits;
            return;
        }
        if (d.Success && !IsCSharp(d.Value)) return;
        ws.GlobalType = "Global_asax";
        var body = script.Groups["code"];
        var line = LineOf(text, body.Index);
        var baseType = inherits ?? "System.Web.HttpApplication";
        var src = AspNetUsings + "\npublic partial class Global_asax : " + baseType + " {\n#line " + line + "\n" + body.Value + "\n}\n";
        ws.Trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(src, Encoding.UTF8), Parse, file));
    }

    public static string? Attr(string directive, string name)
    {
        var m = Regex.Match(directive, @"\b" + name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static bool IsCSharp(string directive)
    {
        var lang = Attr(directive, "Language");
        return lang == null || lang.Equals("C#", StringComparison.OrdinalIgnoreCase) || lang.Equals("cs", StringComparison.OrdinalIgnoreCase);
    }

    static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(@"<%@\s*(WebService|WebHandler|Application)\b[^%]*%>", RegexOptions.IgnoreCase)]
    public static partial Regex DirectiveRx();

    [GeneratedRegex(@"<script[^>]*runat\s*=\s*""?server""?[^>]*>(?<code>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRx();

    static string DetectFramework(string root, List<string> projects)
    {
        foreach (var cfg in new[] { "Web.config", "web.config", "App.config" })
        {
            var p = Path.Combine(root, cfg);
            if (!File.Exists(p)) continue;
            var m = Regex.Match(File.ReadAllText(p), @"targetFramework\s*=\s*""([0-9.]+)""");
            if (m.Success) return "net" + m.Groups[1].Value.Replace(".", "");
        }
        foreach (var p in projects)
        {
            var text = File.ReadAllText(p);
            var m = Regex.Match(text, @"<TargetFrameworkVersion>v([0-9.]+)</TargetFrameworkVersion>");
            if (m.Success) return "net" + m.Groups[1].Value.Replace(".", "");
            m = Regex.Match(text, @"<TargetFrameworks?>([^<;]+)");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return "";
    }

    static bool IsNetFramework(string tf) => Regex.IsMatch(tf, @"^net4\d*$");

    static List<MetadataReference> References(Workspace ws, string? refsDir, List<string> dlls, List<string> projects)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!seen.Add(Path.GetFileName(path)) || !HasMetadata(path)) return;
            refs.Add(MetadataReference.CreateFromFile(path));
        }

        var fw = refsDir ?? (IsNetFramework(ws.TargetFramework) ? FrameworkRefs(ws.TargetFramework) : null);
        if (fw != null)
        {
            foreach (var f in Directory.EnumerateFiles(fw, "*.dll")) Add(f);
            var facades = Path.Combine(fw, "Facades");
            if (Directory.Exists(facades)) foreach (var f in Directory.EnumerateFiles(facades, "*.dll")) Add(f);
        }
        else
        {
            if (IsNetFramework(ws.TargetFramework) || ws.TargetFramework == "")
            {
                ws.Diagnostics.Add(new Diag
                {
                    Severity = "error", Code = "DOTNET_REFS_MISSING",
                    Message = $"reference assemblies for {(ws.TargetFramework == "" ? ".NET Framework" : ws.TargetFramework)} not found in the NuGet cache; framework calls show as unresolved",
                    Details = new() { ["fix"] = "dotnet restore spike/cfm-dotnet/src" },
                });
            }
            // ponytail: SDK-style targets get the analyzer's own runtime, which is close enough for System.*
            // but has no ASP.NET Core; a full analyzer would read the target's reference pack.
            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var f in tpa) Add(f);
        }

        // The project's own output in bin/ would define every source type a second time.
        var own = new HashSet<string>(projects.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
        foreach (var p in projects)
        {
            var m = Regex.Match(File.ReadAllText(p), @"<AssemblyName>([^<]+)</AssemblyName>");
            if (m.Success) own.Add(m.Groups[1].Value);
        }
        foreach (var d in dlls.OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(d);
            if (own.Contains(name) || name.StartsWith("App_Web_", StringComparison.OrdinalIgnoreCase) || name.Equals("App_Code", StringComparison.OrdinalIgnoreCase)) continue;
            Add(d);
        }
        return refs;
    }

    // Reference assemblies from the NuGet cache: the exact version, else the nearest newer one.
    static string? FrameworkRefs(string tf)
    {
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(cache)) return null;
        var found = new List<(int ver, string dir)>();
        foreach (var pkg in Directory.EnumerateDirectories(cache, "microsoft.netframework.referenceassemblies.net4*"))
        {
            var ver = Path.GetFileName(pkg)["microsoft.netframework.referenceassemblies.net".Length..];
            foreach (var v in Directory.EnumerateDirectories(pkg))
            {
                var build = Path.Combine(v, "build", ".NETFramework");
                if (!Directory.Exists(build)) continue;
                foreach (var d in Directory.EnumerateDirectories(build, "v4*"))
                {
                    if (File.Exists(Path.Combine(d, "mscorlib.dll")) && int.TryParse(ver.PadRight(3, '0'), out var n)) found.Add((n, d));
                }
            }
        }
        if (!int.TryParse(tf[3..].PadRight(3, '0'), out var want)) return null;
        var pick = found.Where(f => f.ver >= want).OrderBy(f => f.ver).Concat(found.OrderByDescending(f => f.ver)).FirstOrDefault();
        return pick.dir;
    }

    static bool HasMetadata(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            return pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    // Compiler errors make the graph partial, like GO_TYPE_ERROR. A missing type is reported once
    // per name, since one absent DLL produces hundreds of identical errors.
    static void CompileDiagnostics(Workspace ws)
    {
        var errors = ws.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var missing = new SortedDictionary<string, Microsoft.CodeAnalysis.Diagnostic>(StringComparer.Ordinal);
        var other = new List<Microsoft.CodeAnalysis.Diagnostic>();
        foreach (var e in errors)
        {
            if (e.Id is "CS0246" or "CS0234" or "CS0012")
            {
                var name = Regex.Match(e.GetMessage(), "'([^']+)'").Groups[1].Value;
                if (!missing.ContainsKey(name)) missing[name] = e;
            }
            else other.Add(e);
        }
        foreach (var (name, e) in missing)
        {
            ws.Diagnostics.Add(new Diag
            {
                Severity = "warning", Code = "DOTNET_REF_MISSING",
                Message = Trunc($"type or namespace {name} not found: its calls show as unresolved"),
                Loc = LocOf(ws, e.Location),
                Details = new() { ["fix"] = "copy the dependency DLL into bin/, or restore the project's packages" },
            });
        }
        const int cap = 50;
        foreach (var e in other.OrderBy(e => e.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(e => e.Location.SourceSpan.Start).Take(cap))
        {
            ws.Diagnostics.Add(new Diag { Severity = "error", Code = "DOTNET_TYPE_ERROR", Message = Trunc(e.Id + ": " + e.GetMessage()), Loc = LocOf(ws, e.Location) });
        }
        if (other.Count > cap)
        {
            ws.Diagnostics.Add(new Diag { Severity = "info", Code = "DOTNET_TYPE_ERROR", Message = $"{other.Count - cap} more compiler errors not listed" });
        }
    }

    public static Loc? LocOf(Workspace ws, Location l, bool withEnd = false)
    {
        if (!l.IsInSource) return null;
        var span = l.GetMappedLineSpan();
        var path = span.HasMappedPath ? span.Path : l.SourceTree!.FilePath;
        return new Loc
        {
            File = ws.Rel(path), Line = span.StartLinePosition.Line + 1, Col = span.StartLinePosition.Character + 1,
            EndLine = withEnd ? span.EndLinePosition.Line + 1 : 0,
        };
    }

    public const int MaxText = 120;

    // Source text in the report is capped at 120 runes, like the Go analyzer (PRD NFR-4).
    public static string Trunc(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        var runes = s.EnumerateRunes().ToList();
        return runes.Count <= MaxText ? s : string.Concat(runes.Take(MaxText - 1).Select(r => r.ToString())) + "…";
    }

    static string ModuleName(string root)
    {
        var sln = Directory.EnumerateFiles(root, "*.sln").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        return sln != null ? Path.GetFileNameWithoutExtension(sln) : Path.GetFileName(root);
    }
}
