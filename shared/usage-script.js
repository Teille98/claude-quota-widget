// Reads the claude.ai usage quota. Runs INSIDE a https://claude.ai/ page (WebView2, Android WebView).
// Same origin: the browser attaches its own session cookies; the app's code never sees them.
// UNDOCUMENTED endpoint (the one behind Settings > Usage): the whole format is handled here and in UsageParser.
// The result (a JSON string) is sent to the app via window.chrome.webview.postMessage (Windows)
// or QuotaBridge.post (Android):
//   { ok: true,  org: string | null, limits: [{ kind, percent, resets_at, severity, model }] }
// `org` is the organization name, used only as a default account label inside the app (never logged or exported).
//   { ok: false, reason: 'auth' | 'no-limits' | 'offline' | 'error', status?, message? }
(async () => {
  const H = { accept: 'application/json' };
  try {
    const r0 = await fetch('/api/organizations', { headers: H });
    if (r0.status === 401 || r0.status === 403) return { ok: false, reason: 'auth', status: r0.status };
    if (!r0.ok) return { ok: false, reason: 'error', status: r0.status };
    const orgs = await r0.json();
    if (!Array.isArray(orgs)) return { ok: false, reason: 'error', message: 'organizations: not an array' };
    for (const o of orgs) {
      const r = await fetch(`/api/organizations/${o.uuid}/usage`, { headers: H });
      if (!r.ok) continue;
      const u = await r.json();
      if (Array.isArray(u.limits) && u.limits.length) {
        return {
          ok: true,
          org: typeof o.name === 'string' ? o.name : null,
          limits: u.limits.map(l => ({
            kind: l.kind ?? null,
            percent: l.percent ?? null,
            resets_at: l.resets_at ?? null,
            severity: l.severity ?? null,
            model: l.scope?.model?.display_name ?? null,
          })),
        };
      }
    }
    return { ok: false, reason: 'no-limits', orgs: orgs.length };
  } catch (e) {
    return { ok: false, reason: e instanceof TypeError ? 'offline' : 'error', message: String(e) };
  }
})().then(res => {
  const msg = JSON.stringify(res);
  if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg);
  else if (window.QuotaBridge) window.QuotaBridge.post(msg);
});
