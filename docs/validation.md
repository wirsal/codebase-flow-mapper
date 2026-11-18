# Real-repo validation (CFM-301)

Run on 2026-11-13 with `cfm 0.1.0-dev`, with the safety env pinned (ADR-105). Two private, production Gin + Wire
services were used. They are called **service A** and **service B** here: their code is not public, so function names,
file paths, routes and partner names are left out on purpose. The original repos were read only, and every generator
ran on a copy. Sampling is reproducible on any service: `python3 docs/sample_endpoints.py graph.json` picks 5
endpoints with seed 301 and prints the BFS path from each handler to repo-layer functions.

| Repo | Module dir | State analyzed | Endpoints | Functions | Edges | Unresolved | Time | RSS |
|---|---|---|---|---|---|---|---|---|
| service A | `v2/` | as checked out (a release branch) | 50 | 1070 | 17384 | 387 | 5.8 s | 3.0 GB |
| service A | `v2/` | copy, `wire .` regenerated | 50 | 1072 | 17489 | 26 | 5.7 s | 2.9 GB |
| service B | root | copy, `swag init` + `wire` (spike M0) | 32 | 265 | 1801 | 6 | 9.9 s | 3.1 GB |

## Gate 2: handler → repository through interfaces (≥ 4/5 per repo)

Each path below was checked by hand against the source: the call site line, the interface the call goes through, and
that the VTA target is the implementation actually wired (no alternatives on any of these edges).

**Service A (wire regenerated): 5/5.**

- 4 endpoints reach their repositories in 2–3 interface hops:
  - In one case VTA picked the right repository out of 7 types that have a method with the same name.
  - Two endpoints fan out to 23 and 26 more repository calls.
  - One endpoint reaches a repository that lives in a shared SDK module.
- The 5th endpoint (a partner callback) correctly reaches no repository: its usecase body is `return nil`.

**Service B: 5/5.** All five go handler → usecase (static) → repository interface field → the single repository
implementation (vta), at 1 to 6 repository call sites per endpoint.

**Gate 2: pass.**

## Gate 3: no fake/mock implementation as a call target

No node ID in either graph contains `mock`, `fake` or `stub`, so none can be a call target. Neither repo has mock types
outside `_test.go` files, and CFM never loads tests. **Pass.**

## Gate 5: "what does X do" with and without the report

**Not run yet.** It needs an engineer who doesn't know the service under test.

**Setup (the person running the test):**

1. On a copy of the service, run `go mod download`, then `wire .`, then
   `cfm analyze . -o report.html --json graph.json`.
2. Run `python3 docs/gate5_kit.py graph.json > answers.md`. It picks 6 endpoints (seed 305) that reach 2–10 repository
   methods each, splits them into set A and set B, and writes the answer key.
   - Spot-check the key against the source before running the test.
   - Keep `answers.md` out of the repo, and away from the engineer.
3. Give the engineer the service checked out in their editor. Don't give them `report.html` until set B.

**Questions (the same for every endpoint):**

- **Q1.** Which service/usecase method does the handler hand the work to?
- **Q2.** Which repository methods can this endpoint reach?
- **Q3.** Which outside systems can it touch: database, an HTTP service, cache?

**Running it:**

1. **Set A:** answered without the report, using the editor and grep only.
2. **Set B:** answered with `report.html`. The editor is allowed too.
3. Time each set from the moment its endpoints are read out until the engineer says "done".
4. Score the answers against the key. For Q2, missing one repository call out of 5 or more still counts as correct.
5. If a second engineer is available, swap the sets for them. That rules out "set B was just easier".

**Gate 5 passes** if set B is faster with the same or better score.

Record the result here:

| Engineer | Service | Without report (set A) | With report (set B) | Score A / B | Notes |
|---|---|---|---|---|---|
| | | | | | |

## Misses, grouped by pattern

### Stale `wire_gen.go` (pattern: 2 of 2 repos as checked out) → fixed

Neither repo works as checked out. Service B has `wire_gen.go` gitignored (already reported as `WIRE_GEN_MISSING`). Service A
on the current branch has a `wire_gen.go` that no longer type-checks: provider signatures changed (7 type errors) and
it imports 3 in-module packages that were deleted. The root package then drops out of the call graph, and every
interface call becomes `unresolved` (387, against 26 after `wire .`). Before the fix, CFM reported this as three
`GENERATED_PKG_MISSING` blockers telling the user to run `go generate` on directories that don't exist, which is wrong.

Fix (shipped with CFM-301):

- New `WIRE_GEN_STALE` diagnostic, listed in `stats.blockers`, when a `wire_gen.go` has type errors: `fix: "wire ."`.
- A missing in-module import that only `wire_gen.go` needs no longer produces `GENERATED_PKG_MISSING`.
- Fixture `testdata/fixtures/wire-stale` covers both.

Service A as checked out now shows a single blocker: `run "wire ."`.

### One-offs (diagnostic only)

- **Service A, 3 `HANDLER_UNRESOLVED`:** middleware that are func values chosen at runtime.
- **Service A, 1 `ROUTE_PREFIX_UNKNOWN`:** a router handed to a constructor CFM doesn't track.
- Each already has its diagnostic, and none of the sampled endpoints depends on them.

## Performance (CFM-302)

Measured with `/usr/bin/time -l` on an M-series Mac. "Open" is the time from navigation to a populated list, and
"select" is the time to the first tree after a click (Chromium via Playwright, best of 2 runs).
apache/answer is `v1.4.1` (`c2ad8c6`, about 70k LOC, Gin + Wire). Its dependencies were downloaded into a scratch
`GOMODCACHE`.

| Repo | Endpoints / functions | Full build: time, RSS | Project-only build: time, RSS | Report | Open / select |
|---|---|---|---|---|---|
| service A (wire regenerated) | 50 / 1072 | 5.1 s, 2.53 GB | **2.3 s, 1.69 GB** | 12 MB → 7.3 MB | 0.10 s / 0.09 s |
| service B | 32 / 265 | 11.8 s, 3.19 GB | **1.8 s, 1.83 GB** | 1.8 MB | 0.05 s / 0.08 s |
| apache/answer v1.4.1 | 201 / 1778 | 6.3 s, 2.77 GB | **1.8 s, 1.93 GB** | 9.0 MB | 0.18 s / 0.11 s |

NFR-3 (< 60 s, < 4 GB, opens in < 3 s) is met on all three, so no bottleneck was filed. Memory is still the biggest
cost. It is dominated by `go/packages` loading syntax and types for every dependency, which SSA needs.

### Why the spike's lean mode lost repo reach (42 → 17 on service A)

It was not the missing dependency bodies. The spike also deleted every function outside the module from the VTA input,
and its `inModule` check treats synthetic wrappers (`Pkg == nil`) as outside. Service A's usecases have value receivers
stored as pointers (`&usecase{}` behind an interface), so every call goes through a synthetic `(*usecase).M` wrapper,
and the type flow stopped there. Service B uses pointer receivers, which explains why it was unaffected. With bodies built
only for module packages but no function filter, reach stays at 42 in 1.7 s.

CFM never had that filter. It now builds SSA bodies only for the loaded project packages (`internal/load`). Diffing
the full-build graph against the project-only graph:

- **Service B and apache/answer:** identical (every node, edge and diagnostic).
- **Service A:** project nodes (including flows), diagnostics and the unresolved count (26) are identical. 8,608 edges
  are gone:
  - 8,570 point at external functions. Almost all of them are the low-confidence fan-out of `err.Error()` sites, which
    had 253 candidate types.
  - 34 are `err.Error()` → a project error type, which was one of those 253 low-confidence candidates. At 26
    of those sites the call now reads `builtin error, implementation not tracked`; at 8 it stays as a medium-confidence
    VTA edge.
  - The remaining 4 are at the `ResponseWriter.Write` sites, which lose gin's own writer, so their candidates drop from 3 to 2.
  
  No gate-2 path changes, because none of them goes through `error.Error` or `Write`.

Known ceiling: a project value that only reaches a call through a dependency's body loses its VTA type, and that call
shows up as unresolved instead. Examples are a project type stored in a library struct, or one handed back through a
library callback. None of the three repos hit this.

## TypeScript spike (CFM-502, ADR-108)

The question: without VTA, can a TypeScript analyzer follow NestJS DI from controller to repository well enough to be worth building?

**Setup.** `spike/cfm-ts/cfm-ts.mjs` (350 lines, TypeScript 5.9.3 compiler API, plain JS, no build step) on fixture `spike/cfm-ts/testdata/nest-di` (kept out of `testdata/fixtures`, which the Go tests walk). The fixture covers DI by concrete class, by an abstract class used as token (`{ provide: TxRepo, useClass: PgTxRepo }`), and by an interface with a `Symbol` token (`@Inject(NOTIFIER)`). It also has one unwired decoy implementation for each of the two abstractions. The expected edges in `nest-di.expected.txt` were written before the analyzer ran. Nest itself is not installed, which matches a checkout without `npm ci`.

**Result.**

| Check | Result |
|---|---|
| Hand-written edges found | 11 / 11, including both `/api` global-prefix routes |
| Decoy (`MemoryTxRepo`, `SmsNotifier`) as call target | 0 |
| DI hop with a provider in `@Module` | 1 target, `medium`, reason names the provider and its line |
| Same code with the providers removed | Falls back to the class hierarchy: 2 targets each, `low`, `alternatives: 2` |
| `cfm render` | Accepts the graph. Call Tree and Flowchart work with no viewer change, 0 console errors |
| Time | 194 ms for 11 files |
| Missing `@nestjs/*` | One `TS_MODULE_MISSING` warning per package with fix `npm ci`. DI still resolves, because it only needs project types |

**Why it worked better than feared.** Nest DI is declarative: `@Module({ providers })` maps every token to a class. A provider map built from the AST does for Nest what VTA does for Wire. The risk named in ADR-108 holds only for code without a DI container, such as plain Express, where calls through an interface fall back to the class hierarchy and are marked `low`.

**Schema changes a real analyzer needs** (not made; the spike reused existing values):
- `edge.method` needs `di_provider` (the spike used `framework_registration`) and `cha` (the spike used `vta` with the reason saying "class hierarchy").
- `goVersion` → a `language` field plus the toolchain version.
- Function IDs `fn:<file without extension>.(Class).method` work as they are. Overloads share one implementation, so they collapse to one node.

**Not covered by the spike:**
- `useFactory` / `useValue` / `useExisting`, dynamic modules (`forRoot`), and module scoping (the spike uses one global provider map);
- guards, interceptors and pipes as `uses_middleware`;
- Express and Fastify routes, and the request body (DTO class + class-validator);
- calls inside arrow functions are attributed to the enclosing method;
- `new` expressions, hierarchies deeper than one level, and overrides in subclasses.

**Verdict: go, on one condition.** The core risk is answered on a fixture. A full `cfm-ts` is worth starting only when a real NestJS service is available to validate against, because every number above comes from a fixture written by the same person as the analyzer.

## .NET spike (CFM-503, ADR-108, ADR-109)

The question: can an analyzer built on Roslyn map a legacy .NET Framework service from entry point to its outbound
call, without building or running it?

**Setup.** `spike/cfm-dotnet/` (C#, Roslyn 4.14, about 1,100 lines, `net8.0` with roll-forward, run on SDK 11.0.100)
on the synthetic fixture `spike/cfm-dotnet/testdata/asmx-legacy`. The fixture has an `.asmx` service, an `.ashx`
handler with a homegrown route table mounted from `Global.asax`, controllers reached by `new`, an interface field
assigned only `new SmsNotifier()` with an unwired `EmailNotifier` decoy, an interface returned by a factory with a
never-created `FileAuditLog` decoy, an outbound `HttpWebRequest`, and a `Backup/` folder with a second copy of a class.
The expectations in `asmx-legacy.expected.txt` were written before the analyzer ran. `dotnet test spike/cfm-dotnet/tests`
checks them, the §7.3 site/edge match and byte-stable output.

**Fixture result.**

| Check | Result |
|---|---|
| Hand-written edges, bodies and body tests | all found (`dotnet test`: 3/3) |
| Decoys (`EmailNotifier`, `FileAuditLog`) as call target | 0 |
| Interface field assigned only `new T()` | 1 target, `medium`, reason names the assignment and its line |
| Interface from a factory | 1 target, `medium`, by Rapid Type Analysis (only `DbAuditLog` is ever created) |
| `Backup/` | not loaded, 0 duplicate-type errors |
| Compiler errors with the net461 reference assemblies | 0 |
| `cfm render` | accepts the graph with no Go or viewer change |

**Real service.** Service C is a private ASP.NET Web Site project on .NET Framework 4.6.1 (no `.csproj`, `App_Code/`,
two `.asmx` services, one `.ashx` REST handler). It was read in place, and the report stayed outside the repo. As in
the sections above, names from its code are left out.

| Files | Entry points | Functions | Edges | Unresolved | Time (analysis / wall) | RSS |
|---|---|---|---|---|---|---|
| 630 `.cs` | 416 | 2061 | 14505 | 660 | 3.5 s / 5.3 s | 347 MB |

- **Entry points: 416 of 416.** That's 412 `[WebMethod]`s (376 + 36, counted with grep), 3 route-table routes under the
  `/api` mount from `Global.asax`, and `Application_Start`.
- **Edges.** 13,428 `static`/`high`, 416 entry-point edges, 1 interface call resolved to its one assigned
  implementation, and 660 `unresolved`. The code builds every controller with `new`, so nearly everything resolves
  statically.
- **Unresolved.** 657 of the 660 are calls on `dynamic` values (JSON responses read as `dynamic`), labelled
  `reflection` with the reason "bound at runtime". The other 3 are delegate calls inside the route table. 0 calls are
  ones the compiler couldn't bind, and there are 0 compiler errors.
- **Reach.** 357 of 416 entry points reach an outbound HTTP call (`WebRequest.Create`, `HttpWebRequest.GetResponse`).
  This is the ESB hop the flow ends at.
- **Bodies.** 319 entry points get a request body from their parameters, and 133 conditions get a body test.
- **Not done:** the gate-5 sampling of endpoints by a person who knows the service (CFM-301 style).

**Schema changes a real analyzer needs** (not made; the spike reused existing values):
- `edge.method`: `cha` and `rta`. The spike writes `vta` and says which one in the reason.
- `unresolved.reason`: `dynamic` (the spike uses `reflection`).
- A `try` flow step. The spike shows each `catch` as an `if` on "the exception was thrown".
- `goVersion` → `language` plus toolchain (the spike writes `dotnet net461` into `goVersion`).
- Function IDs carry parameter types, `fn:<ns>.(<Type>).<Name>(<params>)`, because C# has overloads. This is a
  format, not a field change.
- Entry points that aren't HTTP use `method` values `EVENT` and `MAIN`. The viewer shows them as it would a verb.

**Not covered by the spike:**
- WebForms pages (`.aspx` events);
- Web References (SOAP proxies) linked across projects to the `[WebMethod]` they call;
- WCF `.svc`, MVC and Web API controllers, ASP.NET Core and its DI container;
- SSIS packages;
- property getters and setters as call targets;
- calls inside lambdas, which are attributed to the enclosing method;
- constructors of external types (`new SqlCommand(...)`), which aren't call steps.

**Verdict: go.** A static analyzer over Roslyn works on a real legacy service as checked out: no build, no restore of
the target, and nothing from it runs. Next are CFM-504 (one `cfm analyze` command), then WebForms plus Web References,
so a page click can be followed into the API.
