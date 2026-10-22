// ---------------------------------------------------------------- linked devices: pairing, permissions, remote models
async function addRemoteModels(sel) {
  try {
    const all = await api('/api/link/models');
    const groups = {};
    for (const m of all.filter(m => !m.local)) (groups[m.device] ||= []).push(m);
    for (const [dev, list] of Object.entries(groups))
      sel.insertAdjacentHTML('beforeend', `<optgroup label="${esc(dev)} (linked device)">` + list.map(m => `<option value="${esc(m.id)}">${esc(m.id.slice(dev.length + 1))}${m.loaded ? '' : ' (loads on demand)'}</option>`).join('') + '</optgroup>');
  } catch (e) { /* linking is optional */ }
}
async function renderLink() {
  const box = $('link-body'); if (!box) return;
  try {
    const s = await api('/api/link/status');
    const peers = s.peers.map(p => `<tr><td><b>${esc(p.name)}</b><div class="dim">${esc(p.address)}</div></td><td class="${p.online ? 'ok' : 'dim'}">${p.online ? 'online · ' + p.models.length + ' models' : 'offline'}</td>
      <td>${['chat', 'load', 'manage'].map(k => `<label style="margin-right:8px"><input type="checkbox" style="width:auto" ${p.permissions.includes(k) ? 'checked' : ''} onchange='setLinkPerm(${JSON.stringify(p.id)}, this)' data-k="${k}"> ${k}</label>`).join('')}</td>
      <td><button class="b sec" onclick='unpairDevice(${JSON.stringify(p.id)})'>Remove</button></td></tr>`).join('');
    box.innerHTML = `<label><input type="checkbox" id="lk-on" style="width:auto" ${s.enabled ? 'checked' : ''} onchange="linkToggle(this.checked)"> Link this device (${esc(s.name)} · ${esc(s.deviceId)})</label>
      ${s.enabled ? `<div class="row" style="margin-top:8px"><input id="lk-name" value="${esc(s.name)}" style="max-width:220px"><button class="b sec" onclick="linkName()">Rename</button>
        <label class="dim" style="margin-left:12px">Preferred device <select id="lk-pref" onchange="linkPreferred(this.value)"><option value="local">this device</option>${s.peers.map(p => `<option ${p.name === s.preferredDevice ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select></label></div>
        <h4>Pair a device</h4>
        <div class="row"><button class="b" onclick="linkShowCode()">Show a code on this device</button><span id="lk-code" style="font-size:20px;letter-spacing:3px"></span></div>
        <div class="row" style="margin-top:6px"><input id="lk-addr" placeholder="Address of the other device, e.g. http://100.64.0.7:1234"><input id="lk-own" placeholder="This device's address (so it can answer back)"><input id="lk-in" placeholder="Code" style="max-width:100px"><button class="b" onclick="linkPair()">Pair</button></div>
        <div id="lk-msg" class="dim"></div>
        <h4>Paired devices</h4>${s.peers.length ? `<table>${peers}</table>` : '<span class="dim">None yet.</span>'}` : ''}`;
  } catch (e) { box.innerHTML = `<span class="err">${esc(e.message)}</span>`; }
}
async function linkToggle(on) { try { await api('/api/link/' + (on ? 'enable' : 'disable'), {method: 'POST'}); } catch (e) { alert(e.message); } renderLink(); refreshStatus(); }
async function linkName() { try { await api('/api/link/name', {method: 'POST', body: {name: $('lk-name').value}}); renderLink(); } catch (e) { alert(e.message); } }
async function linkPreferred(v) { try { await api('/api/link/preferred', {method: 'POST', body: {device: v}}); } catch (e) { alert(e.message); } }
async function linkShowCode() { try { const r = await api('/api/link/pair/start', {method: 'POST'}); $('lk-code').textContent = r.code; $('lk-msg').textContent = `Type this code on the other device within ${r.expiresInMinutes} minutes.`; } catch (e) { alert(e.message); } }
async function linkPair() {
  $('lk-msg').className = 'dim'; $('lk-msg').textContent = 'Pairing…';
  try { const r = await api('/api/link/pair', {method: 'POST', body: {address: $('lk-addr').value.trim(), code: $('lk-in').value.trim(), ownAddress: $('lk-own').value.trim()}}); renderLink(); refreshStatus(); }
  catch (e) { $('lk-msg').className = 'err'; $('lk-msg').textContent = e.message; }
}
async function setLinkPerm(id, el) {
  const row = el.closest('td'); const perms = [...row.querySelectorAll('input:checked')].map(i => i.dataset.k);
  try { await api(`/api/link/peers/${encodeURIComponent(id)}/permissions`, {method: 'PUT', body: {permissions: perms}}); } catch (e) { alert(e.message); }
}
async function unpairDevice(id) { if (confirm('Remove this device? It will have to be paired again.')) { await api('/api/link/peers/' + encodeURIComponent(id), {method: 'DELETE'}); renderLink(); refreshStatus(); } }
