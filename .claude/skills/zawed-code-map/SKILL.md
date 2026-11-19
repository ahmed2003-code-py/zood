---
name: zawed-code-map
description: ZAWED's map of its own code — a Graphify knowledge graph of every file, class, method, import and call in apps/api (Java) and apps/web (TypeScript) (graphify-out/graph.json), and Archify diagrams of the system (docs/architecture/diagrams/), including tree diagrams of the modules. Use it FIRST whenever you need to know how the code connects before changing or explaining it — "who uses X", "what calls Y", "what breaks if I change Z", "where does this flow go", "which module owns this", "how do the fueling engine and the ledger connect", or at the start of any non-trivial feature, refactor or bug hunt across modules — and skip it for exact-string lookups, where Grep is faster. Also use it when the user asks for a diagram, a tree or a picture of the system, a module, a flow or the database.
---

# ZAWED code map — ask the graph before reading the repo

The graph knows every import, reference, method and file with its exact line. Ask it first,
then read only the files it points at.

## 1. When the graph is the faster road

| Question | Use |
|---|---|
| Who calls / references / imports `X`? What does `X` depend on? | `graphify explain "X"` |
| What is affected if I change `X`? | `graphify explain "X"` (incoming `<--` edges), then `query` |
| How do `A` and `B` connect? | `graphify path "A" "B"` (add `--undirected` if no directed path) |
| Everything around a concept ("offline sync", "maker-checker") | `graphify query "<question>"` |
| Which modules exist and how big they are | `graphify-out/GRAPH_REPORT.md` (community hubs) |
| An exact string, an error code, an i18n key, a route path, a SQL column | **Grep** — faster than the graph |
| What a method actually does, line by line | **Read** the file — the graph knows links, not logic |

## 2. Running it

```bash
G="C:/Users/ae11w/AppData/Roaming/Python/Python312/Scripts/graphify.exe"
export GRAPHIFY_BACKEND=none GRAPHIFY_QUERY_LOG_DISABLE=1
"$G" explain "FuelingEngine.java"                 # a file node and everything it touches
"$G" query "what calls Ledger"                    # BFS around the matching nodes
"$G" path "OfflineSync" "apps/api/src/main/java/com/zawed/api/ledger/Ledger.java::Ledger" --undirected
```

Run from the repo root. Code is parsed locally with tree-sitter; nothing leaves the machine.
Each answer cites `path:Lnn` — open those, not the whole folder.

**Ambiguous names** (exit code 3): the answer lists the candidates. Retry with `path::Symbol`
(`apps/api/src/main/java/com/zawed/api/ledger/Ledger.java::Ledger`) or the full node id it prints.
A Java class and its constructor share a name, so a class name alone is often ambiguous: use the
file name (`FuelingEngine.java`) or the id.

## 3. Keep it fresh

It refreshes itself three ways, so normally it is current:
- git hooks (`graphify hook install`): after every commit, checkout, merge and rebase;
- the project Stop hook `.claude/hooks/graph-refresh.mjs`: after a turn that changed code under `apps/`;
- by hand when needed: `"$G" update .` (incremental, ~20 s).

Before trusting it for a decision, compare `GRAPH_REPORT.md` ("Built from commit") with
`git rev-parse HEAD`, and run `update` if you changed code in this turn. Full rebuild if the graph
is missing or broken (~30 s):

```bash
PYTHONHASHSEED=0 "$G" extract . --code-only && "$G" cluster-only . --backend=none
```

`graphify-out/`, `.graphifyignore` and `.archify/` are local (listed in `.git/info/exclude`), never
committed; every machine builds its own. The "Unknown backend 'none'" labelling warning during
`cluster-only` is expected: communities get numbered names instead of AI names.

## 4. What the graph does not know

- **HTTP between web and API.** `apps/web` calls the API by URL through `src/api/http-client.ts`
  (`/api/v1/...`, typed by `src/api/generated/schema.d.ts` from `apps/api/openapi/openapi.json`),
  not by import, so there is no edge from a screen to its controller. Bridge it with Grep: the path
  string in `apps/web/src/api/http-api.ts` or `src/live/**` ↔ `@RequestMapping`/`@GetMapping`/
  `@PostMapping` in `apps/api/src/main/java/com/zawed/api/**/*Controller.java`.
- **SQL.** Flyway migrations (`apps/api/src/main/resources/db/migration/V*.sql`) and RLS policies
  yield few nodes. Use Grep and `docs/architecture/database-erd.md`.
- **Module rules.** Spring Modulith boundaries are enforced by `ModularityTests`, not by the graph.
- **Runtime facts**: environment variables, Railway services, data. Those live in `docs/adr/0014-deployment.md`,
  the diagrams below and (read-only) Railway.
- **Logic** inside a method. Read the code before you change it.

## 5. The diagrams — `docs/architecture/diagrams/`

Archify sources, committed, each box citing its file and lines. The README in that folder lists
them. Render one (output goes to the ignored `.archify/`):

```bash
A="C:/Users/ae11w/.darsly-tools/archify/archify/bin/archify.mjs"
ARCHIFY_UPDATE_CHECK_DISABLED=1 node "$A" finalize <type> \
  docs/architecture/diagrams/<name>.archify.json .archify/<name>.html \
  --repo-root . --quality showcase --json
```

`<type>` is the diagram's mode: `architecture`, `tree`, `workflow`, `sequence`, `dataflow`,
`lifecycle`, `erd`, `class`, `timeline`, `waterfall`. If the tool is missing, re-clone
`https://github.com/tt-a1i/archify` into `C:/Users/ae11w/.darsly-tools/archify`. Read its
`archify/SKILL.md` and `archify/references/` (authoring defaults, repository authoring) before
writing a new diagram.

Rules that bite here:
- **Box labels in English.** The box font is monospaced and breaks Arabic letter joining; keep
  Arabic for `meta.title` and `cards`.
- **Evidence is checked against a commit.** `meta.repository.revision` must be a commit that
  contains every cited line. Update a diagram after the code it describes is committed: pin that
  commit, run `finalize`, and commit the `.archify.json` with the next commit.
- A new diagram (a money flow, the sync protocol, the ERD…) goes in the same folder with the same
  naming and a row in its README.

## 6. Report

When the graph answered a question, say so in one line ("from the code graph: `X` is used by …")
so the user knows where the answer came from. If the graph was stale and you updated it, say so.
