# Connecting Claude

Termin speaks MCP over Streamable HTTP at `https://<your-host>/mcp` and is its own OAuth 2.1 authorization server.

## claude.ai (web and mobile)

1. Make sure `https://<your-host>/.well-known/oauth-protected-resource` loads from the internet. claude.ai connects from Anthropic's cloud.
2. Go to **Settings → Connectors → Add custom connector**.
3. Enter `https://<your-host>/mcp` as the URL. Leave the OAuth client fields empty: Claude registers itself through dynamic client registration.
4. On Termin's consent page, enter the admin password. Untick write access if you want a read-only connector.
5. Once a connector is added on the web, it also works in the mobile apps.

The registration allowlist covers the claude.ai and claude.com callback hosts. If Claude reports an invalid redirect URI, add the host it uses to `TERMIN_REDIRECT_HOSTS`.

## Claude Desktop

Add the connector the same way under **Settings → Connectors**. Desktop uses a loopback (`http://localhost:<port>`) callback, which is always allowed.

## Claude Code

```sh
claude mcp add --transport http termin https://<your-host>/mcp
```

Claude Code runs the OAuth flow in your browser. For scripted use, set `TERMIN_STATIC_TOKEN` and pass it as a header instead:

```sh
claude mcp add --transport http termin https://<your-host>/mcp --header "Authorization: Bearer $TERMIN_STATIC_TOKEN"
```

## Revoking access

On `/setup`, **Revoke all tokens** invalidates every access and refresh token. Connected clients then have to authorize again. A single client can also call `/revoke` (RFC 7009).

## Troubleshooting

| Symptom | Check |
|---|---|
| "Couldn't reach the MCP server" | Is the URL public and HTTPS? Does `/.well-known/oauth-protected-resource` return JSON? Does the proxy pass `/mcp` through without buffering? |
| 401 loop | `TERMIN_PUBLIC_URL` must match the URL you entered exactly (scheme, host, no trailing path). |
| `invalid_redirect_uri` at registration | Add the callback host to `TERMIN_REDIRECT_HOSTS`. |
| Times off by an hour | Set `TERMIN_DEFAULT_TZ`. Tool descriptions show the date and zone that Claude sees. |
| `backend_unavailable` | Use the **Test** button on `/setup` and check `/readyz`. |
