# Provider setup

Each account needs a CalDAV base URL, a username and a secret. Termin discovers the calendar home through `/.well-known/caldav` (RFC 6764) or `current-user-principal`. Usually the server root or the DAV root is enough.

Provider quirks are handled through profile flags, never through checks for a provider's name in tool code. The profile is detected from the URL (`provider: auto`), or you can set it explicitly.

| Provider | URL | Secret | Profile notes |
|---|---|---|---|
| Nextcloud | `https://cloud.example.com/remote.php/dav` | App password (Settings → Security → Devices & sessions) | Skips REPORT entries without `calendar-data` (trash bin). Tested in CI. |
| Radicale | `https://dav.example.com/` | User password | Older versions ignore `<expand>`. Termin expands client-side whenever a response still carries an RRULE. Tested in CI. |
| Baïkal | `https://dav.example.com/dav.php/` | User password (Basic auth must be enabled in Baïkal's admin settings) | Tested in CI. |
| Fastmail | `https://caldav.fastmail.com/` | App password with CalDAV access | Strict about VTIMEZONE, so Termin always embeds it. |
| iCloud | `https://caldav.icloud.com/` | App-specific password from [appleid.apple.com](https://appleid.apple.com) | Partitioned hosts (`pXX-caldav.icloud.com`). Termin follows the principal redirect to `*.icloud.com` only. |
| Google | — | — | Planned for v1.1: CalDAV access to Google needs OAuth, not a password. |
| Any other CalDAV server | its DAV URL | its password | `generic` profile |

## Notes

- **Calendar ids**: the id is the last path segment of the collection URL (Nextcloud: `personal`; iCloud: a UUID-like name). Tools accept either the id or the display name.
- **Read-only calendars**: calendars without the `write` privilege are flagged `read_only`, and writes to them return `forbidden`.
- **Redirects**: Termin follows redirects manually so it can keep sending credentials. It only crosses to another host over HTTPS, and only to domains the provider profile allows.
- **Plain HTTP**: `http://` backends work but log a warning unless the host is private (LAN, Docker network).
