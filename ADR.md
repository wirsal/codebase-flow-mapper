# Codebase Flow Mapper — Architecture Decision Records (v2.0)

The v1.1 records live in `archive/v1.1/ADR.md`. The table below gives each one's status under v2.0, and the ADR-1xx records further down are new.

## Status of v1.1 ADRs

| v1.1 ADR | v2.0 status | Note |
|---|---|---|
| 001 Local-first app (Go server + React + SQLite) | **Superseded by 101** | Still local, but there's no server |
| 002 Modular monolith | **Kept, shrunk** | 6 internal packages (ARCHITECTURE §2) |
| 003 Canonical IR across language adapters | **Replaced by 108** | The graph JSON is the contract; each language gets its own analyzer |
| 004 Go-native tooling | **Kept, amended by 102** | VTA is the call-graph algorithm |
| 005 TypeScript Compiler API | **Deferred, spike planned** | CFM-502 (ADR-108) |
| 006 Tree-sitter supplementary | **Deferred** | Not needed for Go |
| 007 One canonical graph for both views | **Kept** | Flow outline is part of function nodes (103) |
| 008 React Flow + ELK | **Superseded by 104** | |
| 009 SQLite + JSON snapshots | **Superseded by 101** | JSON only |
| 010 Static facts, AI optional | **Kept, AI removed from MVP** | |
| 011 Categorical confidence | **Kept** | Rules in ARCHITECTURE §4.1 |
| 012 No project code execution | **Kept, made concrete by 105** | |
| 013 Local API security | **Mostly moot by 101** | No API exists. The surviving part is the XSS rule (ARCHITECTURE §5) |
| 014 Full analysis, incremental later | **Kept** | No file hashes stored, add them when incremental work starts |
| 015 Adapter-based frameworks | **Kept** | Gin, net/http, Fiber v2 |

---

## ADR-101: CLI that writes a static HTML report

**Status:** Accepted

**Context.** v1.1 planned a local Go server, a React app, SQLite, a project registry, an analysis job state machine, a session-token API, CSRF/Origin checks and DNS-rebinding defences. About half of the backlog was infrastructure that doesn't test the product hypothesis.

**Decision.** `cfm analyze` runs once and writes a single HTML file with the graph and viewer inlined, plus optional JSON.

**Consequences.**
- No HTTP API means no attack surface for local-API abuse, so v1.1 ADR-013 drops to a single XSS rule.
- Source navigation goes through editor URL schemes (`vscode://file/…`) instead of a source-reading endpoint, so there is no file-read API to protect.
- The ceiling is report size. A very large module (> ~200k functions) may produce a report too heavy for the browser. M3 measures where that happens, and the fix then is a `serve` subcommand with lazy loading. That fix is deliberately not built now.

## ADR-102: VTA for call resolution

**Status:** Accepted, **must be validated in M0**

**Context.** Layered Go services call interfaces. With only static resolution, the graph stops at the interface method. CHA lists every type that implements the interface, including counterfeiter fakes and every mock. RTA needs `main` roots and works coarsely at the program level. Pointer analysis (`go/pointer`) is deprecated and slow.

**Decision.** Use `vta.CallGraph(ssautil.AllFunctions(prog), cha.CallGraph(prog))`. Test files and fake/mock packages are excluded from the load (PRD FR-02), so fakes can't flow into production call sites.

**Consequences.**
- Precision depends on the composition root (`main`, `wire_gen.go`) being in the load. Without it, interface sites come out unresolved, and CFM says so (`NO_COMPOSITION_ROOT`).
- Memory and time on large programs are unknown, and M0/M3 measure them. Fallback if they're too slow: CHA plus the exclusion list. Only build that fallback if a measurement demands it.

## ADR-103: Control flow is a per-function outline, not graph nodes

**Status:** Accepted

**Context.** v1.1 put every `if`, `case`, `loop` and `return` into the canonical graph as nodes, with 12 control-flow edge kinds, next to call edges. That multiplies node count several times over and mixes two different relations in one traversal. It also forces every traversal to filter by edge family.

**Decision.** Each function node carries a pruned `flow` tree (GRAPH_SCHEMA §5). The call graph keeps only `routes_to`, `uses_middleware` and `calls`. The Flowchart view renders outlines and stitches them across calls by `siteId`.

**Consequences.**
- Traversal is trivial, because every edge is a call-ish edge.
- Branch-level queries ("which endpoints reach this `if`?") need the outline to be walked. That isn't an MVP requirement.

## ADR-104: Vanilla JS + Cytoscape.js (+dagre), vendored, no build step

**Status:** Accepted

**Context.** React plus React Flow plus ELK.js means an npm toolchain, a bundler, and a frontend package to maintain, all for two graph views and a search list.

**Decision.** One `viewer.js` (target < 1,500 lines) uses Cytoscape.js and cytoscape-dagre. They are vendored at pinned versions under `web/vendor/` with their licenses, and embedded with `go:embed`.

**Consequences.**
- `go build` produces the whole product.
- If the viewer grows past roughly 3k lines, or needs real component state, revisit this and move to a bundled framework.

## ADR-105: Concrete no-execution contract for go/packages

**Status:** Accepted

**Context.** `go/packages` runs `go list`. With default settings, `go list` can download modules or toolchains, rewrite `go.mod`, and invoke the C toolchain for cgo files.

**Decision.** Pin the env to `GOFLAGS=-mod=readonly GOPROXY=off GOTOOLCHAIN=local CGO_ENABLED=0` (ARCHITECTURE §6). Missing modules produce diagnostics, never downloads. The `evil` and `missing-dep` fixtures enforce this.

**Consequences.**
- On a fresh clone the user must run `go mod download` themselves first. That's their action on their own repo, and the README says so.
- With `CGO_ENABLED=0`, cgo-only files are excluded, and their functions are missing from the graph or reported as load errors.

## ADR-106: Location-free function IDs

**Status:** Accepted

**Decision.** Function IDs are `fn:<pkgPath>.<(Recv).>Name`, with no line numbers (GRAPH_SCHEMA §6). Go has no overloading, so these names are unique per build. Endpoint and unresolved IDs keep their location, because duplicates of those are legal or have no name.

**Consequences.** IDs survive code movement, so a future graph diff stays cheap. Function literals keep SSA ordinal numbering (`$1`), so adding a closure earlier in the same function renumbers the later ones. That's acceptable for the MVP.

## ADR-107: Detect missing generated files, never generate them

**Status:** Accepted (M0 finding)

**Context.** Both real repos gitignore `wire_gen.go` and the swag `docs/` package. Without `wire_gen.go`, VTA has no composition root and resolves **0** interface hops. CFM's main value disappears silently, and the report just looks shallow.

**Decision.**
- Detect the missing files: `wireinject`-tagged files in `IgnoredFiles` with no `wire_gen.go` present, and in-module imports that have no Go files. Emit `WIRE_GEN_MISSING` / `GENERATED_PKG_MISSING` with the exact fix command, and show a blocker banner in the report.
- Don't run `wire`, `swag`, or `go generate`. They are tools acting on the user's repo, and running them breaks ADR-105's no-execution contract.

**Alternatives rejected.**
- Running the generators on a temp copy: this executes third-party tools and adds copy semantics to keep track of.
- Interpreting `wire.Build(...)` provider sets statically: this would work without generated code, but it reimplements Wire's solver. Revisit only if users routinely can't run `wire`.

**Validation.** Add a fixture `wire-missing` containing a `wire.go` (`wireinject`) and no `wire_gen.go`, plus an import of an empty in-module `docs` package. Both warnings must appear, and `stats.blockers` must list both.

## ADR-108: Other languages are separate analyzers that write the graph JSON

**Status:** Accepted. Step 1 (`cfm render`) shipped. The TypeScript spike (CFM-502) says go, once a real NestJS service is available to validate against. The .NET spike (CFM-503, ADR-109) says go, validated on a real service (`docs/validation.md`)

**Context.** Everything that makes the report useful is Go-specific: `go/packages`, SSA, VTA, the router and binder rules. Only `GRAPH_SCHEMA.md` and the viewer are language-free. TypeScript or PHP support therefore needs a new front end, built on that language's own type checker, because resolution through interfaces and DI is the reason CFM exists.

**Decision.**
- `GRAPH_SCHEMA.md` is the contract between analyzers and the viewer. `cfm render <graph.json> -o report.html` wraps any graph that passes `Validate` in the viewer. The graph is foreign input there, so a violation is exit 3, not a CFM bug. Unknown fields are refused, so schema drift fails loudly.
- Each language gets its own analyzer in its own runtime (`cfm-ts` on Node with the TypeScript compiler API, later maybe PHP). `cfm` stays one Go binary.
- Schema changes a second language needs, such as a `language` field in place of `goVersion` or function IDs with overloads, are made by the spike that needs them, not ahead of it.

**Alternatives rejected.**
- Tree-sitter for every language: broad but untyped, so calls through interfaces and DI stop at the interface.
- SCIP indexers as the front end: precise definitions and references, but no routes, flow outline or body.
- One `cfm` binary with every language inside: it would bundle Node and PHP, or reimplement their type checkers in Go.

**Consequences.**
- A schema change is made once per analyzer.
- Each analyzer ships through its own channel (`go install`, npm, …).
- A call graph through interfaces must be written by hand outside Go, since there is no VTA for TypeScript or PHP. This is the main risk the spike measures.
- PHP frameworks resolve much at runtime (facades, string container keys, `Controller@method`), and `php artisan route:list` runs app code (ADR-105). A PHP analyzer would parse routes statically and leave more unresolved.

## ADR-109: The .NET analyzer reads C# with Roslyn, without MSBuild

**Status:** Accepted for the spike (CFM-503)

**Context.** ADR-108 gives each language its own analyzer on that language's type checker. For C# that is Roslyn. The
usual way to feed Roslyn is `MSBuildWorkspace`, but it runs a design-time build: MSBuild targets and tasks from the
project and its NuGet packages, plus source generators. All of that is code from the analyzed project, which ADR-012
and ADR-105 rule out. Legacy ASP.NET Web Site projects also have no `.csproj` at all.

**Decision.**
- `cfm-dotnet` builds one `CSharpCompilation` by hand. It reads `.cs` files from disk, honours an old-style `.csproj`
  `<Compile Include>` list when there is one, and turns code in `.asmx`/`.ashx` and `Global.asax` into syntax trees,
  with `#line` keeping locations on the real file.
- References are metadata only: the .NET Framework reference assemblies from the NuGet cache (downloaded by
  restoring `cfm-dotnet` itself, never the target), plus the DLLs in the target's `bin/`. If they're missing, the
  analyzer reports `DOTNET_REFS_MISSING` as a blocker with the fix.
- `bin`, `obj`, `packages`, `Backup` (left by Visual Studio upgrades), `.vs` and test or mock folders are skipped.
- Interface and virtual calls resolve in this order: receiver only ever assigned `new T()`, then Rapid Type Analysis
  over types created with `new`, then the class hierarchy at `low`. Never by name.

**Consequences.**
- Output of source generators and T4 templates that isn't checked in is invisible. That's the same trade as ADR-107,
  and a diagnostic for it is future work.
- `cfm-dotnet` needs the .NET SDK to build. It targets `net8.0` with roll-forward, so any SDK from 8 on works.
- The analyzer is C#, in the repo but outside the Go module, and Go CI doesn't build it.

