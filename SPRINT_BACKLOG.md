# Codebase Flow Mapper — Backlog (lean MVP)

**Version:** 2.0
**Supersedes:** `archive/v1.1/SPRINT_BACKLOG.md` (6 sprints, ~30 tasks)

| Milestone | Goal | Rough size (1 dev) |
|---|---|---|
| **M0** Spike | Check whether VTA resolves handler → service → repo through DI on real repos | 2–3 days |
| **M1** Analyzer | Graph JSON correct on all fixtures | ~1.5 weeks |
| **M2** Viewer | HTML report with Call Tree + Flowchart | ~1 week |
| **M3** Validate | Release gate (PRD §6) on 2 real repos | 3–4 days |

Sizes are guesses, and M0 is there to calibrate them. Tasks run in order unless marked ∥ (parallel-safe).

---

## M0: Spike (throwaway code under `spike/`)

### CFM-001: VTA reachability spike ✅ done 2026-11-12 (`docs/spike-m0.md`, gate passed 5/5 + 5/5)
**Do:** Write a 150-line `spike/main.go` that loads a module with the safety env (ARCHITECTURE §6), excludes `*fakes`/`*mocks`, builds SSA and VTA, and takes a function name as input. It prints the call tree to depth 6 with each edge marked static or dynamic, and prints elapsed time and RSS.
**Run on:** 2 real target repos (one Gin, and either net/http or Fiber), plus a tiny DI fixture.
**AC:**
- For 5 handlers per repo, the printed tree reaches the concrete repository method through ≥1 interface hop.
- No fake/mock type appears.
- Elapsed time and peak RSS recorded in `docs/spike-m0.md`.
**Gate:** If fewer than 4/5 handlers resolve, **stop**. Record why (no composition root? reflection-based DI? fx/dig?) and revisit ADR-102 before M1.

### CFM-002: Route extraction spike ⏭ folded into CFM-105 (route risks are covered by fixtures)
**Do:** In the same spike, walk the AST to print `METHOD path → handler` for Gin only, including router-as-parameter across files.
**AC:** Matches a hand-written route list for one real repo, or the misses are listed with their pattern in `docs/spike-m0.md`.

---

## M1: Analyzer

### CFM-101: Module skeleton + graph types ✅ done 2026-11-12
**Files:** `go.mod`, `cmd/cfm/main.go`, `internal/graph/{types.go,id.go,validate.go,encode.go,graph_test.go}`. `SiteKey`/`LocOf` live in `id.go`, and the module path is still the placeholder `cfm`.
**AC:**
- Types match GRAPH_SCHEMA exactly (camelCase tags).
- `Validate` implements every rule in §7.
- `Encode` sorts deterministically.
- Unit tests for ID formats, `siteKey`, and each validation rule failing.

### CFM-102: Loader ✅ done 2026-11-12 (also verified on the real service B checkout: all 4 expected diagnostics)
**Files:** `internal/load/load.go`
**AC:**
- Safety env applied, and asserted in a test by inspecting `cfg.Env`.
- Exclusion globs work.
- `PACKAGE_EXCLUDED`, `GO_LOAD_ERROR`, `GO_TYPE_ERROR` and `NO_COMPOSITION_ROOT` are emitted.
- The `missing-dep` fixture yields a partial result without a network call.
- `WIRE_GEN_MISSING` / `GENERATED_PKG_MISSING` with `details.fix` and `stats.blockers` (ADR-107). The new `wire-missing` fixture passes.

### CFM-103: Fixtures + golden harness ∥ (with 102) ✅ done 2026-11-13 (10/10 fixtures incl. evil; stage-level hand expectations per fixture; full-graph goldens `expected.json` are reviewed snapshots in `internal/pipeline/pipeline_test.go -update`)
**Files:** `testdata/fixtures/*` (the 9 fixtures in ARCHITECTURE §7), `internal/pipeline_test.go` with `-update`
**AC:**
- Each fixture is its own module with `go.sum`.
- Hand-written `expected.json` exists for `di-interface` **before** CFM-104 is implemented, so it's an independent expectation rather than a snapshot of whatever the code outputs.

### CFM-104: Call graph stage ✅ done 2026-11-12 (smoke on service B: 265 functions, 214 externals, 6 honest unresolved, ~11 s)
**Files:** `internal/callgraph/{callgraph.go,external.go}`
**AC:**
- The confidence/method table in ARCHITECTURE §4.1 is implemented.
- Externals are categorised.
- Unresolved nodes are created with reasons.
- Synthetic wrappers are followed through.
- The `di-interface` golden passes, with the fake package absent.

### CFM-105: Route tracking core + Gin ✅ done 2026-11-12 (stage-level hand expectations `routes.expected.txt` for gin-basic and gin-split-routes; service B: 32 endpoints = 32 registrations in source, 0 diagnostics)
**Files:** `internal/routes/{router.go,gin.go}`
**AC:**
- Rules 1–5 from ARCHITECTURE §4.2 work, including the parameter fixed point (depth 8).
- `Use` ordering is correct.
- `gin-basic` and `gin-split-routes` goldens pass.
- Dynamic paths are kept with `?`.

### CFM-106: net/http ✅ done 2026-11-13 (hand expectations for nethttp, the routes part of dynamic, and di-interface; service B unchanged at 32 endpoints)
**Files:** `internal/routes/nethttp.go`
**AC:** Every handler-argument rule in §4.2 works, Go 1.22 patterns are parsed, and the `nethttp` golden passes.

### CFM-107: Fiber v2 ✅ done 2026-11-13 (hand expectations for fiber; the v3 import lives in missing-dep because v3 is not in the cache either; service B unchanged)
**Files:** `internal/routes/fiber.go`
**AC:** The `fiber` golden passes, and a Fiber v3 import yields `ROUTER_UNSUPPORTED`.

### CFM-108: Flow outline ✅ done 2026-11-13 (new `flow` fixture with a hand-written `flow.expected.txt`; §7.3 holds on all fixtures and on service B: 1714 sites, 0 mismatches, ~5 ms)
**Files:** `internal/flow/flow.go`
**AC:**
- Every step kind in GRAPH_SCHEMA §5 is produced.
- Pruning works.
- Builtins and conversions are skipped.
- The 120-rune truncation works.
- `Validate` rule §7.3 holds on all fixtures, so every call site matches an edge.

### CFM-109: Pipeline + CLI ✅ done 2026-11-13 (service B via CLI: 32 endpoints, 265 functions, 1801 edges, validates, exit 0, ~10 s)
**Files:** `cmd/cfm/main.go`, `internal/pipeline/pipeline.go` (own package instead of a package named `internal`), `internal/report/report.go` (data-only placeholder until CFM-201)
**AC:**
- `cfm analyze <dir> -o x.html --json x.json --exclude … --editor …` works.
- Exit codes: 0 = ok, 1 = analysis had errors but a report was written, 2 = CFM bug / validation failure, 3 = bad usage.
- `stats` are filled in.
- The `evil` fixture passes.

---

## M2: Viewer

### CFM-201: Report template + vendoring ✅ done 2026-11-13 (cytoscape 3.34.4 + cytoscape-dagre 4.0.1, which bundles @dagrejs/dagre 3.0.0 and graphlib 4.0.1; pins and sha256 in `web/vendor/VERSIONS.md`; verified offline from file:// in Chromium; hash-based CSP; interim route-map view until CFM-202; direction in `DESIGN.md`)
**Files:** `internal/report/report.go`, `web/vendor/*`
**AC:**
- A single HTML file opens from `file://` with the network disabled.
- Cytoscape and dagre versions are pinned and their licenses included.
- The `evil` report contains no raw `</script>`.

### CFM-202: Shell, lists, search, detail panel ✅ done 2026-11-13 (three panes; `/`, arrows, Home/End, Enter/Space, Escape clears; edge detail with Back; vscode/idea/zed links; lists capped at 500 rows with a "refine" note; offline click-through in Chromium incl. evil and service B, 0 console errors; the main canvas is still the CFM-201 route map until CFM-203)
**Files:** `web/viewer.{js,css}`
**AC:**
- Endpoint and function lists with search (by path, name, or file).
- Keyboard: `/` focuses search, arrows move through the list, Enter selects.
- The detail panel shows all edge and node fields, plus the editor link when `editorRoot` is set.
- All text goes in through `textContent`.

### CFM-203: Call Tree view ✅ done 2026-11-13 (selecting from the list re-roots the tree, a tap in the tree only shows details; callees stop at depth 3 with a `+N` frontier, callers walk up to endpoints with no depth cap; hard cap 400 nodes with a note; filter and package choices persist across roots; package list is an overlay; the route map stays as the "All routes" overview; click-through on di-interface, flow, gin-split-routes, dynamic, wire-missing and service B, 0 console errors or warnings; Call Tree | Flowchart tabs come with CFM-204 so no dead tab ships)
**AC:**
- Depth 3 by default, and clicking expands.
- Back-references on cycles.
- Callers mode reaches endpoints.
- Filters for external, low-confidence and unresolved.
- Repeated callees deduped per parent with a `×N` badge. A package filter, with helper packages hidden by default (FR-20).
- A blocker banner when `stats.blockers` is non-empty (FR-19).
- Confidence shown by line style plus a label.

### CFM-204: Flowchart view ✅ done 2026-11-13 (Call Tree | Flowchart tabs in the main toolbar; an endpoint shows its handler's outline; `if`/`switch`/`select`/`loop` diamonds with `true`/`false`, `case …`/`default`, `loop`/`exit` and `↺` back edges; `other` exit when a switch or select has no default, because pruned cases may hide paths; return/panic pills plus start/end; a `+` on a call box expands the callee inline as a dashed cluster where `return` flows back to the caller, guarded against recursion; diamonds truncate to two lines and a tap shows the full text with an editor link; switching tabs keeps the selected node; click-through on flow, di-interface, dynamic, evil and service B, 0 console errors or warnings)
**AC:**
- Diamond, labelled-edge, loop-back and terminal rendering per ARCHITECTURE §5.
- Inline callee expansion works.
- Switching tabs keeps the selection.

---

## M3: Validate and release

### CFM-301: Real-repo validation 🟡 gates 2 and 3 pass, gate 5 waits on a person (2026-11-13; service A 5/5 after `wire .` on a copy, service B 5/5, no mock targets; the stale-`wire_gen.go` pattern became `WIRE_GEN_STALE` with fixture `wire-stale`; results and the empty gate-5 table in `docs/validation.md`)
**AC:**
- PRD §6 gates 2, 3 and 5 are run.
- Results go in `docs/validation.md`, with every miss listed by pattern.
- Misses with a common pattern become fix tasks. One-offs only get a diagnostic.

### CFM-302: Performance measurement ✅ (2026-11-13; lean SSA build shipped: 2.5–6.5x faster, 30–40% less RSS; the spike's loss came from its function filter, not missing bodies; numbers in `docs/validation.md`)
**AC:**
- Time, RSS and report size are recorded for both repos plus one large OSS Go service.
- If NFR-3 is missed, write down the bottleneck (load, VTA or the browser) and file a follow-up. Don't optimise speculatively.
- Find out why lean mode (SSA bodies only for module + router packages) loses repo reach on service A (42 → 7) but not on service B. If it can be made lossless, it's 7× faster with -40% RSS (`docs/spike-m0.md` §4).

### CFM-303: README usage + release ✅ (2026-11-13; README rewritten with install, prep, flags, exit codes, recognized patterns and a sharing note; tag `v0.1.0`. The module path stays `cfm`, so install is `go install ./cmd/cfm` from a clone until there is a remote)
**AC:**
- The README has install instructions (`go install …/cmd/cfm@vX`), the `go mod download` prerequisite, a privacy note on sharing reports, and the supported patterns table.
- A tag is cut.

## M4: Request body

### CFM-401: Request body schema, body-driven branches, try-a-body in the viewer ✅ (2026-11-13; `body` fixture with hand-written `body.expected.txt`; on a private Gin service: 41 bodies, 117 tests, 0 console errors; branch highlight checked in Chromium, dark + light, 390 px)
A handler often branches on a body field, sometimes several calls deep (`currency == "SGD"` goes to a partner, `"HKD"` stays local). The report should show the body an endpoint takes, mark the conditions that read it, and let the user paste a body to see which branches it takes.

**AC:**
- **Schema.** A function that binds a request body gets `body`: the bound Go type and its JSON fields (json tag names, kind, nesting, nullable). Binders: Gin `ShouldBindJSON`/`BindJSON`/`ShouldBind`/`Bind`, Fiber v2 `BodyParser`, `json.NewDecoder(r.Body).Decode` on an `*http.Request`.
- **Conditions.** An `if` condition, a `switch` case or `default` that compares a field of a bound type gets a `test`. This holds in any project function, because DTOs are passed on to usecases. Supported: `==`, `!=`, `<`, `<=`, `>`, `>=` against a constant (literal or named const), `&&`, `||`, `!`, bool fields, `strings.ToUpper`/`ToLower`/`TrimSpace` and `strings.EqualFold`. Anything else inside a test is `unknown`. A value copied into another struct, or read from a DB, is not traced, and its condition carries no test.
- **Viewer.** The panel of an endpoint whose handler binds a body shows a JSON box prefilled with a sample from the schema.
  - Valid JSON lists every body condition reachable from the handler, each with its result: true, false, the matching case, or can't tell.
  - In the flowchart, a taken branch is highlighted and an untaken one is dimmed. A branch whose result can't be decided keeps its normal look.
  - A missing field evaluates as Go's zero value.
  - Invalid JSON says so and highlights nothing.
- **Contract.** `GRAPH_SCHEMA.md` (2.1.0), `internal/graph` types, `Validate`, goldens and `viewer.js` change together. A new `body` fixture has hand-written expectations.

## M5: Other languages (ADR-108)

### CFM-501: `cfm render` ✅ (2026-11-17; `render` of an `analyze --json` graph is byte-identical to the `analyze` report; old schema major, unknown field, invalid graph and broken JSON exit 3 and write nothing)
Any analyzer that writes a valid graph JSON gets the same report as `cfm analyze`.

### CFM-502: TypeScript spike (time-boxed to 3 days) ✅ (2026-11-17; `nest-di` 11/11 hand-written edges, 0 decoy targets, provider-based DI resolves to one `medium` target; `cfm render` + viewer unchanged, 0 console errors; verdict "go once a real NestJS service is available"; details in `docs/validation.md`. No real TS service was named, so the result rests on the fixture)
Answer one question: can a call graph built on the TypeScript compiler API follow NestJS DI from controller to repository well enough to be worth a full analyzer?

**AC:**
- A `nest-di` fixture with a controller, a service behind an interface or injection token, and a repository.
- A throwaway `cfm-ts` writes a graph that `cfm render` accepts, with the route endpoint and the DI hop resolved.
- A note in `docs/validation.md`: what resolved, what did not, the schema changes needed, and a go/no-go for a full analyzer.
- Before starting, name a real TS service to validate against. Without one, the result rests on fixtures only.

### CFM-503: .NET spike (Roslyn, ASP.NET Web Site / ASMX) ✅ (2026-11-18; `asmx-legacy` all hand-written edges, 0 decoy targets, 0 compiler errors; on service C 416/416 entry points, 357 reach an outbound HTTP call, 0 calls the compiler couldn't bind; `cfm render` + viewer unchanged; verdict go; details in `docs/validation.md`)
Answer one question: can an analyzer built on Roslyn map a legacy .NET Framework service (ASMX `[WebMethod]`, `.ashx` handlers with a homegrown route table, `new`-wired controllers, outbound HTTP to an ESB) from entry point to the outbound call, without building or running the project?

**AC:**
- A synthetic `asmx-legacy` fixture under `spike/cfm-dotnet/testdata/` (no code copied from any real repo) with an `.asmx` service, an `.ashx` handler mounted from `Global.asax`, a controller reached by `new`, a call through an interface with one unwired decoy implementation, and an outbound `HttpWebRequest`. Expected edges are written before the analyzer runs.
- A throwaway `cfm-dotnet` (C#, Roslyn, no MSBuild, no build of the target) writes a graph that `cfm render` accepts unchanged.
- Run on a real private service (service C). The report stays outside the repo. Only counts and patterns go into `docs/validation.md`, never names from the private code.
- A note in `docs/validation.md`: what resolved, what did not, the schema changes needed, and a go/no-go for a full analyzer.

### CFM-504: `cfm analyze` dispatches to a language analyzer (one command) ⏸ waits on CFM-503 go
`cfm analyze <dir>` on a directory without `go.mod` but with `.sln`/`.csproj`/`.asmx`/`App_Code` runs `cfm-dotnet` from `PATH` and renders its graph. Needs a new ADR, because ADR-108 has `cfm` never start another tool.

---

## Definition of done (every task)

- `gofmt`, `go vet`, `staticcheck` and `go test -race ./...` are clean.
- Any golden diffs are reviewed in the PR.
- No TODO stubs, skipped tests or `.only` left behind.
- A completion note lists the files changed, the AC mapping (pass/fail), and the commands run with their real output.
