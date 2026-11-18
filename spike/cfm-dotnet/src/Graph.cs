// GRAPH_SCHEMA.md 2.1 as C# types. internal/graph/types.go is the source of truth; field names
// and omission rules follow its json tags, so `cfm render` (DisallowUnknownFields) accepts the output.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CfmDotnet;

public sealed class Graph
{
    public string SchemaVersion { get; set; } = "2.1.0";
    public string AnalyzerVersion { get; set; } = "cfm-dotnet-spike";
    public string Module { get; set; } = "";
    // ponytail: the schema has no language field yet (ADR-108); the target framework goes here.
    public string GoVersion { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public string? EditorRoot { get; set; }
    public List<Node> Nodes { get; set; } = new();
    public List<Edge> Edges { get; set; } = new();
    public List<Diag> Diagnostics { get; set; } = new();
    public Stats Stats { get; set; } = new();
}

public sealed class Stats
{
    public int Packages { get; set; }
    public int Functions { get; set; }
    public int Endpoints { get; set; }
    public int Edges { get; set; }
    public int Unresolved { get; set; }
    public long ElapsedMs { get; set; }
    public List<int> Blockers { get; set; } = new();
}

public sealed class Loc
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Col { get; set; }
    public int EndLine { get; set; }
}

public sealed class Node
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Pkg { get; set; }
    public Loc? Loc { get; set; }
    public string Status { get; set; } = "resolved";
    public string? Framework { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? Receiver { get; set; }
    public string? Signature { get; set; }
    public List<Step>? Flow { get; set; }
    public Body? Body { get; set; }
    public string? Category { get; set; }
    public string? Reason { get; set; }
}

public sealed class Edge
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Method { get; set; } = "";
    public string Reason { get; set; } = "";
    public Loc Site { get; set; } = new();
    public string? SiteId { get; set; }
    public int? Order { get; set; }
    public int Alternatives { get; set; }
}

public sealed class Diag
{
    public string Severity { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public Loc? Loc { get; set; }
    public string? NodeId { get; set; }
    public Dictionary<string, string>? Details { get; set; }
}

public sealed class Step
{
    public string K { get; set; } = "";
    public string? Site { get; set; }
    public int Line { get; set; }
    public string? Cond { get; set; }
    public string? Tag { get; set; }
    public string? Head { get; set; }
    public List<Step>? Then { get; set; }
    public List<Step>? Else { get; set; }
    public List<Case>? Cases { get; set; }
    public List<Step>? Body { get; set; }
    public Test? Test { get; set; }
}

public sealed class Case
{
    public string Label { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Step> Body { get; set; } = new();
    public Test? Test { get; set; }
}

public sealed class Body
{
    public string Type { get; set; } = "";
    public string Binder { get; set; } = "";
    public int Line { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public List<Field> Fields { get; set; } = new();
}

public sealed class Field
{
    public string? Name { get; set; }
    public string Kind { get; set; } = "";
    public string? Type { get; set; }
    public bool Nullable { get; set; }
    public List<Field>? Fields { get; set; }
    public Field? Elem { get; set; }
}

public sealed class Test
{
    public string Op { get; set; } = "";
    public List<Test>? Args { get; set; }
    public Ref? Ref { get; set; }
    // Raw JSON constant; "null" is a real value (nil), so it is not the same as "absent".
    [JsonConverter(typeof(RawJsonConverter))]
    public string? Value { get; set; }
}

public sealed class Ref
{
    public string Type { get; set; } = "";
    public List<string> Path { get; set; } = new();
    public List<string>? Fold { get; set; }
}

sealed class RawJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        JsonDocument.ParseValue(ref reader).RootElement.GetRawText();

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions o) =>
        writer.WriteRawValue(value ?? "null");
}

public static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Go's omitempty: zero ints, false, null and empty strings are left out.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true,
    };

    public static string Serialize(Graph g)
    {
        // Lists the Go side marks without omitempty must be present even when empty.
        return JsonSerializer.Serialize(g, Options) + "\n";
    }
}
