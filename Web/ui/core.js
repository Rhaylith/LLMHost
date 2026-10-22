const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt = b => { if (!b) return '–'; const u=['B','KB','MB','GB','TB']; let i=0; while (b>=1024&&i<4){b/=1024;i++} return (typeof nf === 'function' ? nf(b, i>1?2:0) : b.toFixed(i>1?2:0))+' '+u[i]; };
let apiKey = localStorage.getItem('apikey') || '';
// the app window opens the page with a per-process token (?k=...): use it and remove it from the address
{ const k = new URLSearchParams(location.search).get('k'); if (k) { apiKey = k; history.replaceState(null, '', location.pathname); } }

async function api(path, opt = {}) {
  const headers = Object.assign({'Content-Type':'application/json'}, opt.headers || {});
  if (apiKey) headers['Authorization'] = 'Bearer ' + apiKey;
  const r = await fetch(path, Object.assign({}, opt, {headers, body: opt.body && typeof opt.body !== 'string' ? JSON.stringify(opt.body) : opt.body}));
  if (r.status === 401) { const k = prompt('This server requires an API key:'); if (k) { apiKey = k; localStorage.setItem('apikey', k); return api(path, opt); } }
  if (opt.raw) return r;
  const j = await r.json().catch(() => ({}));
  if (!r.ok) { const e = new Error(j.error?.message || r.statusText); e.errors = j.errors; e.code = j.error?.code; e.repo = j.error?.repo; e.accessUrl = j.error?.accessUrl; throw e; }
  return j;
}

