# Termin

Termin is a self-hosted, remote [MCP](https://modelcontextprotocol.io) server that lets Claude (claude.ai web and mobile, Claude Desktop, Claude Code) read and write any CalDAV calendar through one OAuth-protected endpoint.

*Termin* is German for "appointment".

- **Works as a claude.ai custom connector**: Streamable HTTP plus OAuth 2.1 with dynamic client registration, PKCE and refresh-token rotation, all built in. You don't need Keycloak or Authentik.
- **Generic CalDAV (RFC 4791)** with provider profiles for Nextcloud, Radicale, Baïkal, Fastmail and iCloud.
- **Handles the hard cases correctly**: recurrence (RRULE, RDATE, EXDATE, moved occurrences, `this` / `this_and_future` / `all` edits), timezones across DST changes, all-day events with an exclusive DTEND, and floating times.
- **Token-efficient output**: compact JSON with empty fields left out (an event costs about 40–60 tokens). Raw iCalendar is returned only on request.
- **Safe writes**: every change needs the ETag from an earlier read. A stale ETag returns `conflict` with the current version and never overwrites. Unknown and `X-` properties are kept.
- **One container**: a single user by default, several CalDAV accounts per user, and all state in one SQLite file.

```
Claude (web, mobile, Desktop, Code)
        │  Streamable HTTP + OAuth 2.1 bearer token
        ▼
┌──────────────── Termin (one container) ────────────────┐
│ /mcp            10 tools                               │
│ /authorize      consent page (admin password)          │
│ /token /register /revoke  .well-known metadata          │
│ /setup          accounts, connector URL, revoke tokens │
│ SQLite: OAuth clients/tokens (hashed), accounts        │
│         (AES-256-GCM), audit log                       │
└────────────────────────────────────────────────────────┘
        │  PROPFIND / REPORT / PUT / DELETE (Basic auth, ETags)
        ▼
 Nextcloud · Radicale · Baïkal · Fastmail · iCloud · any CalDAV
```

Claude never sees your CalDAV credentials. It only holds a Termin token, and Termin maps that token to backend secrets that are encrypted at rest.

## Quick start

claude.ai connects to Termin from Anthropic's cloud, so Termin needs a **public HTTPS URL**.

```sh
cd deploy
mkdir -p secrets
docker run --rm ghcr.io/lukasmerzorg/termin gen-key > secrets/termin_secret_key
printf '%s' 'a long admin password' > secrets/termin_admin_password
# set TERMIN_PUBLIC_URL in docker-compose.yml and the host name in Caddyfile
docker compose up -d
```

1. Open `https://termin.example.com/setup`, sign in with the admin password and add a CalDAV account. Termin tests the connection before it saves the account.
2. In claude.ai, go to **Settings → Connectors → Add custom connector** and enter `https://termin.example.com/mcp`.
3. Claude opens Termin's consent page. Enter the admin password and choose whether to allow write access.

Provider notes are in [docs/providers.md](docs/providers.md). The connector setup for every Claude client is in [docs/claude-connector.md](docs/claude-connector.md). For Unraid (with SWAG), see [docs/unraid.md](docs/unraid.md).

Until a release tag is pushed, there is no published image on GHCR. Run `docker compose build` (the compose file has `build: ..`) or `docker build -t termin .`.

## Tools

Every tool takes an optional `account` (a label). If you leave it out, Termin uses the default account. Tool descriptions include the server's current date and default timezone, so Claude resolves "tomorrow" correctly.

| Tool | Purpose | Annotations |
|---|---|---|
| `list_calendars` | Calendars with id, name, color, timezone, components (VEVENT/VTODO) and a read-only flag | readOnly |
| `get_events` | Events in a range with recurrences expanded, sorted by start, paged with `next_cursor` | readOnly |
| `get_event` | One event in full: description, attendees, organizer, rrule, exdates, reminders, and raw iCal on request | readOnly |
| `search_events` | Text search over summary, location and description (default range ±90 days) | readOnly |
| `free_busy` | Merged busy blocks, plus free slots ≥ `min_slot_minutes`, optionally within `working_hours` | readOnly |
| `create_event` | Create an event, timed or all-day, optionally recurring with reminders | — |
| `update_event` | Patch fields with scope `this`, `this_and_future` or `all` (needs `etag`) | destructive |
| `delete_event` | Delete an event, one occurrence (EXDATE) or this-and-future (needs `etag`) | destructive |
| `list_todos` | VTODOs filtered by status and due date | readOnly |
| `upsert_todo` | Create, update or complete a VTODO | — |

The event shape that all read tools use:

```json
{
  "uid": "7f3c…",
  "recurrence_id": "2026-10-14T09:00:00+02:00",
  "calendar": "family",
  "summary": "Kita Elterngespräch",
  "start": "2026-10-14T09:00:00+02:00",
  "end": "2026-10-14T09:30:00+02:00",
  "tz": "Europe/Zurich",
  "location": "Boniswil",
  "recurring": true,
  "status": "CONFIRMED",
  "etag": "\"a1b2\""
}
```

All-day events use `"date": "2026-10-14"` instead of `start` and `end`. Multi-day all-day events use `start_date` and `end_date` (both inclusive). Errors are structured as `{ "error": "conflict|not_found|forbidden|invalid_time|invalid_input|backend_unavailable", "message", "hint"?, "current"? }`.

### Semantics

- **Time input**: input times must carry an offset (`2026-10-14T09:00:00+02:00`), an IANA zone suffix (`2026-10-14T09:00[Europe/Zurich]`) or come with a `timezone` argument. Naive times are rejected.
- **Stored events**: created events use a TZID and embed a VTIMEZONE. The zone is `timezone` if you pass it, otherwise `TERMIN_DEFAULT_TZ`.
- **All-day events**: these are stored as DATE values with an exclusive DTEND. The API shows inclusive dates and converts between the two.
- **Floating times**: these come back with `"floating": true` and are interpreted in the default zone.
- **Recurrence expansion**: Termin asks the server to expand recurrences with CalDAV `<expand>`. Any resource that comes back unexpanded is expanded client-side, honoring RRULE, RDATE, EXDATE and RECURRENCE-ID overrides. A query returns at most 1000 occurrences, and infinite rules are clipped to the range.
- **Editing one occurrence**: `scope=this` writes an override VEVENT into the same resource.
- **Splitting a series**: `scope=this_and_future` ends the original RRULE with UNTIL and starts a new series with a new UID. COUNT-based rules are recounted.
- **Moving a series**: when a series moves, its overrides and EXDATEs move with it.
- **Every write**: `If-Match` / `If-None-Match: *`, then SEQUENCE and DTSTAMP are bumped. The stored resource is parsed, patched and reserialized, never rebuilt from JSON.
- **Free/busy**: `TRANSP:TRANSPARENT` and cancelled events don't count as busy. All-day events count as free unless they are marked `OPAQUE`.

## Configuration

Secrets and the public URL come from environment variables. Every secret also accepts a `_FILE` variant for Docker secrets.

| Variable | Default | Meaning |
|---|---|---|
| `TERMIN_PUBLIC_URL` | — (required) | Public HTTPS URL, used as the OAuth issuer. The MCP resource is `<url>/mcp`. |
| `TERMIN_SECRET_KEY` | — (required) | Base64 32-byte key for AES-256-GCM encryption of backend secrets. Generate one with `termin gen-key`. |
| `TERMIN_ADMIN_PASSWORD` / `TERMIN_ADMIN_PASSWORD_HASH` | — | Admin password for the consent page and `/setup`. The hash is Argon2id; create one with `termin hash-password`. |
| `TERMIN_DEFAULT_TZ` | `TZ`, else `UTC` | Default IANA timezone, e.g. `Europe/Zurich`. |
| `TERMIN_DATA_DIR` | `/data` | Location of the SQLite database, Data Protection keys and an optional `termin.yaml`. |
| `TERMIN_CONFIG` | `<data>/termin.yaml` | Accounts as code (see [termin.example.yaml](termin.example.yaml)). |
| `TERMIN_STATIC_TOKEN` | — | Optional fixed bearer token with full scope, for Claude Code, scripts and tests. |
| `TERMIN_REDIRECT_HOSTS` | — | Extra allowed OAuth redirect hosts (HTTPS). claude.ai, claude.com and loopback are always allowed. |
| `TERMIN_ALLOWED_ORIGINS` | — | Extra allowed `Origin` values on `/mcp`. |
| `TERMIN_RATE_LIMIT` | `60` | Requests per minute per token on `/mcp`. |
| `TERMIN_METRICS` | off | `1` exposes Prometheus metrics on `/metrics`. |
| `TERMIN_FORCE_CLIENT_EXPAND` | off | `1` always expands recurrences client-side. |
| `TERMIN_DEV` | off | `1` allows an `http://` public URL and relaxed cookies. Use it for local testing only. |

The scopes are `calendar.read` and `calendar.write`. If the user unticks write access on the consent page, the connector is read-only and its write tools are hidden.

## Security

- **TLS**: the reverse proxy terminates TLS. Termin refuses a non-HTTPS public URL unless `TERMIN_DEV=1` is set.
- **Tokens**: tokens are opaque and stored only as SHA-256 hashes. Access tokens last 1 hour and refresh tokens 30 days. Refresh tokens rotate, and reusing an old refresh token revokes its whole family. Tokens are bound to the `/mcp` resource (RFC 8707).
- **Client registration**: dynamic client registration only accepts redirect URIs on claude.ai, claude.com and loopback, plus `TERMIN_REDIRECT_HOSTS`.
- **DNS rebinding**: Termin validates the `Origin` header on `/mcp`.
- **Rate limits**: 60 requests per minute per token on `/mcp`, and 10 failed logins per 15 minutes per IP.
- **Backend credentials**: they never appear in tool input, tool output or logs. Backend errors are mapped to the structured error set, and URLs and response bodies are never passed through.
- **Event text**: summaries, locations and descriptions are returned as data fields only. Descriptions are capped at 2,000 characters unless `get_event` is called with `full_text`.
- **Logs**: logs are JSON lines carrying the request id, tool, account label, duration and result code. No event content is logged at INFO level.
- **Audit**: every write tool records the uid, calendar and scope in an audit table, which keeps 90 days of history.

## Operations

- `GET /healthz` checks the process.
- `GET /readyz` checks the database plus one PROPFIND per account. The result is cached for 60 s, and the endpoint returns 503 when degraded.
- `GET /metrics` (with `TERMIN_METRICS=1`) exposes tool call counts by result, tool latency, and backend responses by status class.

## Development

```sh
dotnet build
dotnet test tests/Termin.Unit            # unit + OAuth/MCP contract tests
dotnet test tests/Termin.Integration     # Radicale, Baïkal, Nextcloud via Testcontainers
```

The integration suite starts real CalDAV servers in Docker and runs every tool through both expansion paths: server-side `<expand>` and forced client-side expansion. To use a server that is already running instead, set `TERMIN_IT_RADICALE_URL` (plus `_USER` and `_PASSWORD`). To pull from a mirror, set `TERMIN_IT_NEXTCLOUD_IMAGE=…`. To skip a server, set `TERMIN_IT_BAIKAL_SKIP=1`.

```
src/Termin.Server/        ASP.NET host, MCP tools, OAuth endpoints, setup page
src/Termin.CalDav/        discovery, REPORT/PUT/DELETE, provider profiles
src/Termin.Calendar/      JSON model, expansion, tz/all-day rules, patching
src/Termin.Storage/       SQLite, secret encryption, OAuth state, audit
tests/Termin.Unit/        calendar edge cases, storage, OAuth + MCP contract
tests/Termin.Integration/ Testcontainers: Radicale, Baïkal, Nextcloud
tests/fixtures/           anonymized provider .ics
deploy/                   docker-compose.yml + Caddyfile
docs/                     provider setup, claude.ai connector guide
```

The stack is .NET 9 and ASP.NET Core with the official MCP C# SDK (`ModelContextProtocol.AspNetCore`), a hand-written CalDAV client on `HttpClient`, Ical.Net for parsing and expansion, and `Microsoft.Data.Sqlite`.

## Status

| Milestone | State |
|---|---|
| v0.1 read-only tools, discovery, static bearer token, Radicale/Baïkal/Nextcloud in CI | done |
| v0.2 writes with ETags, recurrence scopes, VTODO tools, iCloud and Fastmail profiles | done (iCloud and Fastmail profiles still need the manual release check) |
| v0.3 built-in OAuth 2.1 + DCR, works as a claude.ai custom connector | done |
| v1.0 setup page, multi-account, metrics, docs, Docker image | done except the GHCR release and the MCP registry listing |
| v1.1 Google via OAuth, optional OIDC login delegation (`TERMIN_OIDC_ISSUER`) | not started |

## License

MIT
