using System.Net;

namespace Termin.Server.Infrastructure;

/// <summary>Tiny HTML helpers for the consent and setup pages (no front-end build).</summary>
public static class Html
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static IResult Page(string title, string body, int status = 200) => Results.Content($$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="referrer" content="no-referrer">
        <title>{{E(title)}} · Termin</title>
        <style>
          :root { color-scheme: light dark; --fg:#1d1d1f; --bg:#f6f6f4; --card:#fff; --muted:#6b6b6b; --accent:#2f6f4f; --line:#ddd; --err:#a12b2b; }
          @media (prefers-color-scheme: dark) { :root { --fg:#ececec; --bg:#141414; --card:#1e1e1e; --muted:#9a9a9a; --accent:#6fbf8f; --line:#333; --err:#ff8080; } }
          * { box-sizing: border-box; }
          body { margin:0; font:16px/1.5 system-ui, -apple-system, Segoe UI, sans-serif; color:var(--fg); background:var(--bg); }
          main { max-width: 640px; margin: 48px auto; padding: 0 16px; }
          .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:24px; margin-bottom:16px; }
          h1 { font-size:22px; margin:0 0 4px; } h2 { font-size:17px; margin:0 0 12px; }
          p.muted, .muted { color:var(--muted); font-size:14px; }
          label { display:block; font-size:14px; margin:12px 0 4px; }
          input[type=text], input[type=password], input[type=url], select { width:100%; padding:9px 10px; border:1px solid var(--line); border-radius:8px; background:transparent; color:inherit; font:inherit; }
          button { margin-top:16px; padding:9px 16px; border:0; border-radius:8px; background:var(--accent); color:#fff; font:inherit; cursor:pointer; }
          button.secondary { background:transparent; color:var(--fg); border:1px solid var(--line); }
          button.danger { background:var(--err); }
          .err { color:var(--err); }
          table { width:100%; border-collapse:collapse; font-size:14px; } td, th { text-align:left; padding:6px 4px; border-bottom:1px solid var(--line); }
          code { font-size:13px; }
          .row { display:flex; gap:8px; flex-wrap:wrap; align-items:center; }
        </style>
        </head>
        <body><main>{{body}}</main></body>
        </html>
        """, "text/html; charset=utf-8", statusCode: status);
}
