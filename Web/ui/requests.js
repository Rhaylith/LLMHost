// ---------------------------------------------------------------- request log: filters, live refresh, detail drawer
let rqTimer = null;
async function loadRequests() {
  const p = new URLSearchParams();
  for (const [id, k] of [['rq-model', 'model'], ['rq-status', 'status'], ['rq-q', 'q']]) if ($(id).value.trim()) p.set(k, $(id).value.trim());
  try {
    const r = await api('/api/requests?' + p.toString());
    $('rq-info').textContent = `${r.requests.length} shown of ${r.total} kept` + (r.bodiesLogged ? ' · bodies are logged' : '');
    $('rq-list').innerHTML = '<table><tr><th>Time</th><th>Status</th><th>Request</th><th>Model</th><th>Who</th><th>ms</th><th>In</th><th>Out</th></tr>' + r.requests.map(e =>
      `<tr class="rq" data-id="${esc(e.id)}"><td>${esc(new Date(e.time).toLocaleTimeString())}</td><td class="${e.status >= 400 ? 'err' : 'ok'}">${e.status}</td><td>${esc(e.method)} ${esc(e.path)}${e.stream ? ' <span class="tag">stream</span>' : ''}</td><td>${esc(e.model || '')}</td><td>${esc(e.token || '')}</td><td>${Math.round(e.durationMs)}</td><td>${e.inputTokens || ''}</td><td>${e.outputTokens || ''}</td></tr>`).join('') + '</table>';
    $('rq-list').querySelectorAll('.rq').forEach(tr => tr.onclick = () => showRequest(tr.dataset.id));
  } catch (e) { $('rq-list').innerHTML = `<span class="err">${esc(e.message)}</span>`; }
}
async function showRequest(id) {
  openDrawer('<span class="dim">Loading…</span>');
  try {
    const e = await api('/api/requests/' + encodeURIComponent(id));
    const rows = [['Time', e.time], ['Request', e.method + ' ' + e.path], ['Status', e.status], ['Duration', Math.round(e.durationMs) + ' ms'], ['Model', e.model], ['Who', e.token], ['Address', e.ip],
      ['Tokens', `${e.inputTokens} in · ${e.outputTokens} out`], ['Stream', e.stream ? 'yes' : 'no'], ['Error', e.error]].filter(r => r[1] !== null && r[1] !== undefined && r[1] !== '');
    openDrawer(`<h3>Request ${esc(e.id)}</h3><table>${rows.map(r => `<tr><th style="text-align:left">${esc(r[0])}</th><td>${esc(String(r[1]))}</td></tr>`).join('')}</table>
      <h4>Request body</h4><pre class="log">${e.requestBody ? esc(e.requestBody) : '(not logged: switch on "Keep request and response bodies" in Settings)'}</pre>
      <h4>Response body</h4><pre class="log">${e.responseBody ? esc(e.responseBody) : '(not logged)'}</pre>`);
  } catch (err) { openDrawer(`<span class="err">${esc(err.message)}</span>`); }
}
async function clearRequests() { if (confirm('Clear the request log?')) { await api('/api/requests', {method: 'DELETE'}); loadRequests(); } }
document.querySelector('nav button[data-tab="requests"]').addEventListener('click', () => { loadRequests(); clearInterval(rqTimer); rqTimer = setInterval(() => { if ($('requests').classList.contains('on') && $('rq-live').checked && !document.hidden) loadRequests(); }, 2000); });
['rq-model', 'rq-status', 'rq-q'].forEach(id => $(id).onkeydown = e => { if (e.key === 'Enter') loadRequests(); });
