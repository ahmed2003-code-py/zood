# System diagrams

[Archify](https://github.com/tt-a1i/archify) sources. Each box cites the file and lines it comes
from, checked against the commit pinned in `meta.repository.revision`. How to render, update and
add one: the `zawed-code-map` skill (§5).

| File | Type | Shows |
|---|---|---|
| `modules.archify.json` | tree | The API's Spring Modulith modules grouped by role (money, master data, access, offline and support, shared kernel) and the web app's main areas |

Render locally (output goes to the ignored `.archify/`):

```bash
A="C:/Users/ae11w/.darsly-tools/archify/archify/bin/archify.mjs"
ARCHIFY_UPDATE_CHECK_DISABLED=1 node "$A" finalize tree \
  docs/architecture/diagrams/modules.archify.json .archify/modules.html \
  --repo-root . --quality showcase --json
```

For calls and dependencies between classes (who calls what), use the code graph instead:
`graphify-out/graph.html`, built locally by Graphify.
