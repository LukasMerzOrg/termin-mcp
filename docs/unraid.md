# Running Termin on Unraid

Termin is one container with one data directory, so Unraid's built-in Docker manager is enough. You need a public HTTPS hostname that reaches the container, because claude.ai connects from Anthropic's cloud.

The examples use `termin.merz.tools`. Replace it with your own host name.

## 1. Image

The template pulls `ghcr.io/lukasmerzorg/termin:latest`. CI publishes it when a `v*` tag is pushed. GHCR packages start out private, so either make the package public (GitHub → Packages → termin → Package settings → Change visibility) or log in once from the Unraid terminal:

```sh
docker login ghcr.io -u <github-user>   # password: a PAT with read:packages
```

## 2. Add the container

1. In the Unraid terminal, create the data folder with the right owner:
   ```sh
   mkdir -p /mnt/user/appdata/termin && chown 99:100 /mnt/user/appdata/termin
   ```
1. Copy [deploy/unraid/termin.xml](../deploy/unraid/termin.xml) to `/boot/config/plugins/dockerMan/templates-user/my-termin.xml` (through the `flash` share or the terminal).
1. Go to **Docker → Add Container** and pick **termin** under *Template*.
1. Fill in:
   - **Public URL**: `https://termin.merz.tools`, with no trailing slash. It must match the connector URL exactly.
   - **Secret key**: the output of `openssl rand -base64 32` from the Unraid terminal. Keep a copy, because losing or changing it makes stored CalDAV passwords unreadable.
   - **Admin password**: a long password. You enter it on `/setup` and on the consent page.
   - **Default timezone**: `Europe/Zurich`.
   - **Network Type**: SWAG's custom network (see step 3).
1. Click **Apply**.

The template runs the container as `--user 99:100` (`nobody:users`) so it can write to `/mnt/user/appdata/termin`. If you drop that flag, the database can't be created and the container exits on startup.

Check the container log for a line like `Now listening on: http://[::]:8080`.

## 3. Public HTTPS with SWAG

1. **Network**: in the Termin container settings, set *Network Type* to the custom network SWAG uses (for example `proxynet`). Name resolution by container name only works on a custom network. You can then remove the port mapping, because only SWAG talks to Termin.
2. **Proxy conf**: copy [deploy/unraid/termin.subdomain.conf](../deploy/unraid/termin.subdomain.conf) to `/mnt/user/appdata/swag/nginx/proxy-confs/termin.subdomain.conf`.
3. **Certificate**: the SWAG certificate has to cover `termin.merz.tools`.
   - If SWAG's `URL` is `merz.tools`: add `termin` to `SUBDOMAINS`, or nothing at all if you use `SUBDOMAINS=wildcard`.
   - If SWAG's `URL` is a different domain: add `termin.merz.tools` to `EXTRA_DOMAINS`. With DNS validation, the DNS plugin credentials must also cover `merz.tools`.
4. **DNS**: point `termin.merz.tools` at your public IP, or CNAME it to the name you already use for SWAG.
5. Restart SWAG and check the config: `docker exec swag nginx -t`.

The conf leaves out Authelia and Authentik on purpose, because claude.ai has to reach `/mcp`, `/authorize` and `/.well-known/*` without a login wall. Termin's consent page and `/setup` are protected by the admin password. SWAG's `proxy.conf` already sets `Host` and `X-Forwarded-Proto`, and the conf turns off buffering for streamed `/mcp` responses.

<details><summary>Other proxies</summary>

**Nginx Proxy Manager**: forward to `http://<unraid-ip>:8080` and add `proxy_buffering off;` under *Advanced*.
**Cloudflare Tunnel**: service `http://<unraid-ip>:8080`, with Cloudflare Access turned off for this host.
</details>

Termin trusts `X-Forwarded-*` only from private networks (10/8, 172.16/12, 192.168/16, loopback), which covers every proxy above.

Test from outside your network, for example on mobile data:

```sh
curl https://termin.merz.tools/.well-known/oauth-protected-resource
```

It should return JSON with `"resource": "https://termin.merz.tools/mcp"`.

## 4. Nextcloud account

1. In Nextcloud, go to **Settings → Security → Devices & sessions** and create an app password called `termin`.
2. Open `https://termin.merz.tools/setup` and sign in with the admin password.
3. Add an account:
   - **Profile**: Nextcloud
   - **URL**: `https://<your-nextcloud>/remote.php/dav`
   - **Username**: your Nextcloud login name (shown in Settings → Personal info, not the display name)
   - **Password**: the app password
4. Termin tests the connection and lists your calendars before saving.

If Nextcloud runs on the same Unraid box, you can use its LAN address, for example `http://192.168.1.10:8443/remote.php/dav`. Plain HTTP to a private address is allowed without a warning.

## 5. Connect claude.ai

1. Go to **Settings → Connectors → Add custom connector**.
2. Enter the name `Termin` and the URL `https://termin.merz.tools/mcp`. Leave the OAuth client ID and secret empty.
3. On Termin's consent page, enter the admin password and choose whether to allow write access.

The connector then also works in the Claude mobile apps. Test it with a prompt like "What's on my calendar tomorrow?"

## Updating and backups

- **Update**: Docker tab → *Check for updates* → *Apply update*. State lives in `/data`, so it survives updates.
- **Backup**: `/mnt/user/appdata/termin` (the SQLite file and keys) plus your secret key. The appdata backup plugin covers the folder, but the key lives in the template, so store it in your password manager.

## Troubleshooting

See [claude-connector.md](claude-connector.md#troubleshooting). Unraid-specific issues:

| Symptom | Cause |
|---|---|
| Container stops right away with `SQLite Error 14` or `access denied` on `/data` | Missing `--user 99:100`, or the appdata folder belongs to root. Fix it with `chown -R 99:100 /mnt/user/appdata/termin`. |
| `TERMIN_PUBLIC_URL must be https` | You entered `http://`. Use the public HTTPS URL, not the LAN address. |
| Consent page loads but login loops | The proxy doesn't send `X-Forwarded-Proto: https`, so the secure cookie is dropped. |
| SWAG returns 502 | Termin isn't on SWAG's custom network, or the container isn't named `termin`. |
| `unauthorized` when pulling the image | The GHCR package is private. Make it public or run `docker login ghcr.io`. |
