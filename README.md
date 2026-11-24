# HARscope

A local MCP server for token-efficient investigation of HAR (HTTP Archive) files. AI agents query captures in small,
focused steps (*progressive disclosure*: summary → timeline → search → one entry → part of a body), and the server
returns only the requested data in bounded responses instead of sending the entire HAR file at once.

HARscope is a *data access layer*, not an LLM-based analyzer. The calling agent interprets the results.

Version **0.3.0**, 10 tools:

| Category | Tools |
|---|---|
| Files | `har_list`, `har_open` |
| Summaries and timelines | `har_overview`, `har_trace_flow` |
| Search and inspection | `har_search`, `har_get_entry`, `har_query_body` |
| Comparison | `har_compare_entries`, `har_compare_captures` |
| Reproduction | `har_repro` (templates only; no requests are sent) |

## Architecture

```text
src/
  HarScope.Core/          core library with no MCP dependency (reusable for a CLI)
    Security/PathGuard    path validation: only .har files under HAR_ROOT; rejects "..", absolute paths, and links outside root
    Security/Redactor     redacts sensitive header, cookie, and query-string values
    Parsing/HarParser     HAR 1.2 validation + metadata indexing
    Indexing/CaptureStore in-memory capture cache (LRU), capture IDs, file change detection → reload
    Querying/             OverviewService, SearchService, EntryInspector, BodyReader (body decoding),
                          JsonPath (JSONPath subset), WhereClause (row filtering), BodyQueryService,
                          JsonBodyCache (LRU cache of parsed JSON bodies), OutputGuard (output limits)
    Analysis/             FlowService (timeline), CompareService + JsonDiff (diffs), ReproService (reproduction templates)
  HarScope.Mcp/           MCP stdio host (thin adapter over Core) + ServerGuide (client instructions)
tests/
  HarScope.Core.Tests/    Core unit tests (synthetic HAR fixtures)
  HarScope.Mcp.Tests/     tool tests + end-to-end tests using the official MCP client over stdio
scripts/
  install.ps1            test → publish to staging → replace locked files → verify
  bench/Bench.cs         synthetic large-HAR benchmark (.NET 10 file-based app)
samples/checkout-flow.har synthetic HAR: login → profile → search → cart → checkout → failed payment
```

Data flow:

```text
.har file (under HAR_ROOT)
  → PathGuard (canonical path, link checks)
  → HarParser: JsonDocument + per-entry metadata index (ID, time, method, host, path, status, duration, MIME type,
    body availability, correlation headers)
  → CaptureStore (up to N captures in memory, automatic reload when files change)
  → Overview / Flow / Search / EntryInspector / BodyQuery / Compare / Repro
    (bodies are decoded and parsed only when requested, then cached in JsonBodyCache)
  → Redactor + OutputGuard → compact JSON for the agent
```

### Capture IDs and entry IDs

- `captureId` is a slug of the relative file path: lowercase, non-alphanumeric characters replaced with `-`, without `.har`.
  `Checkout Failed 23-11.har` → `checkout-failed-23-11`; `team/login.har` → `team-login`. If two files produce the same slug,
  the file opened later receives a `-2` suffix.
- `entryId` = `<captureId>:entry-<n>`. `n` is the original position (starting at 1) in the `log.entries` array.
- Results include a `revision` (a short hash of the file contents). If the file changes on disk, the capture is reloaded
  and the agent receives a warning that old entry IDs may refer to different requests.

### How the server runs

The transport is stdio. Each MCP client session (for example, a Claude Code session) starts its own child process.
The process stays alive while the session is open and waits for messages on stdin without busy-waiting. The server opens no ports.
As a result, the HAR cache and redaction fingerprint key are scoped to each session, and the executable remains locked
while a session is using it (see `scripts/install.ps1`).

## Prerequisites

- .NET SDK 10 (tested with 10.0.402).
- No database, cloud service, or internet access is required by the server at runtime.

## Build, test, and install

```powershell
dotnet build
```

```powershell
dotnet test
```

Install or update to `%USERPROFILE%\.mcp-servers\harscope`. The script runs tests, publishes to staging, stops HARscope
processes that lock the destination files, copies the files, and verifies their hashes:

```powershell
.\scripts\install.ps1
```

For the first installation, also register with Claude Code at user scope:

```powershell
.\scripts\install.ps1 -Register -HarRoot D:\network-logs
```

HARscope processes stopped during an update are started again by Claude Code. Updated tool definitions become visible
in a new session.

The published executable is *framework-dependent*, so the target machine needs the .NET 10 runtime. Avoid `dotnet run`
in MCP configuration: it rebuilds whenever the client starts, adding startup time and potentially writing build output to stdout.

## Manual MCP client setup

All examples use stdio. Replace `D:\network-logs` with the folder where you store `.har` files (escape backslashes as `\\` in JSON).

**Claude Code (CLI):**

```powershell
claude mcp add harscope --scope user --env HAR_ROOT=D:\network-logs -- C:\Users\<user>\.mcp-servers\harscope\HarScope.Mcp.exe
```

**`.mcp.json` (Claude Code, project scope):**

```json
{
  "mcpServers": {
    "harscope": {
      "type": "stdio",
      "command": "C:\\Users\\<user>\\.mcp-servers\\harscope\\HarScope.Mcp.exe",
      "env": { "HAR_ROOT": "D:\\network-logs" }
    }
  }
}
```

**Claude Desktop (`claude_desktop_config.json`), Cursor, and other clients** use the same `mcpServers` format
(`command` + `env`).

On connection, the server sends **instructions** (a short guide for the model): when to use HARscope, the recommended
tool sequence, and guidance against reading `.har` files directly. The text is in
[ServerGuide.cs](src/HarScope.Mcp/ServerGuide.cs).

**Try the sample:** point `HAR_ROOT` to this repository's `samples` folder, then ask the agent to open `checkout-flow.har`.

### Exporting a HAR from your browser

Chrome/Edge: DevTools → **Network** tab → enable **Preserve log** → perform the workflow → right-click the request list →
**Save all as HAR with content**. Save the file in `HAR_ROOT`. Recent Chrome versions offer both sanitized HAR exports
(without cookies/authorization) and complete exports. HARscope can read either.

## Configuration

Settings are read from environment variables. Alternatively, use command-line arguments (`--HAR_ROOT D:\logs`) or
an `appsettings.json` file next to the executable.

| Variable | Default | Description |
|---|---|---|
| `HAR_ROOT` | (required) | Folder containing HAR files. Files outside this folder cannot be read. |
| `HARSCOPE_MAX_FILE_MB` | 256 | Larger files are rejected (the entire file is loaded into memory). |
| `HARSCOPE_MAX_OUTPUT_CHARS` | 24000 | Size limit for one tool result (≈ 6k tokens). |
| `HARSCOPE_MAX_OPEN_CAPTURES` | 4 | Captures held in memory; the least recently used is evicted and reloaded when requested again. |
| `HARSCOPE_DEFAULT_BODY_CHARS` | 4000 | Default body chunk length in `har_get_entry`. |
| `HARSCOPE_REDACT` | `on` | Set to `off` to disable redaction. |
| `HARSCOPE_REDACT_HEADERS` | – | Additional header names to redact, separated by commas. |
| `HARSCOPE_REDACT_QUERY` | – | Additional query parameter names to redact, separated by commas. |

Other limits (fixed in code): search pages default to 20 entries, maximum 100; bodies return at most 20,000 characters per chunk;
individual header/cookie/query values are limited to 1,000 characters; `har_list` returns at most 500 files; the JSON body cache
holds up to 64 entries / 64 MB.

## Tools

| Tool | Purpose | Output notes |
|---|---|---|
| `har_list` | List `.har` files in `HAR_ROOT` (newest first) | Relative path, size, mtime, `captureId` if already opened |
| `har_open(path)` | Load a file and make it the active capture | `captureId`, `revision`, entry count, failure count, time range |
| `har_overview(captureId?, slowest=5)` | Top-level statistics | Method/status/host/MIME/resource-type distributions, unique endpoints, slowest requests. No bodies. |
| `har_trace_flow(...)` | Timeline grouped by activity | Groups based on time gaps, calls per group, failure streaks, confirmed links |
| `har_search(...)` | Filter + sort + pagination | One compact row per entry: `id`, `t`, `method`, `host`, `path`, `status`, `ms`, `mime`, `flags` |
| `har_get_entry(entryId, sections?, bodyOffset?, bodyMaxChars?)` | Inspect one entry | Only `summary` by default; other sections must be requested |
| `har_query_body(entryId, path?, ...)` | Query specific parts of a JSON body | Values + concrete paths, `fields`, `where`, structure, or aggregates |
| `har_compare_entries(entryIdA, entryIdB, ...)` | Diff two entries | Status, query parameters, headers, JSON bodies by path |
| `har_compare_captures(captureA, captureB, ...)` | Diff matching calls across two HAR files | Different/identical pairs, calls present on only one side |
| `har_repro(entryIds, format?, allHeaders?)` | Sanitized reproduction templates | `.http` or cURL, `variables`, `dependencies` |

### `har_search`

Filters: `urlContains`, `method`, `status`, `statusMin`/`statusMax`, `host`, `pathContains`, `mimeType`, `resourceType`,
`minDurationMs`/`maxDurationMs`, `from`/`to` (ISO-8601), `requestHeader`/`responseHeader` (header names), `correlationId`,
`bodyContains` (text in decoded bodies; `bodySide` = `request`/`response`/either), `failedOnly`,
`includeDataUrls`. Entries with `data:` URLs (inline images/fonts) are hidden by default, with their count included in `warnings`.
Sort keys: `time` (default), `duration`, `status`. Result metadata: `totalMatches`, `returnedCount`, `hasMore`, `nextOffset`,
`truncated`, `warnings`.

Result row flags: `failed` (status 0, ≥ 400, or a network error), `netError:<text>`, `redirect`, `reqBody`,
`resBody` / `resBody:base64`, `noResBody`, `correlated` (has trace/correlation headers).

### `har_get_entry`

Sections: `requestHeaders`, `responseHeaders`, `headers` (both), `queryParams`, `requestBody`, `responseBody`,
`timings`, `cookies`.

- Bodies are returned in chunks: `totalChars`, `offset`, `returnedChars`, `truncated`, `nextOffset`. If a JSON body is
  larger than one chunk, the result suggests using `har_query_body`.
- Base64-encoded text bodies (for example, JSON) are decoded. Binary bodies (images, fonts, etc.) return metadata only.
- Bodies missing from the HAR are reported as `available: false` with a reason; no content is inferred.
- Timing values of `-1` (the HAR sentinel for "not applicable") are reported as `null`.

### `har_query_body`

For large JSON bodies, agents can retrieve the relevant data without reading the text chunk by chunk.

**Path syntax** (a JSONPath subset, not full JSONPath):

| Syntax | Meaning |
|---|---|
| `$` | Root (optional: `data.items` = `$.data.items`) |
| `.name`, `['name']` | Property access (use brackets for names containing spaces or symbols) |
| `[n]`, `[-1]` | Array index; negative indices count from the end |
| `[*]`, `.*` | All array elements / all object property values |
| `[start:end]` | Array slice (exclusive end) |

Not supported: recursive descent `..`, filters `[?()]`, unions `[a,b]`, or slices with a step. These paths are rejected with
an error listing the supported syntax. Use `where` to filter rows.

**Modes and options:**

- `mode="values"` (default): each match is returned with its concrete path (for example, `$.data[3].team`), with
  `offset`/`limit` pagination (default 20, maximum 200). Object/array values larger than 2,000 characters are replaced with
  a summary (`_summary`, `keys`/`length`).
- `fields=["errorCode","detail.message"]`: project relative fields from each match.
- `where=["<field> <op> [value]", ...]`: filter matches before projection/aggregation; multiple conditions are combined with AND.
  Operators: `==`, `!=`, `>`, `>=`, `<`, `<=`, `contains`, `notContains`, `startsWith` (case-insensitive text),
  `exists`, `notExists`. Values can be numbers, `true`/`false`/`null`, `'text with spaces'`, or unquoted text. `@` refers to
  the match itself. Missing fields never satisfy `==`/`<`/`>`/`contains`/`exists`, but do satisfy `!=` and
  `notContains`. Results include `scanned` (before filtering) and `matched` (after filtering).
- `mode="structure"`: the body shape without values (keys, types, array lengths, and merged keys from up to 25 sampled elements).
- `aggregate`: `count`, `sum`, `min`, `max`, `avg`, `distinct`, with optional `valueField` (the field to aggregate) and `groupBy`
  (group by a field). At most 100 groups are returned, sorted by descending value. Non-numeric values are skipped and counted in `skipped`.

Example dashboard response: `{"data":{"data":[{"team":"OPS","risk":"Operational","count":10}, ...]}}`.

| Question | Call |
|---|---|
| What is the body structure? | `har_query_body(id, mode="structure")` |
| How many rows are there? | `har_query_body(id, path="$.data.data[*]", aggregate="count")` |
| Which teams appear, and how many rows does each have? | `har_query_body(id, path="$.data.data[*].team", aggregate="distinct")` |
| What is the total count per team? | `har_query_body(id, path="$.data.data[*]", aggregate="sum", valueField="count", groupBy="team")` |
| Return only `team` and `count` per row? | `har_query_body(id, path="$.data.data[*]", fields=["team","count"])` |
| Do all rows belong to team OPS? | `har_query_body(id, path="$.data.data[*]", where=["team != OPS"])`; `matched` = 0 means no rows violate the rule |
| Which `teamIds` were sent in the request? | `har_query_body(id, source="request", path="$.teamIds")` |

### `har_trace_flow`

A chronological timeline divided into groups. A new group starts when a request begins more than `gapMs`
(default 800 ms) after all previous requests have finished. A group often corresponds to one user action (a click or page
load), but this is a **heuristic**: proximity in time does not prove that two requests came from the same action.

Results include, per group: `start`/`end`, `durationMs`, `gapBeforeMs`, call/failure/slow-call counts, `firstFailure`, and a list
of calls (offset in ms from the group start, status, duration, and flags including `slow`; at most 25 per group). Links supported
by the data are reported separately in `confirmedLinks` (matching trace/correlation header values, redirect targets), while
`failureStreaks` lists consecutive failures. Static assets (images, fonts, CSS, JS) and `data:` URLs are hidden by default
(`includeStatic`). Other options: `from`/`to`, `pathContains`, `slowMs`, group pagination with `offset`/`limit`.

### `har_compare_entries`

Diff two entries from the same capture or different captures:

- `differences` (a one-line summary), status, query parameters, and request/response headers.
- Headers that change with each request (`date`, `etag`, `x-request-id`, `traceparent`, ...) are ignored unless
  `includeVolatileHeaders` is enabled; their count is reported in `ignoredHeaderChanges`.
- JSON bodies are diffed by path (`added`, `removed`, `changed`, `typeChanged`; arrays by index, length changes reported
  as `$.x.length`). Other text bodies report the first differing position with a context snippet.
- `ignorePaths` (for example, `["$.meta.timestamp", "$.data[*].updatedAt"]`) skips fields expected to change.
- Sensitive header/query values remain redacted; matching `fp` values indicate matching original values.

### `har_compare_captures`

Compare matching calls across two opened HAR files, for example **before and after a fix**:

- Calls are paired by method + path + sorted query string (host is ignored, so development and staging can match),
  in order of occurrence.
- Each pair includes status A/B, duration, whether request and response bodies match (JSON is compared structurally),
  and a diff count. Different pairs are sorted first: status changes, then response changes, then request changes.
- `onlyInA` / `onlyInB` contain calls without a match.
- Options: `pathContains`, `method`, `ignorePaths`, `differentOnly`, `includeStatic`, pagination.
- For details about one pair, follow up with `har_compare_entries`.

### `har_repro`

Reproduction templates for up to 10 entries, sorted chronologically. **No requests are sent.**

- Formats: `http` (default; for VS Code REST Client / IntelliJ HTTP Client, independent of the shell) or `curl` (Bash quoting;
  use `curl.exe` in PowerShell and adjust quoting).
- Sensitive headers, cookies, and query parameters, as well as secret-looking JSON/form body fields (`password`, `token`,
  `secret`, `otp`, `pin`, ...), are replaced with `{{placeholder}}` values listed in `variables`.
- Browser-managed and volatile headers are removed. Minimal mode retains only `content-type`, `accept`, `accept-language`,
  non-volatile `x-*` headers, and secret placeholders. Set `allHeaders=true` to retain all non-secret headers.
- `dependencies`: request values (query parameters, `x-*`/auth headers, body fields) first seen in an earlier response,
  such as a login token or the ID of a newly created order. Secret values are shown in redacted form.
- Recorded requests may not be reproducible: sessions, CSRF tokens, cookies, or server state may have changed.

## Example prompts for your agent

- "Open the latest HAR and summarize failed requests."
  → `har_list` → `har_open` → `har_overview` → `har_search(failedOnly=true)`
- "What happened when I clicked Save?" → `har_trace_flow` → inspect the group containing the save calls
- "Why did the payment fail?"
  → `har_search(failedOnly=true)` → `har_query_body(id, fields=["errorCode","message"])` →
  `har_search(correlationId="corr-77")` for other requests with the same correlation ID
- "Compare a successful checkout with a failed one." → `har_compare_entries(successId, failureId)`
- "Did the response change after the fix?" → `har_open(before.har)`, `har_open(after.har)` →
  `har_compare_captures(a, b, differentOnly=true)` → `har_compare_entries` for differing pairs
- "Check that every row in this response belongs to team OPS." → `har_query_body(id, path="$.data[*]", where=["team != OPS"])`
- "Which request sent team ID X?" → `har_search(bodyContains="X", bodySide="request")`
- "Generate requests to repeat the login and then fetch the profile." → `har_repro([loginId, profileId])`

## Performance

Measured using [scripts/bench/Bench.cs](scripts/bench/Bench.cs) with synthetic HAR files (6–18 KB JSON responses per entry):

| HAR size | Entries | Load (read + parse + index) | Memory (heap) | `bodyContains` across all bodies | Other queries |
|---|---|---|---|---|---|
| 150 MB | 9,911 | 1.2 s | 175 MB (1.17×) | 0.29 s | ≤ 0.11 s |
| 400 MB | 26,278 | 2.2 s | 457 MB (1.14×) | 0.79 s | ≤ 0.2 s |

`har_query_body` takes a few milliseconds for cached bodies (initial parse: approximately 25–45 ms). Memory is per process,
so every session opening a large HAR holds its own copy. This is why the default file size limit is 256 MB.

```powershell
dotnet run scripts/bench/Bench.cs -- $env:TEMP\harscope-bench 150
```

## Privacy and security

- The server runs locally: no telemetry, uploads, or outbound network requests. HAR files are read-only.
  `har_repro` only generates text and never sends requests.
- File access is restricted to `HAR_ROOT`. Absolute paths, `..`, alternate data streams (`:`), non-`.har` files, and
  symlinks/junctions pointing outside the root are rejected. `har_list` does not follow links.
- **Redaction (enabled by default)** covers header values (`Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, names
  containing `token`/`secret`/`password`/`session`, etc.), all cookie values, sensitive query parameters (`access_token`,
  `api_key`, `code`, `signature`, etc.), and values resembling `Bearer …`/`Basic …`/JWT credentials.
  Results use `[REDACTED len=142 fp=a1b2c3d4]`. `fp` is an HMAC with a random per-process key: matching values produce the
  same `fp` within one server session, but cannot be correlated across sessions using that fingerprint.
- **Request/response bodies are not redacted** in `har_get_entry`, `har_query_body`, or `har_compare_*`. Passwords or tokens
  in bodies (for example, a login body) may be visible to the agent. The exception is `har_repro`, which replaces
  secret-looking body fields with placeholders. Avoid HAR files containing sensitive production credentials.
- Logs are written only to stderr (stdout is reserved for the MCP protocol) and never contain headers or bodies.
- HAR contents are treated as untrusted data. Tool descriptions and server instructions remind the agent that text
  inside a HAR file is not an instruction.
- Even browser-sanitized HAR exports may contain sensitive data in bodies or URLs.

## Limitations

- **Memory**: the entire file is loaded into memory (approximately 1.15× the file size per open capture, per session).
  There is no streaming parser or persistent index (SQLite) yet.
- `bodyContains` performs plain, case-insensitive text matching, not JSON field queries. To filter by field values, find
  candidates with `bodyContains`, then inspect them with `har_query_body` + `where`.
- `har_query_body`, JSON body diffs, and dependency detection in `har_repro` work with JSON bodies. XML, HTML, and form
  bodies can be read as text with `har_get_entry` (text diffs report only the first differing position).
- JSON arrays are diffed by index: reordering elements can appear as many changes.
- `har_compare_captures` pairs calls by method + path + query. Calls to the same endpoint with constantly changing
  parameters (for example, a cache buster `?_=123`) will not match.
- `har_trace_flow` groups requests solely by timing; background requests (polling, analytics) may be included in a group.
- Time filters use `startedDateTime` from the HAR. Entries without a valid timestamp sort last and are omitted from
  `har_trace_flow`.
- Entry IDs are tied to the file contents. If a file is overwritten with another capture, old IDs lose their meaning
  (reported through warnings and `revision`).
- Requests are not replayed. Replay would require a separate security design (allowed targets, explicit confirmation).

## License

Copyright 2026 Rama Almer Felix.

HARscope is licensed under the [Apache License, Version 2.0](LICENSE). See [NOTICE](NOTICE) for attribution.
Third-party dependencies remain under their respective licenses.
