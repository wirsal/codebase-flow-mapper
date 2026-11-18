# Codebase Flow Mapper (CFM)

**Version:** 0.3.0 · **Language:** Go only · **Routers:** Gin, net/http, Fiber v2

CFM is a single Go binary. Point it at a Go module and it writes one self-contained HTML file. Open the file and you can:

- pick an HTTP endpoint and follow middleware → handler → service → repository, **through interfaces wired by DI**;
- pick any function and see its callees, or its callers back up to the endpoints that reach it;
- view a call path as a **Call Tree** or as a **Flowchart** with branches, loops and returns.

There is no server, database, account, AI or network call, and CFM never runs any of the target project's code.

![Call Tree of POST /tx: the handler calls the service through an interface, and the service reaches the Postgres repository](docs/img/call-tree.png)

*Call Tree of `POST /tx` (fixture `testdata/fixtures/di-interface`). The handler calls `txService.Create` through an
interface. The dashed `medium` edge marks an interface call that VTA resolved to the wired implementation, and it
continues to `pgRepo.Save`.*

## Install

Requires Go 1.26 or newer.

```bash
go install github.com/fikrigatrh/codebase-flow-mapper/cmd/cfm@v0.3.0
cfm version
```

From a clone, `go install ./cmd/cfm` does the same.

### Optional: Mac app

```bash
mkdir -p dist && osacompile -o dist/CFM.app macos/CFM.applescript
```

Drag a service folder (the one with `go.mod`) onto `CFM.app`, or open the app and pick a folder. It runs
`cfm analyze` and opens the report, which it writes to `~/cfm-reports/<folder>.html` and never into the repo. The app
only wraps the CLI, so it still needs Go and `cfm` installed as above, and the service prepared as below. Because the
app is unsigned, open it the first time with right-click → Open.

## Prepare the service you want to analyze

CFM runs with `GOPROXY=off` and never runs generators, so the checkout has to be complete before you analyze it:

```bash
cd my-service
go mod download     # every dependency must be in the module cache
wire .              # if the service uses Wire (in the directory that holds wire.go)
swag init           # if the code imports a generated swag docs package
```

The Wire step matters most. Without a current `wire_gen.go`, CFM can't see which concrete types reach the
interfaces, and calls stop at the interface. That happens both when `wire_gen.go` is gitignored and when it was
generated before a provider changed. CFM detects both, shows a blocker banner at the top of the report, and prints the
command to run.

## Run

```bash
cfm analyze ./my-service -o flow.html
open flow.html
```

| Flag | Meaning |
|---|---|
| `-o report.html` | Report to write (required) |
| `--json graph.json` | Also write the graph as JSON (`GRAPH_SCHEMA.md`) |
| `--exclude glob` | Skip more packages, by relative path or last path element. Repeatable. `*fakes`, `*mocks`, `mock*` and `testutil*` are always skipped, and so is `vendor/` |
| `--editor vscode\|idea\|zed` | Make source locations open in your editor |

`cfm render graph.json -o report.html` builds the same report from a graph JSON, such as one written by `--json`
or by another analyzer that follows `GRAPH_SCHEMA.md`. A graph that doesn't validate exits with code 3.

Point `cfm` at the directory with the module's `go.mod`. In a multi-module repo, that is the service's own directory
(for example `services/orders/v2`), not the repo root.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Report written |
| 1 | Report written, but the target has load or type errors, so the graph is partial (see the diagnostics) |
| 2 | CFM bug. Nothing was written |
| 3 | Usage error |

Typical run: 2–3 s and under 2 GB RSS for a service with 50–200 endpoints (`docs/validation.md`).

## What it recognizes

| Router | Registrations |
|---|---|
| Gin (`github.com/gin-gonic/gin`) | `GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS/Any/Handle/Match`, `Group`, `Use` on `*gin.Engine`, `*gin.RouterGroup`, `gin.IRouter` and `gin.IRoutes`. A router passed across functions and files is followed. |
| net/http | `http.Handle/HandleFunc`, `(*http.ServeMux).Handle/HandleFunc`, `ServeHTTP` types, `HandlerFunc(f)`, wrapper middleware, Go 1.22 patterns (`"GET /items/{id}"`) |
| Fiber v2 (`github.com/gofiber/fiber/v2`) | `Get/Post/Put/Patch/Delete/Head/Options/All/Add`, `Group`, `Use` on `*fiber.App` and `fiber.Router` |

| Call pattern | Result |
|---|---|
| Direct call or method call | `static` edge, high confidence |
| Interface call whose concrete type is built in the analyzed code (constructors in `main`, `wire_gen.go`, providers) | `vta` edge to the real implementation. Confidence drops as the number of candidate types grows |
| Counterfeiter-style fakes or mocks | Never a target: those packages are excluded |
| Non-constant path, router or handler in a map, func value chosen at runtime | Shown as unresolved, with a diagnostic. Never dropped silently |
| Fiber v3 or other routers | `ROUTER_UNSUPPORTED` diagnostic |

### Other languages

Only Go is supported. Two experiments write a graph for `cfm render`. Neither is a supported tool yet:

- `spike/cfm-ts/` reads a NestJS project.
- `spike/cfm-dotnet/` reads a C# .NET Framework web app (ASMX `[WebMethod]`s, `.ashx` route tables, `Global.asax`,
  `static Main`) with Roslyn. It never builds or runs the project.

  ```bash
  dotnet build spike/cfm-dotnet/src          # also downloads the .NET Framework reference assemblies
  dotnet spike/cfm-dotnet/src/bin/Debug/net8.0/cfm-dotnet.dll ./MyWebSite -o graph.json
  cfm render graph.json -o report.html
  ```

Results and gaps are in `docs/validation.md`. The decisions are ADR-108 and ADR-109.

### Trying a request body

![Flowchart of POST /v1/topup with the body {"source":{"currency":"SGD"}}: the SGD case of the currency switch is highlighted](docs/img/request-body-flowchart.png)

*Flowchart of `POST /v1/topup` (fixture `testdata/fixtures/body`) with a body whose currency is `SGD`. The
`express && value > 1000` branch and the `case dto.CurrencySGD, "MYR"` branch inside the expanded
`(*Service).Topup` are amber. The other cases are dimmed.*

When a handler binds a JSON body (Gin `ShouldBindJSON`/`BindJSON`/`ShouldBind`/`Bind`, Fiber `BodyParser`,
`json.NewDecoder(r.Body).Decode`), the endpoint's detail panel shows a JSON box prefilled with the body's fields. Edit
it, and the panel lists every condition reachable from the handler that compares a body field with a constant, such as
`req.Currency == "SGD"` or `switch strings.ToUpper(req.Currency)`, with its result for that body. In the Flowchart, the
branch the body takes is highlighted and the others are dimmed.

This only works when the condition reads the bound struct itself, or a struct nested in it, even several calls deep.
Some conditions show as "can't tell" and keep all their branches:

- the value was copied into another struct;
- the value comes from a database or config;
- the field is compared with a variable;
- the choice goes through a map.

The graph comes from static analysis. It is not a runtime trace and doesn't prove that a branch or call runs for a
given request.

## Sharing a report

**A report is a copy of your code's structure.** It contains function names, file paths and line numbers, route
paths, and branch conditions taken from the source, up to 120 characters each. The HTML has no network access, but
whoever gets the file can read all of that. Share it only where you would share the repository itself.

## Documents

| Document | Purpose |
|---|---|
| `PRD.md` | Scope, requirements, acceptance criteria |
| `ARCHITECTURE.md` | Pipeline, analysis rules, viewer, safety, testing |
| `GRAPH_SCHEMA.md` | The JSON graph embedded in the report |
| `ADR.md` | Decisions, including what was cut and why |
| `SPRINT_BACKLOG.md` | Milestones and tasks |
| `docs/validation.md` | Results on real repos, and performance |
| `AGENTS.md` | Instructions for AI agents and contributors |
