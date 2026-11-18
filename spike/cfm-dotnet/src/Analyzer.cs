// Call graph over one Roslyn compilation (ARCHITECTURE §4.1 rules, adapted to C#).
//
// Resolution, in order:
//   1. A non-virtual call, or a call on base., is `static`/`high`.
//   2. A call through an interface or a virtual member first looks at the receiver: a field, local or
//      property that is only ever assigned `new T()` reaches exactly those T (`vta`, `medium`).
//   3. Else Rapid Type Analysis: implementations whose type is created with `new` somewhere in the
//      analyzed code (`vta`, `medium` for one, `low` for several).
//   4. Else the whole class hierarchy (`vta`, `low`): the type may come from reflection or a container.
// ponytail: `vta` stands in for `cha`/`rta`, which the schema doesn't have yet (docs/validation.md).
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CfmDotnet;

public sealed partial class Analyzer
{
    readonly Workspace ws;
    readonly CSharpCompilation comp;
    readonly HashSet<SyntaxTree> own;
    readonly Dictionary<SyntaxTree, SemanticModel> models = new();
    readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Edge> edges = new(StringComparer.Ordinal);
    readonly List<Diag> diags = new();
    readonly Dictionary<string, IMethodSymbol> fnDecls = new(StringComparer.Ordinal);

    readonly List<INamedTypeSymbol> sourceTypes = new();
    readonly HashSet<INamedTypeSymbol> created = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ISymbol, List<(INamedTypeSymbol type, Location at)>> assignedNew = new(SymbolEqualityComparer.Default);
    readonly HashSet<ISymbol> assignedOther = new(SymbolEqualityComparer.Default);

    public Analyzer(Workspace ws)
    {
        this.ws = ws;
        comp = ws.Compilation;
        own = new HashSet<SyntaxTree>(ws.Trees);
    }

    public Graph Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        diags.AddRange(ws.Diagnostics);
        CollectTypes();
        CollectCreations();
        Entries();
        foreach (var t in sourceTypes)
        {
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                if (BodyOf(m) != null) AddFunction(m);
            }
        }
        // Outlines can discover local functions, which add more functions to outline.
        var done = new HashSet<string>(StringComparer.Ordinal);
        while (fnDecls.Keys.Except(done).OrderBy(x => x, StringComparer.Ordinal).ToList() is { Count: > 0 } todo)
        {
            foreach (var id in todo)
            {
                done.Add(id);
                nodes[id].Flow = OutlineFunction(id, fnDecls[id]) is { Count: > 0 } f ? f : null;
            }
        }
        return Assemble(clock.ElapsedMilliseconds);
    }

    SemanticModel Model(SyntaxTree t)
    {
        if (!models.TryGetValue(t, out var m)) models[t] = m = comp.GetSemanticModel(t);
        return m;
    }

    bool IsOwn(ISymbol s) => s.DeclaringSyntaxReferences.Any(r => own.Contains(r.SyntaxTree));

    void CollectTypes()
    {
        foreach (var tree in ws.Trees)
        {
            var model = Model(tree);
            foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is INamedTypeSymbol t && !sourceTypes.Contains(t, SymbolEqualityComparer.Default)) sourceTypes.Add(t);
            }
        }
    }

    // Which types are created with `new`, and which variables only ever hold such a creation.
    void CollectCreations()
    {
        foreach (var tree in ws.Trees)
        {
            var model = Model(tree);
            foreach (var n in tree.GetRoot().DescendantNodes())
            {
                switch (n)
                {
                    case BaseObjectCreationExpressionSyntax oc when model.GetTypeInfo(oc).Type is INamedTypeSymbol t:
                        created.Add(t.OriginalDefinition);
                        break;
                    case VariableDeclaratorSyntax v when v.Initializer != null && model.GetDeclaredSymbol(v) is { } sym:
                        Assigned(sym, v.Initializer.Value, model);
                        break;
                    case PropertyDeclarationSyntax p when p.Initializer != null && model.GetDeclaredSymbol(p) is { } sym:
                        Assigned(sym, p.Initializer.Value, model);
                        break;
                    case AssignmentExpressionSyntax a:
                        if (model.GetSymbolInfo(a.Left).Symbol is { } target and (IFieldSymbol or ILocalSymbol or IPropertySymbol))
                        {
                            if (a.IsKind(SyntaxKind.SimpleAssignmentExpression)) Assigned(target, a.Right, model);
                            else assignedOther.Add(target.OriginalDefinition);
                        }
                        break;
                    case ArgumentSyntax arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None):
                        if (model.GetSymbolInfo(arg.Expression).Symbol is { } refd) assignedOther.Add(refd.OriginalDefinition);
                        break;
                }
            }
        }
    }

    void Assigned(ISymbol sym, ExpressionSyntax value, SemanticModel model)
    {
        sym = sym.OriginalDefinition;
        value = Strip(value);
        if (value.IsKind(SyntaxKind.NullLiteralExpression) || value.IsKind(SyntaxKind.DefaultLiteralExpression)) return;
        if (value is BaseObjectCreationExpressionSyntax oc && model.GetTypeInfo(oc).Type is INamedTypeSymbol t && t.TypeKind != TypeKind.Error)
        {
            if (!assignedNew.TryGetValue(sym, out var list)) assignedNew[sym] = list = new();
            list.Add((t.OriginalDefinition, oc.GetLocation()));
            return;
        }
        assignedOther.Add(sym);
    }

    static ExpressionSyntax Strip(ExpressionSyntax e)
    {
        while (true)
        {
            switch (e)
            {
                case ParenthesizedExpressionSyntax p: e = p.Expression; break;
                case CastExpressionSyntax c: e = c.Expression; break;
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression): e = b.Left; break;
                default: return e;
            }
        }
    }

    // ---- IDs and nodes (GRAPH_SCHEMA §6, with parameter types because C# has overloads) ----

    public static string TypeDisplay(INamedTypeSymbol t) =>
        (t.ContainingType != null ? TypeDisplay(t.ContainingType) + "." : "") + t.Name +
        (t.TypeParameters.Length > 0 ? "<" + string.Join(",", t.TypeParameters.Select(p => p.Name)) + ">" : "");

    static string Ns(INamedTypeSymbol t) => t.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "";

    public static string QualifiedType(INamedTypeSymbol t)
    {
        var ns = Ns(t.OriginalDefinition);
        return (ns == "" ? "" : ns + ".") + TypeDisplay(t.OriginalDefinition);
    }

    static string MemberName(IMethodSymbol m) => m.MethodKind switch
    {
        MethodKind.Constructor => ".ctor",
        MethodKind.StaticConstructor => ".cctor",
        _ => m.Name,
    };

    string FnId(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        if (m.MethodKind == MethodKind.LocalFunction && m.ContainingSymbol is IMethodSymbol parent) return FnId(parent) + "$" + m.Name;
        var t = m.ContainingType;
        var ns = Ns(t);
        var ps = string.Join(",", m.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return "fn:" + (ns == "" ? "" : ns + ".") + "(" + TypeDisplay(t) + ")." + MemberName(m) + "(" + ps + ")";
    }

    string FnName(IMethodSymbol m)
    {
        m = m.OriginalDefinition;
        if (m.MethodKind == MethodKind.LocalFunction && m.ContainingSymbol is IMethodSymbol parent) return FnName(parent) + "$" + m.Name;
        return "(" + TypeDisplay(m.ContainingType) + ")." + MemberName(m);
    }

    // The package of a function: its namespace, or its folder when the code is in the global
    // namespace (App_Code in a Web Site project usually is).
    string PkgOf(IMethodSymbol m, SyntaxNode decl)
    {
        var ns = Ns(m.ContainingType);
        if (ns != "") return ns;
        var dir = Path.GetDirectoryName(Loader.LocOf(ws, decl.GetLocation())?.File ?? "")?.Replace('\\', '/');
        return string.IsNullOrEmpty(dir) ? "." : dir;
    }

    // The syntax that holds a method's executable body, or null for abstract/extern/partial stubs
    // and compiler-made members.
    static SyntaxNode? BodyOf(IMethodSymbol m)
    {
        if (m.PartialImplementationPart is { } impl) m = impl;
        if (m.IsImplicitlyDeclared || m.IsAbstract || m.IsExtern) return null;
        if (m.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.LocalFunction or MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.ExplicitInterfaceImplementation)) return null;
        foreach (var r in m.DeclaringSyntaxReferences)
        {
            var s = r.GetSyntax();
            if (s is BaseMethodDeclarationSyntax b && (b.Body != null || b.ExpressionBody != null)) return s;
            if (s is LocalFunctionStatementSyntax l && (l.Body != null || l.ExpressionBody != null)) return s;
        }
        return null;
    }

    string AddFunction(IMethodSymbol m)
    {
        m = m.PartialImplementationPart ?? m;
        m = m.OriginalDefinition;
        var id = FnId(m);
        if (nodes.ContainsKey(id)) return id;
        var decl = BodyOf(m)!;
        var ident = decl switch
        {
            MethodDeclarationSyntax md => md.Identifier.GetLocation(),
            ConstructorDeclarationSyntax cd => cd.Identifier.GetLocation(),
            LocalFunctionStatementSyntax lf => lf.Identifier.GetLocation(),
            _ => decl.GetLocation(),
        };
        var loc = Loader.LocOf(ws, ident)!;
        loc.EndLine = Loader.LocOf(ws, decl.GetLocation(), withEnd: true)!.EndLine;
        nodes[id] = new Node
        {
            Id = id, Kind = "function", Name = FnName(m), Pkg = PkgOf(m, decl), Loc = loc, Status = "resolved",
            Receiver = m.MethodKind == MethodKind.LocalFunction ? null : TypeDisplay(m.ContainingType),
            Signature = Loader.Trunc(m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)),
        };
        fnDecls[id] = m;
        return id;
    }

    string External(IMethodSymbol m)
    {
        m = (m.ReducedFrom ?? m).OriginalDefinition;
        var t = m.ContainingType;
        var ns = Ns(t);
        var name = "(" + TypeDisplay(t) + ")." + MemberName(m);
        var id = "external:" + (ns == "" ? "" : ns + ".") + name;
        if (!nodes.ContainsKey(id))
        {
            nodes[id] = new Node { Id = id, Kind = "external", Name = name, Pkg = ns == "" ? null : ns, Status = "resolved", Category = Category(ns + "." + TypeDisplay(t)) };
        }
        return id;
    }

    // ARCHITECTURE §4.1 category table, with .NET prefixes.
    static readonly (string prefix, string cat)[] Categories =
    {
        ("System.Data.", "db"), ("Microsoft.Data.", "db"), ("Oracle.", "db"), ("Npgsql.", "db"), ("MySql.", "db"),
        ("Dapper.", "db"), ("Microsoft.EntityFrameworkCore.", "db"), ("System.Data.Entity.", "db"),
        ("System.Net.Http.", "http_client"), ("System.Net.WebRequest", "http_client"), ("System.Net.HttpWebRequest", "http_client"),
        ("System.Net.WebResponse", "http_client"), ("System.Net.HttpWebResponse", "http_client"), ("System.Net.WebClient", "http_client"),
        ("RestSharp.", "http_client"), ("System.Web.Services.Protocols.", "http_client"), ("System.ServiceModel.", "http_client"),
        ("StackExchange.Redis.", "cache"), ("System.Runtime.Caching.", "cache"), ("System.Web.Caching.", "cache"), ("Microsoft.Extensions.Caching.", "cache"),
        ("RabbitMQ.", "queue"), ("Confluent.Kafka.", "queue"), ("MassTransit.", "queue"), ("Azure.Messaging.", "queue"), ("System.Messaging.", "queue"),
        ("log4net.", "log"), ("NLog.", "log"), ("Serilog.", "log"), ("Microsoft.Extensions.Logging.", "log"),
        ("System.Diagnostics.Debug", "log"), ("System.Diagnostics.Trace", "log"), ("System.Console", "log"),
    };

    static string Category(string qualified) =>
        Categories.FirstOrDefault(c => qualified.StartsWith(c.prefix, StringComparison.Ordinal)).cat ?? "other";

    string Unresolved(Loc site, string text, string reason)
    {
        var id = $"unresolved:{site.File}:{site.Line}:{site.Col}";
        if (!nodes.ContainsKey(id))
        {
            nodes[id] = new Node { Id = id, Kind = "unresolved", Name = Loader.Trunc(text), Loc = site, Status = "unresolved", Reason = reason };
            diags.Add(new Diag { Severity = "info", Code = "CALL_UNRESOLVED", Message = Loader.Trunc($"{reason}: {text}"), Loc = site, NodeId = id });
        }
        return id;
    }

    void AddEdge(Edge e)
    {
        var key = $"{e.Source}|{e.Target}|{e.Kind}|{e.SiteId}|{e.Order}";
        edges.TryAdd(key, e);
    }

    // ---- call resolution ----

    public sealed record Target(string To, string Method, string Confidence, string Reason, int Alternatives = 0);

    // Resolves a call (or a method group used as a handler) to its targets. `receiver` is the
    // expression left of the dot, or null for an unqualified call on this.
    List<Target> Resolve(IMethodSymbol? m, ExpressionSyntax? receiver, SemanticModel model, Loc site, string text)
    {
        if (m == null) return new() { new(Unresolved(site, text, "callee type unknown"), "unresolved", "unknown", "the compiler could not bind this call (missing reference or type error)") };
        m = m.ReducedFrom ?? m;
        if (m.MethodKind == MethodKind.DelegateInvoke)
        {
            return new() { new(Unresolved(site, text, "func_value_unknown"), "unresolved", "unknown", $"call through a {m.ContainingType.Name} value; the target is chosen at runtime") };
        }
        var def = m.OriginalDefinition;
        if (def.PartialDefinitionPart == null && def.PartialImplementationPart is { } impl) def = impl;
        if (!IsOwn(def)) return new() { new(External(def), "static", "high", $"call into {Ns(def.ContainingType).DefaultIfEmpty("global")}") };

        var dispatch = def.ContainingType.TypeKind == TypeKind.Interface || def.IsAbstract || def.IsVirtual || def.IsOverride;
        if (!dispatch || receiver is BaseExpressionSyntax) return Static(def, site, text);
        return Dispatch(def, receiver, model, site, text);
    }

    List<Target> Static(IMethodSymbol def, Loc site, string text)
    {
        if (BodyOf(def) == null) return new() { new(Unresolved(site, text, "no_concrete_type"), "unresolved", "unknown", "the callee has no body in the analyzed code") };
        return new() { new(AddFunction(def), "static", "high", "static call") };
    }

    List<Target> Dispatch(IMethodSymbol def, ExpressionSyntax? receiver, SemanticModel model, Loc site, string text)
    {
        var owner = def.ContainingType.Name + "." + def.Name;
        var impls = sourceTypes.Where(t => t.TypeKind is TypeKind.Class or TypeKind.Struct && !t.IsAbstract)
            .Select(t => (type: t, impl: ImplFor(t, def))).Where(x => x.impl != null).ToList();
        var distinct = impls.Select(x => x.impl!).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
        if (distinct.Count == 0) return new() { new(Unresolved(site, text, "no_concrete_type"), "unresolved", "unknown", $"no implementation of {owner} in the analyzed code") };
        if (distinct.Count == 1 && SymbolEqualityComparer.Default.Equals(distinct[0], def)) return Static(def, site, text);

        // 2. the receiver only ever holds `new T()`
        var sym = receiver == null ? null : model.GetSymbolInfo(Strip(receiver)).Symbol?.OriginalDefinition;
        if (sym is IFieldSymbol or ILocalSymbol or IPropertySymbol && !assignedOther.Contains(sym) && assignedNew.TryGetValue(sym, out var news))
        {
            var hits = news.Select(n => (n.at, impl: ImplFor(n.type, def))).Where(x => x.impl != null).ToList();
            var targets = hits.Select(h => h.impl!).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
            if (targets.Count > 0)
            {
                var at = Loader.LocOf(ws, hits[0].at)!;
                var kind = sym switch { IFieldSymbol => "field", ILocalSymbol => "local", _ => "property" };
                var what = string.Join(", ", news.Select(n => "new " + n.type.Name + "()").Distinct());
                return Many(targets, $"interface call {owner}: {kind} {sym.Name} is only assigned {what} ({at.File}:{at.Line})");
            }
        }

        // 3. rapid type analysis
        var live = impls.Where(x => created.Contains(x.type.OriginalDefinition)).Select(x => x.impl!).Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToList();
        if (live.Count > 0)
        {
            return Many(live, $"class hierarchy: {live.Count} of {distinct.Count} implementation(s) of {owner} belong to types created with new");
        }

        // 4. class hierarchy
        return distinct.Select(i => new Target(AddFunction(i), "vta", "low",
            $"class hierarchy: no implementation of {owner} is created with new in the analyzed code (reflection or a container?)",
            distinct.Count)).ToList();
    }

    List<Target> Many(List<IMethodSymbol> targets, string reason)
    {
        var conf = targets.Count == 1 ? "medium" : "low";
        return targets.Select(t => new Target(AddFunction(t), "vta", conf, reason, conf == "low" ? targets.Count : 0)).ToList();
    }

    // The method that runs for def on an instance of t, if t has one in the analyzed code.
    IMethodSymbol? ImplFor(INamedTypeSymbol t, IMethodSymbol def)
    {
        IMethodSymbol? found = null;
        if (def.ContainingType.TypeKind == TypeKind.Interface)
        {
            foreach (var iface in t.AllInterfaces.Where(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, def.ContainingType.OriginalDefinition)))
            {
                var member = iface.GetMembers(def.Name).OfType<IMethodSymbol>().FirstOrDefault(x => SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, def));
                if (member != null && t.FindImplementationForInterfaceMember(member) is IMethodSymbol im) { found = im; break; }
            }
        }
        else
        {
            for (var b = t; b != null && found == null; b = b.BaseType)
            {
                foreach (var m in b.GetMembers(def.Name).OfType<IMethodSymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, def) || Overrides(m, def)) { found = m; break; }
                }
            }
        }
        if (found == null) return null;
        found = found.OriginalDefinition;
        return IsOwn(found) && BodyOf(found) != null ? found : null;
    }

    static bool Overrides(IMethodSymbol m, IMethodSymbol def)
    {
        for (var o = m.OverriddenMethod; o != null; o = o.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(o.OriginalDefinition, def)) return true;
        }
        return false;
    }

    // ---- output ----

    Graph Assemble(long elapsed)
    {
        var g = new Graph { Module = ws.Module, GoVersion = ws.TargetFramework == "" ? "dotnet" : "dotnet " + ws.TargetFramework };
        g.Nodes = nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        g.Edges = edges.Values.OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal)
            .ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.SiteId ?? "", StringComparer.Ordinal).ThenBy(e => e.Order ?? -1).ToList();
        g.Diagnostics = diags.OrderBy(d => d.Loc?.File ?? "", StringComparer.Ordinal).ThenBy(d => d.Loc?.Line ?? 0).ThenBy(d => d.Loc?.Col ?? 0)
            .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToList();
        for (var i = 0; i < g.Diagnostics.Count; i++)
        {
            if (g.Diagnostics[i].Code == "DOTNET_REFS_MISSING") g.Stats.Blockers.Add(i);
        }
        var fns = g.Nodes.Where(n => n.Kind == "function").ToList();
        g.Stats.Packages = fns.Select(n => n.Pkg).Distinct().Count();
        g.Stats.Functions = fns.Count;
        g.Stats.Endpoints = g.Nodes.Count(n => n.Kind == "endpoint");
        g.Stats.Edges = g.Edges.Count;
        g.Stats.Unresolved = g.Nodes.Count(n => n.Kind == "unresolved");
        g.Stats.ElapsedMs = elapsed;
        return g;
    }
}

static class StringExt
{
    public static string DefaultIfEmpty(this string s, string d) => s == "" ? d : s;
}
