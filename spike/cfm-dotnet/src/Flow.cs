// Per-function flow outline (GRAPH_SCHEMA §5, ARCHITECTURE §4.3) and body tests (§8) for C#.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CfmDotnet;

public sealed partial class Analyzer
{
    // Named types that appear in some bound request body; tests are set on conditions that read them.
    readonly HashSet<INamedTypeSymbol> bodyTypes = new(SymbolEqualityComparer.Default);

    List<Step> OutlineFunction(string id, IMethodSymbol m)
    {
        var decl = BodyOf(m)!;
        var model = Model(decl.SyntaxTree);
        var o = new Outliner(this, id, model);
        var steps = new List<Step>();
        if (decl is ConstructorDeclarationSyntax { Initializer: { } init })
        {
            steps.AddRange(o.Calls(init));
        }
        var (block, arrow) = decl switch
        {
            BaseMethodDeclarationSyntax b => (b.Body, b.ExpressionBody),
            LocalFunctionStatementSyntax l => (l.Body, l.ExpressionBody),
            _ => (null, null),
        };
        if (block != null) steps.AddRange(o.Block(block.Statements));
        if (arrow != null)
        {
            steps.AddRange(o.Calls(arrow.Expression));
            if (!m.ReturnsVoid) steps.Add(new Step { K = "return", Line = Line(arrow) });
        }
        return Prune(steps);
    }

    int Line(SyntaxNode n) => Loader.LocOf(ws, n.GetLocation())!.Line;

    // §4.3 pruning: a construct stays only if a call, return or throw sits beneath it.
    static List<Step> Prune(List<Step> steps)
    {
        var kept = new List<Step>();
        foreach (var s in steps)
        {
            switch (s.K)
            {
                case "if":
                    s.Then = NullIfEmpty(Prune(s.Then ?? new()));
                    s.Else = NullIfEmpty(Prune(s.Else ?? new()));
                    if (s.Then != null || s.Else != null) kept.Add(s);
                    break;
                case "loop":
                    s.Body = NullIfEmpty(Prune(s.Body ?? new()));
                    if (s.Body != null) kept.Add(s);
                    break;
                case "switch":
                    foreach (var c in s.Cases!) c.Body = Prune(c.Body);
                    s.Cases = s.Cases!.Where(c => c.Body.Count > 0).ToList();
                    if (s.Cases.Count > 0) kept.Add(s);
                    break;
                default:
                    kept.Add(s);
                    break;
            }
        }
        return kept;
    }

    static List<Step>? NullIfEmpty(List<Step> l) => l.Count == 0 ? null : l;

    sealed class Outliner
    {
        readonly Analyzer a;
        readonly string fn;
        readonly SemanticModel model;

        public Outliner(Analyzer a, string fn, SemanticModel model)
        {
            this.a = a;
            this.fn = fn;
            this.model = model;
        }

        int Line(SyntaxNode n) => a.Line(n);

        static IEnumerable<StatementSyntax> Stmts(StatementSyntax? s) => s switch
        {
            null => Array.Empty<StatementSyntax>(),
            BlockSyntax b => b.Statements,
            _ => new[] { s },
        };

        public List<Step> Block(IEnumerable<StatementSyntax> stmts)
        {
            var steps = new List<Step>();
            foreach (var s in stmts) Statement(s, steps);
            return steps;
        }

        void Statement(StatementSyntax s, List<Step> steps)
        {
            switch (s)
            {
                case BlockSyntax b:
                    steps.AddRange(Block(b.Statements));
                    break;
                case IfStatementSyntax i:
                    steps.AddRange(Calls(i.Condition));
                    steps.Add(new Step
                    {
                        K = "if", Line = Line(i), Cond = Loader.Trunc(i.Condition.ToString()), Test = a.CondTest(i.Condition, model),
                        Then = Block(Stmts(i.Statement)),
                        Else = i.Else == null ? null : Block(Stmts(i.Else.Statement)),
                    });
                    break;
                case SwitchStatementSyntax sw:
                    steps.AddRange(Calls(sw.Expression));
                    steps.Add(Switch(sw));
                    break;
                case ForStatementSyntax f:
                    if (f.Declaration != null) steps.AddRange(Calls(f.Declaration));
                    foreach (var init in f.Initializers) steps.AddRange(Calls(init));
                    var body = new List<Step>();
                    if (f.Condition != null) body.AddRange(Calls(f.Condition));
                    body.AddRange(Block(Stmts(f.Statement)));
                    foreach (var inc in f.Incrementors) body.AddRange(Calls(inc));
                    var head = $"{f.Declaration}{string.Join(", ", f.Initializers)}; {f.Condition}; {string.Join(", ", f.Incrementors)}";
                    steps.Add(new Step { K = "loop", Line = Line(f), Head = Loader.Trunc(head), Body = body });
                    break;
                case ForEachStatementSyntax fe:
                    steps.AddRange(Calls(fe.Expression));
                    steps.Add(new Step { K = "loop", Line = Line(fe), Head = Loader.Trunc($"{fe.Type} {fe.Identifier} in {fe.Expression}"), Body = Block(Stmts(fe.Statement)) });
                    break;
                case ForEachVariableStatementSyntax fv:
                    steps.AddRange(Calls(fv.Expression));
                    steps.Add(new Step { K = "loop", Line = Line(fv), Head = Loader.Trunc($"{fv.Variable} in {fv.Expression}"), Body = Block(Stmts(fv.Statement)) });
                    break;
                case WhileStatementSyntax w:
                    var wb = Calls(w.Condition);
                    wb.AddRange(Block(Stmts(w.Statement)));
                    steps.Add(new Step { K = "loop", Line = Line(w), Head = Loader.Trunc("while " + w.Condition), Body = wb });
                    break;
                case DoStatementSyntax d:
                    var db = Block(Stmts(d.Statement));
                    db.AddRange(Calls(d.Condition));
                    steps.Add(new Step { K = "loop", Line = Line(d), Head = Loader.Trunc("do … while " + d.Condition), Body = db });
                    break;
                case TryStatementSyntax t:
                    // ponytail: the schema has no try step. The try block runs inline, each catch is an `if`
                    // on "the exception was thrown", and finally runs after; a `try` step is a schema change.
                    steps.AddRange(Block(t.Block.Statements));
                    foreach (var c in t.Catches)
                    {
                        var cond = "catch" + (c.Declaration != null ? $" ({c.Declaration.Type})" : "") + (c.Filter != null ? $" when ({c.Filter.FilterExpression})" : "");
                        steps.Add(new Step { K = "if", Line = Line(c), Cond = Loader.Trunc(cond), Then = Block(c.Block.Statements) });
                    }
                    if (t.Finally != null) steps.AddRange(Block(t.Finally.Block.Statements));
                    break;
                case UsingStatementSyntax u:
                    if (u.Declaration != null) steps.AddRange(Calls(u.Declaration));
                    if (u.Expression != null) steps.AddRange(Calls(u.Expression));
                    steps.AddRange(Block(Stmts(u.Statement)));
                    break;
                case LockStatementSyntax l:
                    steps.AddRange(Calls(l.Expression));
                    steps.AddRange(Block(Stmts(l.Statement)));
                    break;
                case CheckedStatementSyntax c:
                    steps.AddRange(Block(c.Block.Statements));
                    break;
                case UnsafeStatementSyntax us:
                    steps.AddRange(Block(us.Block.Statements));
                    break;
                case FixedStatementSyntax fx:
                    steps.AddRange(Calls(fx.Declaration));
                    steps.AddRange(Block(Stmts(fx.Statement)));
                    break;
                case LabeledStatementSyntax ls:
                    Statement(ls.Statement, steps);
                    break;
                case ReturnStatementSyntax r:
                    if (r.Expression != null) steps.AddRange(Calls(r.Expression));
                    steps.Add(new Step { K = "return", Line = Line(r) });
                    break;
                case ThrowStatementSyntax th:
                    if (th.Expression != null) steps.AddRange(Calls(th.Expression));
                    steps.Add(new Step { K = "panic", Line = Line(th) });
                    break;
                case LocalFunctionStatementSyntax lf:
                    // Its own function node; the parent holds a call step only where it is called.
                    if (model.GetDeclaredSymbol(lf) is IMethodSymbol lm && BodyOf(lm) != null) a.AddFunction(lm);
                    break;
                default:
                    steps.AddRange(Calls(s));
                    break;
            }
        }

        Step Switch(SwitchStatementSyntax sw)
        {
            var r = a.RefOf(sw.Expression, model);
            var cases = new List<Case>();
            var caseTests = new List<Test>();
            var defaults = new List<Case>();
            foreach (var sec in sw.Sections)
            {
                var labels = new List<string>();
                var tests = new List<Test>();
                var isDefault = false;
                var testable = r != null;
                foreach (var l in sec.Labels)
                {
                    switch (l)
                    {
                        case DefaultSwitchLabelSyntax:
                            isDefault = true;
                            break;
                        case CaseSwitchLabelSyntax c:
                            labels.Add(c.Value.ToString());
                            if (r != null && a.Const(c.Value, model) is { } v) tests.Add(Cmp("eq", r, v));
                            else testable = false;
                            break;
                        case CasePatternSwitchLabelSyntax p:
                            labels.Add(p.Pattern + (p.WhenClause != null ? " " + p.WhenClause : ""));
                            testable = false;
                            break;
                    }
                }
                var body = Block(sec.Statements);
                var c2 = new Case { Label = Loader.Trunc(isDefault && labels.Count == 0 ? "default" : string.Join(", ", labels.Concat(isDefault ? new[] { "default" } : Array.Empty<string>()))), Body = body };
                if (isDefault) { if (labels.Count == 0) defaults.Add(c2); }
                else if (testable && tests.Count > 0)
                {
                    c2.Test = tests.Count == 1 ? tests[0] : new Test { Op = "or", Args = tests };
                    caseTests.Add(c2.Test);
                }
                cases.Add(c2);
            }
            // default: none of the other cases matched. Needs every other case to be testable.
            var allTestable = cases.Count(c => c.Label != "default") == caseTests.Count;
            foreach (var d in defaults)
            {
                if (allTestable && caseTests.Count > 0) d.Test = new Test { Op = "and", Args = caseTests.Select(t => new Test { Op = "not", Args = new() { t } }).ToList() };
            }
            return new Step { K = "switch", Line = Line(sw), Tag = Loader.Trunc(sw.Expression.ToString()), Cases = cases };
        }

        // Call steps in evaluation order: operands before the call that uses them. Lambdas and anonymous
        // methods are walked in place. ponytail: their calls are attributed to the enclosing function.
        public List<Step> Calls(SyntaxNode n)
        {
            var steps = new List<Step>();
            Visit(n, steps);
            return steps;
        }

        void Visit(SyntaxNode n, List<Step> steps)
        {
            if (n is LocalFunctionStatementSyntax) return;
            foreach (var c in n.ChildNodes()) Visit(c, steps);
            switch (n)
            {
                case InvocationExpressionSyntax inv:
                    Invocation(inv, steps);
                    break;
                case BaseObjectCreationExpressionSyntax oc:
                    Creation(oc, steps);
                    break;
                case ConstructorInitializerSyntax ci when model.GetSymbolInfo(ci).Symbol is IMethodSymbol ctor && a.IsOwn(ctor) && BodyOf(ctor) != null:
                    Emit(ci.ArgumentList.OpenParenToken.GetLocation(), ci.ToString(), steps, a.Static(ctor, Site(ci.ArgumentList.OpenParenToken.GetLocation()), ci.ToString()));
                    break;
            }
        }

        void Invocation(InvocationExpressionSyntax inv, List<Step> steps)
        {
            if (model.GetConstantValue(inv).HasValue) return; // nameof(...)
            var info = model.GetSymbolInfo(inv);
            var m = info.Symbol as IMethodSymbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] as IMethodSymbol : null);
            ExpressionSyntax? receiver = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Expression,
                MemberBindingExpressionSyntax => inv.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
                _ => null,
            };
            var paren = inv.ArgumentList.OpenParenToken.GetLocation();
            var site = Site(paren);
            if (m == null && (model.GetTypeInfo(inv).Type?.TypeKind == TypeKind.Dynamic || (receiver != null && model.GetTypeInfo(receiver).Type?.TypeKind == TypeKind.Dynamic)))
            {
                // A `dynamic` receiver is bound by the runtime binder, so there is no static target.
                Emit(paren, inv.ToString(), steps, new() { new(a.Unresolved(site, inv.ToString(), "reflection"), "unresolved", "unknown", "dynamic call: the member is bound at runtime") });
                return;
            }
            Emit(paren, inv.ToString(), steps, a.Resolve(m, receiver, model, site, inv.ToString()));
        }

        // `new T(...)` is a call step only when T has an explicit constructor with a body in the
        // analyzed code; DTO and framework constructions would bury the flow.
        void Creation(BaseObjectCreationExpressionSyntax oc, List<Step> steps)
        {
            if (model.GetSymbolInfo(oc).Symbol is not IMethodSymbol ctor || !a.IsOwn(ctor) || BodyOf(ctor) is not ConstructorDeclarationSyntax cd) return;
            // An empty `public X() { }` (often just a TODO comment) has nothing to show.
            if (cd.Initializer == null && cd.ExpressionBody == null && cd.Body!.Statements.Count == 0) return;
            var at = oc.ArgumentList?.OpenParenToken.GetLocation() ?? oc.NewKeyword.GetLocation();
            Emit(at, oc.ToString(), steps, a.Static(ctor.OriginalDefinition, Site(at), oc.ToString()));
        }

        Loc Site(Location l) => Loader.LocOf(a.ws, l)!;

        void Emit(Location at, string text, List<Step> steps, List<Target> targets)
        {
            var site = Site(at);
            var siteId = $"{site.File}:{site.Line}:{site.Col}";
            foreach (var t in targets)
            {
                a.AddEdge(new Edge { Source = fn, Target = t.To, Kind = "calls", Confidence = t.Confidence, Method = t.Method, Reason = t.Reason, Site = site, SiteId = siteId, Alternatives = t.Alternatives });
            }
            steps.Add(new Step { K = "call", Site = siteId, Line = site.Line });
        }
    }

    // ---- body tests (§8) ----

    static Test Cmp(string op, Ref r, string value) => new() { Op = op, Ref = r, Value = value };

    static readonly Test Unknown = new() { Op = "unknown" };

    Test? CondTest(ExpressionSyntax e, SemanticModel model)
    {
        if (bodyTypes.Count == 0) return null;
        var t = T(e, model);
        return t == null || t.Op == "unknown" ? null : t;
    }

    Test? T(ExpressionSyntax e, SemanticModel model)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        switch (e)
        {
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression):
                {
                    var op = b.IsKind(SyntaxKind.LogicalAndExpression) ? "and" : "or";
                    var l = T(b.Left, model);
                    var r = T(b.Right, model);
                    if (l == null && r == null) return null;
                    var args = new List<Test>();
                    foreach (var x in new[] { l ?? Unknown, r ?? Unknown })
                    {
                        if (x.Op == op) args.AddRange(x.Args!); else args.Add(x);
                    }
                    return new Test { Op = op, Args = args };
                }
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                return T(u.Operand, model) is { } inner ? new Test { Op = "not", Args = new() { inner } } : null;
            case BinaryExpressionSyntax b:
                {
                    var op = b.Kind() switch
                    {
                        SyntaxKind.EqualsExpression => "eq", SyntaxKind.NotEqualsExpression => "ne",
                        SyntaxKind.LessThanExpression => "lt", SyntaxKind.LessThanOrEqualExpression => "le",
                        SyntaxKind.GreaterThanExpression => "gt", SyntaxKind.GreaterThanOrEqualExpression => "ge",
                        _ => null,
                    };
                    if (op == null) return null;
                    if (RefOf(b.Left, model) is { } lr && Const(b.Right, model) is { } rv) return Cmp(op, lr, rv);
                    if (RefOf(b.Right, model) is { } rr && Const(b.Left, model) is { } lv)
                    {
                        var flip = op switch { "lt" => "gt", "le" => "ge", "gt" => "lt", "ge" => "le", _ => op };
                        return Cmp(flip, rr, lv);
                    }
                    return null;
                }
            case InvocationExpressionSyntax inv when model.GetSymbolInfo(inv).Symbol is IMethodSymbol m && m.ContainingType.SpecialType == SpecialType.System_String:
                return StringTest(inv, m, model);
            default:
                if (model.GetTypeInfo(e).Type?.SpecialType == SpecialType.System_Boolean && RefOf(e, model) is { } br) return new Test { Op = "truthy", Ref = br };
                return null;
        }
    }

    // string.IsNullOrEmpty(x), x.Equals("c"), string.Equals(x, "c", StringComparison.OrdinalIgnoreCase)
    Test? StringTest(InvocationExpressionSyntax inv, IMethodSymbol m, SemanticModel model)
    {
        var args = inv.ArgumentList.Arguments.Select(x => x.Expression).ToList();
        if (m.IsStatic && m.Name is "IsNullOrEmpty" or "IsNullOrWhiteSpace" && args.Count == 1 && RefOf(args[0], model) is { } r)
        {
            var blank = m.Name == "IsNullOrWhiteSpace" ? new Ref { Type = r.Type, Path = r.Path, Fold = (r.Fold ?? new()).Append("trim").ToList() } : r;
            return new Test { Op = "or", Args = new() { Cmp("eq", r, "null"), Cmp("eq", blank, "\"\"") } };
        }
        if (m.Name != "Equals") return null;
        var operands = m.IsStatic ? args : (inv.Expression is MemberAccessExpressionSyntax ma ? new[] { ma.Expression }.Concat(args).ToList() : null);
        if (operands == null || operands.Count < 2) return null;
        var ignoreCase = operands.Count > 2 && operands[2].ToString().Contains("IgnoreCase", StringComparison.Ordinal);
        Ref? rf = null;
        string? val = null;
        if (RefOf(operands[0], model) is { } r0 && Const(operands[1], model) is { } v1) (rf, val) = (r0, v1);
        else if (RefOf(operands[1], model) is { } r1 && Const(operands[0], model) is { } v0) (rf, val) = (r1, v0);
        if (rf == null || val == null) return null;
        if (ignoreCase)
        {
            rf = new Ref { Type = rf.Type, Path = rf.Path, Fold = (rf.Fold ?? new()).Append("lower").ToList() };
            val = val.ToLowerInvariant();
        }
        return Cmp("eq", rf, val);
    }

    // A field path inside a bound body type, with ToUpper/ToLower/Trim folds, or null.
    Ref? RefOf(ExpressionSyntax e, SemanticModel model)
    {
        if (bodyTypes.Count == 0) return null;
        var fold = new List<string>();
        while (e is InvocationExpressionSyntax inv && inv.Expression is MemberAccessExpressionSyntax ma &&
               model.GetSymbolInfo(inv).Symbol is IMethodSymbol m && m.ContainingType.SpecialType == SpecialType.System_String &&
               (m.Name is "ToUpper" or "ToUpperInvariant" or "ToLower" or "ToLowerInvariant" || (m.Name == "Trim" && inv.ArgumentList.Arguments.Count == 0)))
        {
            fold.Insert(0, m.Name.StartsWith("ToUpper") ? "upper" : m.Name.StartsWith("ToLower") ? "lower" : "trim");
            e = ma.Expression;
        }
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        var segs = new List<(string name, ITypeSymbol? type)>();
        var cur = e;
        while (true)
        {
            if (cur is MemberAccessExpressionSyntax ma2 && model.GetSymbolInfo(ma2).Symbol is IPropertySymbol or IFieldSymbol)
            {
                var s = model.GetSymbolInfo(ma2).Symbol!;
                if (s.IsStatic) return null;
                segs.Insert(0, (s.Name, s is IPropertySymbol ps ? ps.Type : ((IFieldSymbol)s).Type));
                cur = ma2.Expression;
                continue;
            }
            if (cur is ConditionalAccessExpressionSyntax) return null;
            break;
        }
        // The outermost bound type the expression reaches: the root itself, or a segment's type.
        var rootType = model.GetTypeInfo(cur).Type as INamedTypeSymbol;
        var start = -1;
        INamedTypeSymbol? bound = null;
        if (rootType != null && bodyTypes.Contains(rootType.OriginalDefinition)) { start = 0; bound = rootType; }
        else
        {
            for (var i = 0; i < segs.Count - 1; i++)
            {
                if (segs[i].type is INamedTypeSymbol st && bodyTypes.Contains(st.OriginalDefinition)) { start = i + 1; bound = st; break; }
            }
        }
        if (bound == null || start >= segs.Count) return null;
        return new Ref { Type = QualifiedType(bound), Path = segs.Skip(start).Select(s => s.name).ToList(), Fold = fold.Count > 0 ? fold : null };
    }

    // A compile-time constant as raw JSON, or null when the expression isn't one.
    string? Const(ExpressionSyntax e, SemanticModel model)
    {
        var c = model.GetConstantValue(e);
        if (!c.HasValue) return null;
        return c.Value switch
        {
            null => "null",
            string s => System.Text.Json.JsonSerializer.Serialize(s),
            char ch => System.Text.Json.JsonSerializer.Serialize(ch.ToString()),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    // ---- request body fields (§8) ----

    Body BodyFor(INamedTypeSymbol t, string binder, int line)
    {
        var b = new Body { Type = QualifiedType(t), Binder = binder, Line = line, Fields = FieldsOf(t, Seen()) };
        bodyTypes.Add(t.OriginalDefinition);
        return b;
    }

    // A body made of several parameters (an ASMX method with more than one): a synthetic root
    // object whose fields are the parameters.
    Body BodyForParams(IMethodSymbol m, string binder, int line)
    {
        var b = new Body
        {
            Type = QualifiedType(m.ContainingType) + "." + m.Name + "(…)", Binder = binder, Line = line,
            Fields = m.Parameters.Select(p => FieldFor(p.Name, p.Type, Seen())).ToList(),
        };
        return b;
    }

    static HashSet<INamedTypeSymbol> Seen() => new(SymbolEqualityComparer.Default);

    List<Field> FieldsOf(INamedTypeSymbol t, HashSet<INamedTypeSymbol> seen)
    {
        var fields = new List<Field>();
        if (!seen.Add(t.OriginalDefinition)) return fields;
        var chain = new List<INamedTypeSymbol>();
        for (var b = t; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType) chain.Insert(0, b);
        foreach (var b in chain)
        {
            foreach (var mem in b.GetMembers())
            {
                if (mem.IsStatic || mem.DeclaredAccessibility != Accessibility.Public || mem.IsImplicitlyDeclared) continue;
                if (mem.GetAttributes().Any(x => x.AttributeClass?.Name is "XmlIgnoreAttribute" or "ScriptIgnoreAttribute" or "JsonIgnoreAttribute" or "IgnoreDataMemberAttribute")) continue;
                ITypeSymbol? type = mem switch
                {
                    IPropertySymbol p when p.GetMethod != null && !p.IsIndexer => p.Type,
                    IFieldSymbol f when !f.IsConst => f.Type,
                    _ => null,
                };
                if (type != null) fields.Add(FieldFor(mem.Name, type, seen));
            }
        }
        seen.Remove(t.OriginalDefinition);
        return fields;
    }

    Field FieldFor(string? name, ITypeSymbol type, HashSet<INamedTypeSymbol> seen)
    {
        var nullable = type.IsReferenceType;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            type = n.TypeArguments[0];
            nullable = true;
        }
        var f = new Field { Name = name, Nullable = nullable };
        switch (type.SpecialType)
        {
            case SpecialType.System_String or SpecialType.System_Char or SpecialType.System_DateTime:
                f.Kind = "string"; return f;
            case SpecialType.System_Boolean:
                f.Kind = "boolean"; return f;
            case SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
                SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64:
                f.Kind = "integer"; return f;
            case SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal:
                f.Kind = "number"; return f;
            case SpecialType.System_Object:
                f.Kind = "any"; return f;
        }
        if (type.TypeKind == TypeKind.Enum) { f.Kind = "integer"; return f; }
        if (type.Name is "Guid" or "DateTimeOffset" or "TimeSpan") { f.Kind = "string"; return f; }
        if (type is IArrayTypeSymbol arr)
        {
            f.Kind = "array"; f.Nullable = true; f.Elem = FieldFor(null, arr.ElementType, seen); return f;
        }
        if (type is INamedTypeSymbol nt)
        {
            if (nt.Name is "IDictionary" or "Hashtable" || nt.AllInterfaces.Any(i => i.Name == "IDictionary" && i.ContainingNamespace.ToDisplayString() is "System.Collections" or "System.Collections.Generic"))
            {
                f.Kind = "object"; return f;
            }
            var enumerable = nt.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T ? nt :
                nt.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
            if (enumerable != null)
            {
                f.Kind = "array"; f.Elem = FieldFor(null, enumerable.TypeArguments[0], seen); return f;
            }
            if (IsOwn(nt) && nt.TypeKind is TypeKind.Class or TypeKind.Struct)
            {
                f.Kind = "object"; f.Type = QualifiedType(nt);
                bodyTypes.Add(nt.OriginalDefinition);
                var inner = FieldsOf(nt, seen);
                f.Fields = inner.Count > 0 ? inner : null;
                return f;
            }
        }
        f.Kind = "any";
        return f;
    }
}
