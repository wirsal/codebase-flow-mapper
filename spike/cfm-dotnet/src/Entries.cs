// Entry points of a .NET Framework web app, as `endpoint` nodes (GRAPH_SCHEMA §2):
//   - ASMX: every [WebMethod] of the class an .asmx names           POST /Svc.asmx/Method   high
//   - ASHX: a handler's homegrown route table (routes.Get(path, h))  GET /api/path           medium
//           or, without one, its ProcessRequest                     ANY /Handler.ashx        high
//   - Global.asax: Application_*/Session_* events                   EVENT Application_Start  high
//   - static Main (console apps, jobs)                              MAIN Program.Main        high
// ponytail: WebForms pages, WCF .svc, MVC/Web API controllers and ASP.NET Core are not read yet.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CfmDotnet;

public sealed partial class Analyzer
{
    static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Get"] = "GET", ["Post"] = "POST", ["Put"] = "PUT", ["Patch"] = "PATCH", ["Delete"] = "DELETE",
        ["Head"] = "HEAD", ["Options"] = "OPTIONS", ["Any"] = "ANY", ["All"] = "ANY",
    };

    void Entries()
    {
        foreach (var f in ws.Asmx.OrderBy(x => x, StringComparer.Ordinal)) Asmx(f);
        var mounts = Mounts();
        foreach (var f in ws.Ashx.OrderBy(x => x, StringComparer.Ordinal)) Ashx(f, mounts);
        GlobalEvents();
        Mains();
    }

    string Endpoint(string framework, string verb, string path, Loc loc, string target, string confidence, string reason)
    {
        var id = $"endpoint:{framework}:{verb}:{path}:{loc.File}:{loc.Line}";
        nodes[id] = new Node { Id = id, Kind = "endpoint", Name = $"{verb} {path}", Loc = loc, Status = "resolved", Framework = framework, Method = verb, Path = path };
        AddEdge(new Edge { Source = id, Target = target, Kind = "routes_to", Confidence = confidence, Method = "framework_registration", Reason = Loader.Trunc(reason), Site = loc });
        return id;
    }

    INamedTypeSymbol? TypeNamed(string name, string file, string directive)
    {
        var t = comp.GetTypeByMetadataName(name) ?? sourceTypes.FirstOrDefault(x => x.Name == name || QualifiedType(x) == name);
        if (t == null)
        {
            diags.Add(new Diag { Severity = "warning", Code = "HANDLER_UNRESOLVED", Message = Loader.Trunc($"{directive} names class {name}, which is not in the analyzed code"), Loc = new Loc { File = ws.Rel(file), Line = 1 } });
        }
        return t;
    }

    static string? DirectiveClass(string file)
    {
        var m = Loader.DirectiveRx().Match(File.ReadAllText(file));
        return m.Success ? Loader.Attr(m.Value, "Class") : null;
    }

    void Asmx(string file)
    {
        var cls = DirectiveClass(file);
        if (cls == null || TypeNamed(cls, file, "WebService") is not { } type) return;
        var rel = ws.Rel(file);
        for (var t = type; t != null && IsOwn(t); t = t.BaseType)
        {
            foreach (var m in t.GetMembers().OfType<IMethodSymbol>())
            {
                var attr = m.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "WebMethodAttribute" or "WebMethod");
                if (attr?.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax syn || BodyOf(m) == null) continue;
                var model = Model(syn.SyntaxTree);
                var name = m.Name;
                foreach (var arg in syn.ArgumentList?.Arguments ?? default)
                {
                    if (arg.NameEquals?.Name.Identifier.Text == "MessageName" && model.GetConstantValue(arg.Expression).Value is string s) name = s;
                }
                var loc = Loader.LocOf(ws, syn.GetLocation())!;
                var fn = AddFunction(m);
                Endpoint("asmx", "POST", $"/{rel}/{name}", loc, fn, "high", $"[WebMethod] {TypeDisplay(t)}.{m.Name}, served by {rel}");
                AttachParamsBody(fn, m, "WebMethod");
            }
        }
    }

    // A SOAP call binds every parameter. One class-typed parameter is the body itself; several
    // parameters become a synthetic object with one field each.
    void AttachParamsBody(string fn, IMethodSymbol m, string binder)
    {
        if (m.Parameters.Length == 0) return;
        var line = nodes[fn].Loc!.Line;
        if (m.Parameters.Length == 1 && m.Parameters[0].Type is INamedTypeSymbol t && IsOwn(t) && t.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            nodes[fn].Body = BodyFor(t, binder, line);
        }
        else
        {
            nodes[fn].Body = BodyForParams(m, binder, line);
        }
    }

    // System.Web.Routing mounts: new Route("api/{*pathInfo}", new H()) where H.GetHttpHandler
    // returns new X() mounts handler X at /api. Read from every file, Global.asax included.
    Dictionary<INamedTypeSymbol, List<(string prefix, Loc at)>> Mounts()
    {
        var mounts = new Dictionary<INamedTypeSymbol, List<(string, Loc)>>(SymbolEqualityComparer.Default);
        foreach (var tree in ws.Trees)
        {
            var model = Model(tree);
            foreach (var oc in tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (model.GetTypeInfo(oc).Type is not INamedTypeSymbol rt || QualifiedType(rt) != "System.Web.Routing.Route") continue;
                var args = oc.ArgumentList?.Arguments ?? default;
                if (args.Count < 2 || model.GetConstantValue(args[0].Expression).Value is not string url) continue;
                var handlerType = model.GetTypeInfo(Strip(args[^1].Expression)).Type as INamedTypeSymbol;
                var getHandler = handlerType?.GetMembers("GetHttpHandler").OfType<IMethodSymbol>().FirstOrDefault();
                if (getHandler == null || BodyOf(getHandler) is not { } body) continue;
                var bm = Model(body.SyntaxTree);
                var prefix = "/" + url.Split('{')[0].Trim('/');
                foreach (var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>())
                {
                    if (ret.Expression != null && bm.GetTypeInfo(Strip(ret.Expression)).Type is INamedTypeSymbol x && Strip(ret.Expression) is BaseObjectCreationExpressionSyntax)
                    {
                        if (!mounts.TryGetValue(x.OriginalDefinition, out var list)) mounts[x.OriginalDefinition] = list = new();
                        list.Add((prefix.TrimEnd('/'), Loader.LocOf(ws, oc.GetLocation())!));
                    }
                }
            }
        }
        return mounts;
    }

    void Ashx(string file, Dictionary<INamedTypeSymbol, List<(string prefix, Loc at)>> mounts)
    {
        var cls = DirectiveClass(file);
        if (cls == null || TypeNamed(cls, file, "WebHandler") is not { } type) return;
        var rel = ws.Rel(file);
        var bases = mounts.TryGetValue(type.OriginalDefinition, out var ms) ? ms : new() { ("/" + rel, new Loc()) };
        var found = 0;
        foreach (var decl in type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()))
        {
            var model = Model(decl.SyntaxTree);
            foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // A route-table registration: a project method named like a verb taking (string path, delegate handler).
                if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol reg || !IsOwn(reg) || !Verbs.TryGetValue(reg.Name, out var verb)) continue;
                if (reg.Parameters.Length < 2 || reg.Parameters[0].Type.SpecialType != SpecialType.System_String || reg.Parameters[1].Type.TypeKind != TypeKind.Delegate) continue;
                var args = inv.ArgumentList.Arguments;
                var loc = Loader.LocOf(ws, inv.GetLocation())!;
                found++;
                if (model.GetConstantValue(args[0].Expression).Value is not string sub)
                {
                    diags.Add(new Diag { Severity = "warning", Code = "ROUTE_PATH_DYNAMIC", Message = Loader.Trunc($"route path is not a constant: {args[0]}"), Loc = loc });
                    continue;
                }
                var handler = args[1].Expression;
                var hinfo = model.GetSymbolInfo(handler);
                var hm = hinfo.Symbol as IMethodSymbol ?? (hinfo.CandidateSymbols.Length == 1 ? hinfo.CandidateSymbols[0] as IMethodSymbol : null);
                foreach (var (prefix, at) in bases)
                {
                    var path = prefix + "/" + sub.TrimStart('/');
                    var mount = at.File != "" ? $", mounted at {prefix} ({at.File}:{at.Line})" : "";
                    var reason = $"route table {reg.ContainingType.Name}.{reg.Name}(\"{sub}\", {handler}) in {type.Name}{mount}";
                    if (hm == null || handler is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
                    {
                        var u = Unresolved(Loader.LocOf(ws, handler.GetLocation())!, handler.ToString(), "func_value_unknown");
                        diags.Add(new Diag { Severity = "warning", Code = "HANDLER_UNRESOLVED", Message = Loader.Trunc($"route {verb} {path}: handler is not a named method"), Loc = loc });
                        Endpoint("ashx", verb, path, loc, u, "unknown", reason);
                        continue;
                    }
                    var receiver = handler is MemberAccessExpressionSyntax ma ? ma.Expression : null;
                    var targets = Resolve(hm, receiver, model, loc, handler.ToString());
                    var ep = "";
                    foreach (var t in targets)
                    {
                        ep = Endpoint("ashx", verb, path, loc, t.To, t.Confidence == "high" ? "medium" : t.Confidence, reason + (t.Method == "vta" ? "; " + t.Reason : ""));
                        if (nodes[t.To].Kind == "function") AttachDeserializeBody(t.To);
                    }
                }
            }
        }
        if (found > 0) return;
        var pr = type.GetMembers("ProcessRequest").OfType<IMethodSymbol>().FirstOrDefault(m => BodyOf(m) != null);
        if (pr == null) return;
        var fn = AddFunction(pr);
        foreach (var (prefix, at) in bases)
        {
            var path = at.File != "" ? prefix : "/" + rel;
            Endpoint("ashx", "ANY", path, nodes[fn].Loc!, fn, "high", $"IHttpHandler {type.Name}.ProcessRequest, served by {rel}");
        }
        AttachDeserializeBody(fn);
    }

    // A handler that reads its body itself: the first JSON deserialization into a project type.
    void AttachDeserializeBody(string fn)
    {
        if (nodes[fn].Body != null || BodyOf(fnDecls[fn]) is not { } decl) return;
        var model = Model(decl.SyntaxTree);
        foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol m || m.TypeArguments.Length != 1) continue;
            if (m.Name is not ("Deserialize" or "DeserializeObject")) continue;
            if (m.ContainingType.Name is not ("JavaScriptSerializer" or "JsonConvert" or "JsonSerializer")) continue;
            if (m.TypeArguments[0] is not INamedTypeSymbol t || !IsOwn(t)) continue;
            nodes[fn].Body = BodyFor(t, m.Name, Loader.LocOf(ws, inv.GetLocation())!.Line);
            return;
        }
    }

    void GlobalEvents()
    {
        if (ws.GlobalType == null) return;
        var type = comp.GetTypeByMetadataName(ws.GlobalType) ?? sourceTypes.FirstOrDefault(t => t.Name == ws.GlobalType);
        if (type == null) return;
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>().OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (!(m.Name.StartsWith("Application_") || m.Name.StartsWith("Session_")) || BodyOf(m) == null) continue;
            var fn = AddFunction(m);
            Endpoint("global.asax", "EVENT", m.Name, nodes[fn].Loc!, fn, "high", $"ASP.NET calls {m.Name} by name on the Global.asax class");
        }
    }

    void Mains()
    {
        foreach (var t in sourceTypes)
        {
            foreach (var m in t.GetMembers("Main").OfType<IMethodSymbol>())
            {
                if (!m.IsStatic || BodyOf(m) == null || m.Parameters.Length > 1) continue;
                var fn = AddFunction(m);
                Endpoint("console", "MAIN", TypeDisplay(t) + ".Main", nodes[fn].Loc!, fn, "high", "static Main: the process entry point");
            }
        }
    }
}
