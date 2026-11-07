// ---------------------------------------------------------------- discover
// ---------------------------------------------------------------- the model table (staff picks and search results)
// One table for both. Click a column header to sort (click again to reverse). Name, parameters, size and the capability columns
// sort the rows that are loaded; downloads, likes and updated also ask Hugging Face for that order, so the first rows really are
// the top of the list. Sizes are fetched in one request after the rows appear and sort once they are in.
const DT = {mode: 'picks', view: 'table', rows: [], picks: [], info: '', picksInfo: '', canMore: false, sort: {key: null, dir: -1}};
const DT_SERVER_SORT = {downloads: 'downloads', likes: 'likes', updated: 'updated'};
const DT_COLS = [
  {key: 'name', label: 'Model', dir: 1, get: r => r.name.toLowerCase()},
  {key: 'params', label: 'Params', title: 'Number of parameters (read from the model name)', get: r => r.params},
  {key: 'size', label: 'Size', title: 'Download size: the Q4_K_M file when the repository has one, otherwise the smallest file', get: r => r.sizeSort},
  {key: 'vision', label: '👁', title: 'Vision (image input)', flag: true, get: r => r.caps.vision ? 1 : 0},
  {key: 'tools', label: '🔧', title: 'Tool use', flag: true, get: r => r.caps.tools ? 1 : 0},
  {key: 'reasoning', label: '🧠', title: 'Reasoning', flag: true, get: r => r.caps.reasoning ? 1 : 0},
  {key: 'moe', label: '⚙', title: 'Mixture of experts', flag: true, get: r => r.caps.moe ? 1 : 0},
  {key: 'downloads', label: 'Downloads', get: r => r.downloads},
  {key: 'likes', label: 'Likes', get: r => r.likes},
  {key: 'updated', label: 'Updated', get: r => r.updated || null},
];
// "8B", "135M", "0.5B"; the largest number wins ("30B-A3B" is 30B), a digit glued to a letter ("A3B", "E4B") is not a size
function parseParams(text) {
  let best = null;
  for (const m of String(text || '').matchAll(/(?<![A-Za-z0-9.])(\d+(?:\.\d+)?)([BbMm])(?![A-Za-z0-9])/g)) {
    const v = parseFloat(m[1]) * (m[2].toLowerCase() === 'b' ? 1 : 0.001);
    if (!best || v > best.v) best = {v, label: m[1] + m[2].toUpperCase()};
  }
  return best;
}
const capsOfTags = tags => ({vision: tags.includes('vision'), tools: tags.includes('tools'), reasoning: tags.includes('reasoning'), moe: tags.includes('moe'), embedding: tags.includes('embeddings'), rerank: tags.includes('rerank')});
function pickRow(e) {
  const p = parseParams(e.params) || parseParams(e.name);
  return {kind: 'pick', repo: e.repo, name: e.name, sub: e.repo + (e.notes ? ' · ' + e.notes : ''), params: p ? p.v : null, paramsLabel: p ? p.label : (e.params || ''),
    caps: capsOfTags(e.tags || []), downloads: null, likes: null, updated: null, quant: e.recommendedQuant, size: null, sizeSort: null};
}
// exact parameter count from the GGUF header (Hugging Face reports it) as a size label: 134515008 -> 135M, 7615616512 -> 7.6B
function paramsLabel(n) { return n >= 1e9 ? (n >= 1e10 ? Math.round(n / 1e9) : (n / 1e9).toFixed(1).replace(/\.0$/, "")) + "B" : Math.round(n / 1e6) + "M"; }
function searchRow(m) {
  const p = m.parameters > 0 ? {v: m.parameters / 1e9, label: paramsLabel(m.parameters)} : parseParams(m.id);
  return {kind: 'search', repo: m.id, name: m.id, license: m.license, params: p ? p.v : null, paramsLabel: p ? p.label : '', caps: m.capabilities || {},
    downloads: m.downloads, likes: m.likes, updated: (m.lastModified || '').slice(0, 10), quant: 'Q4_K_M', size: null, sizeSort: null};
}
function dtSorted(rows = DT.rows) {
  const col = DT_COLS.find(c => c.key === DT.sort.key), dir = DT.sort.dir;
  if (!col) return rows.slice();
  return rows.map((r, i) => [r, i]).sort(([a, i], [b, j]) => {
    const x = col.get(a), y = col.get(b);
    if (x == null && y == null) return i - j;
    if (x == null) return 1; if (y == null) return -1;                           // rows without a value stay at the bottom either way
    const c = typeof x === 'string' ? x.localeCompare(y) : x - y;
    return c ? c * dir : i - j;
  }).map(p => p[0]);
}
function dtSizeCell(r) {
  const s = r.size;
  if (!s) return r.sizeFailed ? '<span class="dim" title="Size not available (gated, unknown or offline)">—</span>' : '<span class="dim">…</span>';
  const range = s.minBytes === s.maxBytes ? fmt(s.maxBytes) : fmt(s.minBytes) + ' – ' + fmt(s.maxBytes);
  const title = `${s.files} file${s.files === 1 ? '' : 's'}: ${range}`;
  return `<span title="${esc(title)}">${s.quantBytes ? esc(r.quant) + ' ' + fmt(s.quantBytes) : range}</span>`;
}
function dtRow(r) {
  const J = v => JSON.stringify(v);
  const flag = k => r.caps[k] ? `<span class="ok" title="${k}">✓</span>` : '<span class="dim">·</span>';
  const others = ['audio', 'embedding', 'rerank'].filter(k => r.caps[k]).map(k => `<span class="tag">${k}</span>`).join('');
  const name = r.kind === 'pick' ? `<b>${esc(r.name)}</b> ${others}<div class="dim">${esc(r.sub)}</div>` : `${esc(r.name)} ${others} ${licenseTag(r.license, '')}`;
  const dl = r.kind === 'pick' ? `<button class="b" title="Download ${esc(r.quant)}" onclick='dl(${J(r.repo)}, null, ${J(r.quant)})'>Download</button> ` : '';
  return `<tr><td>${name}</td><td style="white-space:nowrap">${esc(r.paramsLabel || '—')}</td><td style="white-space:nowrap">${dtSizeCell(r)}</td>`
    + ['vision', 'tools', 'reasoning', 'moe'].map(k => `<td class="flag">${flag(k)}</td>`).join('')
    + `<td>${r.downloads == null ? '' : r.downloads.toLocaleString()}</td><td>${r.likes == null ? '' : r.likes}</td><td style="white-space:nowrap">${esc(r.updated || '')}</td>`
    + `<td style="white-space:nowrap">${dl}<button class="b sec" onclick='showFiles(${J(r.repo)})'>Files</button> <button class="b sec" title="README, licence and details" onclick='showCard(${J(r.repo)})'>Details</button></td></tr>`;
}
function dtRender() {
  if (DT.view !== 'table') return;
  const head = DT_COLS.map(c => {
    const on = DT.sort.key === c.key;
    return `<th class="sortable${c.flag ? ' flag' : ''}" title="${esc(c.title || 'Sort by ' + c.label)}" onclick="dtSort('${c.key}')">${esc(c.label)}${on ? (DT.sort.dir > 0 ? ' ▲' : ' ▼') : ''}</th>`;
  }).join('') + '<th></th>';
  $('results').innerHTML = (DT.info ? `<div class="dim" style="margin-bottom:4px">${DT.info}</div>` : '')
    + [['LLMs', DT.rows.filter(r => !r.caps.vision)], ['Image models (vision)', DT.rows.filter(r => r.caps.vision)]].filter(g => g[1].length)   // text and image models stay in separate lists
      .map(([title, rows]) => `<h4 style="margin:20px 0 6px;padding-bottom:4px;border-bottom:2px solid var(--line);font-size:22px;font-weight:600">${title} <span class="dim">(${rows.length})</span></h4><table class="dt"><thead><tr>${head}</tr></thead><tbody>${dtSorted(rows).map(dtRow).join('')}</tbody></table>`).join('')
    + (DT.rows.length ? '' : '<span class="dim">Nothing found.</span>')
    + (DT.canMore ? '<div style="margin-top:8px"><button class="b sec" onclick="search(true)">Load more</button></div>' : '');
}
function dtSort(key) {
  const col = DT_COLS.find(c => c.key === key);
  if (DT.sort.key === key) DT.sort.dir = -DT.sort.dir; else DT.sort = {key, dir: col.dir || -1};
  const server = DT_SERVER_SORT[key];
  if (server && DT.mode === 'search' && DT.sort.dir < 0 && $('d-sort').value !== server) { $('d-sort').value = server; search(); return; }   // new top of the list from Hugging Face
  dtRender();
}
function dtBack() { DT.view = 'table'; dtRender(); }
async function dtLoadSizes(rows) {
  const need = rows.filter(r => !r.size && !r.sizeTried);
  need.forEach(r => { r.sizeTried = true; });
  for (let i = 0; i < need.length; i += 50) {
    const part = need.slice(i, i + 50);
    let sizes = {};
    try { sizes = (await api('/api/models/repo-sizes', {method: 'POST', body: {repos: part.map(r => ({repo: r.repo, quant: r.quant}))}})).sizes || {}; } catch (e) { /* offline: dashes */ }
    for (const r of part) { const s = sizes[r.repo]; if (s && s.files) { r.size = s; r.sizeSort = s.quantBytes || s.minBytes; } else r.sizeFailed = true; }
    if (rows.some(r => DT.rows.includes(r))) dtRender();
  }
}
function showPicks() {
  DT.mode = 'picks'; DT.rows = DT.picks; DT.canMore = false; DT.info = DT.picksInfo;
  if (DT.sort.key && DT_SERVER_SORT[DT.sort.key]) DT.sort = {key: null, dir: -1};   // staff picks have no downloads, likes or dates
  dtRender(); dtLoadSizes(DT.picks);
}
async function renderCatalog() {
  const catData = await api('/api/models/catalog');
  DT.picks = catData.entries.map(pickRow);
  DT.picksInfo = `Staff picks · updated ${esc(catData.updated || 'unknown')} · ${esc(catData.source)}${catData.url ? ' <button class="b sec" onclick="refreshCatalog()">Refresh</button>' : ''} · search above for anything else`;
  if (DT.mode === 'picks' && DT.view === 'table') showPicks();
}
// ---------------------------------------------------------------- model badges and detail drawer
const CAP_BADGES = [['vision', '👁 vision'], ['audio', '🎧 audio'], ['tools', '🔧 tools'], ['reasoning', '🧠 reasoning'], ['moe', '⚙ MoE'], ['embedding', '📐 embedding'], ['rerank', '↕ rerank']];
function capBadges(c) { return c ? CAP_BADGES.filter(([k]) => c[k]).map(([k, t]) => `<span class="tag" title="${k}">${t}</span>`).join('') : ''; }
function licenseTag(id, kind) {
  if (!id) return '';
  const cls = kind === 'noncommercial' ? 'err' : kind === 'restricted' ? 'warn' : 'dim';
  return `<span class="tag ${cls}" title="Licence">${esc(id)}${kind === 'noncommercial' ? ' ⚠' : ''}</span>`;
}
// a small, safe Markdown view: everything is escaped first, then a few patterns become tags; links only for http(s)
function mdLite(md) {
  const blocks = [];
  let s = esc(md).replace(/```[^\n]*\n([\s\S]*?)```/g, (_, c) => { blocks.push(c); return '\u0000' + (blocks.length - 1) + '\u0000'; });
  s = s.replace(/^(#{1,4})\s+(.*)$/gm, (_, h, t) => `<h${h.length + 2}>${t}</h${h.length + 2}>`)
       .replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>').replace(/`([^`\n]+)`/g, '<code>$1</code>')
       .replace(/\[([^\]\n]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
       .replace(/^\s*[-*]\s+(.*)$/gm, '&bull; $1')
       .replace(/<!--[\s\S]*?-->/g, '');
  return s.replace(/\u0000(\d+)\u0000/g, (_, i) => '<pre>' + blocks[i] + '</pre>');
}
// ---------------------------------------------------------------- model folders and disk usage
async function renderDisk() {
  try {
    const d = await api('/api/models/disk');
    const pct = r => r.totalBytes ? Math.round((1 - r.freeBytes / r.totalBytes) * 100) : 0;
    $('disk-body').innerHTML = `<table><tr><th>Folder</th><th></th><th>Models</th><th>Size</th><th>Drive free</th><th></th></tr>` + d.roots.map(r =>
      `<tr><td style="word-break:break-all">${esc(r.path)}${r.exists ? '' : ' <span class="err">missing</span>'}</td><td><span class="tag">${r.role === 'download' ? 'main' : 'extra'}</span></td><td>${r.modelCount}</td><td>${fmt(r.modelBytes)}</td>
       <td>${fmt(r.freeBytes)} of ${fmt(r.totalBytes)} <span class="dim">(${pct(r)}% used)</span></td>
       <td>${r.role === 'extra' ? `<button class="b sec" onclick='dirRemove(${JSON.stringify(r.path)})'>Remove</button>` : ''}</td></tr>`).join('') + '</table>'
      + (d.largest.length ? '<h4>Largest models</h4>' + d.largest.map(m => `<div>${fmt(m.bytes)} · ${esc(m.id)}</div>`).join('') : '')
      + (d.partials.length ? `<div style="margin-top:8px"><span class="warn">${d.partials.length} leftover partial download(s), ${fmt(d.partials.reduce((a, p) => a + p.bytes, 0))}</span> <button class="b sec" onclick="cleanPartials()">Delete them</button></div>` : '');
  } catch (e) { $('disk-body').innerHTML = `<span class="err">${esc(e.message)}</span>`; }
}
async function dirAdd() {
  const path = $('dir-add').value.trim(); if (!path) return;
  try { await api('/api/models/directory/add', {method:'POST', body:{path}}); $('dir-add').value = ''; renderDisk(); refreshModels(); } catch (e) { alert(e.message); }
}
async function dirRemove(path) { try { await api('/api/models/directory/remove', {method:'POST', body:{path}}); renderDisk(); refreshModels(); } catch (e) { alert(e.message); } }
async function cleanPartials() { try { const r = await api('/api/models/disk/clean-partials', {method:'POST'}); alert('Deleted ' + r.deleted + ' file(s), ' + fmt(r.bytes)); renderDisk(); } catch (e) { alert(e.message); } }
let dirTimer = null;
async function dirMove() {
  const to = $('dir-to').value.trim(); if (!to) return;
  const mode = $('dir-mode').value;
  if (mode !== 'switchOnly' && !confirm((mode === 'move' ? 'Move' : 'Copy') + ' all model files to ' + to + '?')) return;
  try { await api('/api/models/directory/move', {method:'POST', body:{to, mode}}); pollMove(); } catch (e) { $('dir-job').className = 'err'; $('dir-job').textContent = e.message; }
}
async function pollMove() {
  clearTimeout(dirTimer);
  const j = await api('/api/models/directory/move').catch(() => null);
  if (!j || j.state === 'none') return;
  const el = $('dir-job');
  if (j.state === 'running') {
    el.className = 'dim'; el.innerHTML = `${esc(j.mode)}: ${(j.progress * 100).toFixed(1)}% · ${j.filesDone}/${j.files} files · ${esc(j.current || '')} <button class="b sec" onclick="api('/api/models/directory/move',{method:'DELETE'})">Cancel</button>`;
    dirTimer = setTimeout(pollMove, 700);
  } else {
    el.className = j.state === 'completed' ? 'ok' : 'err';
    el.textContent = j.state === 'completed' ? 'The main folder is now ' + j.to : (j.error || j.state);
    renderDisk(); refreshModels();
  }
}
// ---------------------------------------------------------------- model update check
let updateMap = {}, localSort = 'name';
function updateBadge(m) {
  const u = updateMap[m.id];
  if (!u) return '';
  if (u.status === 'update') return `<span class="tag warn" title="${esc(u.message || '')}">update available</span> <button class="b sec" ${m.loadedAs ? 'disabled title="Eject the model first"' : ''} onclick='applyUpdate(${JSON.stringify(m.id)}, this)'>Update</button>`;
  if (u.status === 'renamed') return `<span class="tag warn" title="${esc(u.message || '')}">repo moved</span>`;
  if (u.status === 'missing') return `<span class="tag warn" title="${esc(u.message || '')}">not in repo any more</span>`;
  if (u.status === 'current') return '<span class="tag" title="Same checksum as Hugging Face">up to date</span>';
  if (u.status === 'error') return `<span class="tag err" title="${esc(u.message || '')}">check failed</span>`;
  return '<span class="tag" title="' + esc(u.message || '') + '">unknown</span>';
}
async function checkUpdates(btn) {
  btn.disabled = true; $('upd-msg').textContent = 'Checking…';
  try { const r = await api('/api/models/updates/check', {method:'POST', body:{}}); await refreshModels(); $('upd-msg').textContent = r.updatesAvailable.length ? r.updatesAvailable.length + ' update(s) available' : 'Everything is up to date (or unknown).'; }
  catch (e) { $('upd-msg').textContent = e.message; btn.disabled = false; }
}
async function applyUpdate(id, btn) {
  if (!confirm('Download the newer version of ' + id + '? The current file stays until the new one is complete and verified.')) return;
  btn.disabled = true;
  try { await api('/api/models/updates/apply', {method:'POST', body:{id}}); refreshDownloads(); }
  catch (e) { alert(e.message); btn.disabled = false; }
}
// ---------------------------------------------------------------- gated models and the Hugging Face token
function openExternal(url) { if (/^https:\/\/huggingface\.co\//.test(url || '')) window.open(url, '_blank', 'noopener,noreferrer'); }
function errHtml(e, retry) {
  if (e.code !== 'gated') return `<span class="err">${esc(e.message)}</span>`;
  return `<div class="card"><b>This model requires accepting a licence on Hugging Face</b><div class="dim" style="margin:6px 0">${esc(e.message)}</div>
    <button class="b" onclick='openExternal(${JSON.stringify(e.accessUrl)})'>Open on Hugging Face</button>
    <button class="b sec" onclick='${retry}'>Retry</button></div>`;
}
async function checkHfToken() {
  const out = $('hf-who'); out.className = 'dim'; out.textContent = 'Checking…';
  try {
    const r = await api('/api/hf/whoami', {method:'POST', body:{token: $('s-hftoken').value || null}});
    out.className = r.valid ? 'ok' : 'err';
    out.textContent = r.valid ? `Signed in as ${r.name}${r.fullName ? ' (' + r.fullName + ')' : ''}` : r.error;
  } catch (e) { out.className = 'err'; out.textContent = e.message; }
}
function openDrawer(html) { $('drawer-body').innerHTML = html; $('drawer').style.display = 'block'; }
function closeDrawer() { $('drawer').style.display = 'none'; }
async function showCard(repo) {
  openDrawer('<span class="dim">Loading…</span>');
  try {
    const c = await api('/api/models/card?repo=' + encodeURIComponent(repo));
    openDrawer(`<h3>${esc(c.repo)}</h3><div>${capBadges(c.capabilities)} ${licenseTag(c.license, c.licenseKind)}</div>
      ${c.licenseWarning ? `<div class="err" style="margin:6px 0">${esc(c.licenseWarning)}</div>` : ''}
      <div class="dim" style="margin:6px 0">${c.downloads.toLocaleString()} downloads · ${c.likes} likes${c.lastModified ? ' · updated ' + esc(c.lastModified.slice(0, 10)) : ''}${c.baseModel ? ' · base: ' + esc(c.baseModel) : ''}${c.pipeline ? ' · ' + esc(c.pipeline) : ''}</div>
      <div class="row"><button class="b" onclick='showFiles(${JSON.stringify(c.repo)}); closeDrawer()'>Files and quants</button></div>
      <div class="readme">${c.readme ? mdLite(c.readme) : '<span class="dim">This repository has no README.</span>'}</div>`);
  } catch (e) { openDrawer(errHtml(e, 'showCard(' + JSON.stringify(repo).replace(/"/g, "&quot;") + ')')); }
}
async function showLocalInfo(id) {
  openDrawer('<span class="dim">Loading…</span>');
  try {
    const g = await api('/api/models/info?id=' + encodeURIComponent(id));
    const rows = [['Architecture', g.architecture], ['Name', g.name], ['Parameters', g.sizeLabel], ['Quantization', g.quantization], ['Context length', g.contextLength || null],
      ['Embedding size', g.embeddingLength || null], ['Layers', g.blockCount || null], ['Attention heads', g.headCount ? g.headCount + ' (' + (g.headCountKv || g.headCount) + ' KV)' : null],
      ['Experts', g.expertCount ? g.expertCount + ' (' + g.expertUsedCount + ' per token)' : null], ['Vocabulary', g.vocabSize || null], ['Base model', g.baseModel],
      ['File size', fmt(g.fileBytes)], ['Chat template', g.chatTemplate ? 'yes' : 'no'], ['Source', g.repoUrl]].filter(r => r[1] != null && r[1] !== '');
    openDrawer(`<h3>${esc(g.id)}</h3><div>${capBadges(g.capabilities)} ${licenseTag(g.license, g.licenseKind)}</div>
      ${g.licenseWarning ? `<div class="err" style="margin:6px 0">${esc(g.licenseWarning)}</div>` : ''}
      <table>${rows.map(r => `<tr><th style="text-align:left">${esc(r[0])}</th><td>${esc(String(r[1]))}</td></tr>`).join('')}</table>`);
  } catch (e) { openDrawer(`<span class="err">${esc(e.message)}</span>`); }
}
// Discover filters are remembered between visits
const DISC_KEYS = ['d-sort', 'd-family', 'd-vision', 'd-tools', 'd-reason', 'd-quant', 'd-maxsize', 'd-fits'];
function discSave() { try { localStorage.setItem('discoverFilters', JSON.stringify(Object.fromEntries(DISC_KEYS.map(k => [k, $(k).type === 'checkbox' ? $(k).checked : $(k).value])))); } catch (e) {} }
function discLoad() {
  try {
    const s = JSON.parse(localStorage.getItem('discoverFilters') || '{}');
    DISC_KEYS.forEach(k => { if (k in s) { if ($(k).type === 'checkbox') $(k).checked = !!s[k]; else $(k).value = s[k]; } });
  } catch (e) {}
}
let searchNext = null, searchQuery = '';
function searchParams(cursor) {
  const p = new URLSearchParams({q: searchQuery, sort: $('d-sort').value});
  if ($('d-family').value.trim()) p.set('family', $('d-family').value.trim());
  if ($('d-vision').checked) p.set('vision', '1'); if ($('d-tools').checked) p.set('tools', '1'); if ($('d-reason').checked) p.set('reasoning', '1');
  if (cursor) p.set('cursor', cursor);
  return p.toString();
}
async function search(more) {
  discSave();
  if (!more) { searchQuery = $('q').value.trim(); searchNext = null; }
  const plain = !searchQuery && $('d-sort').value === 'downloads' && !$('d-family').value.trim() && !$('d-vision').checked && !$('d-tools').checked && !$('d-reason').checked;
  if (!more && plain) { DT.view = 'table'; showPicks(); return; }               // nothing asked for: the staff picks
  if (!more) {
    DT.mode = 'search'; DT.view = 'table'; DT.rows = []; DT.info = ''; DT.canMore = false;
    // the Sort box and the column headers say the same thing; a sort by a column Hugging Face cannot do (params, size ...) is kept
    if (!DT.sort.key || DT_SERVER_SORT[DT.sort.key]) { const k = Object.keys(DT_SERVER_SORT).find(k => DT_SERVER_SORT[k] === $('d-sort').value); DT.sort = {key: k || null, dir: -1}; }
    $('results').innerHTML = '<span class="dim">Searching…</span>';
  }
  try {
    const r = await api('/api/models/search?' + searchParams(more ? searchNext : null));
    searchNext = r.next;
    const fresh = r.items.map(searchRow);
    DT.rows = more ? DT.rows.concat(fresh) : fresh;
    DT.canMore = !!searchNext;
    dtRender(); dtLoadSizes(fresh);
  } catch (e) { $('results').innerHTML = `<span class="err">${esc(e.message)}</span>`; }
}
async function refreshCatalog() {
  try { const r = await api('/api/models/catalog/refresh', {method:'POST'}); await renderCatalog(); if (!r.changed) alert('The staff-picks list is already up to date.'); }
  catch (e) { alert(e.message); }
}
$('q').onkeydown = e => { if (e.key === 'Enter') search(); };
discLoad();
async function showFiles(repo) {
  DT.view = 'files';                                                  // the table is hidden until Back
  $('results').innerHTML = '<span class="dim">Loading files…</span>';
  try {
    discSave();
    const fp = new URLSearchParams({repo}); if ($('d-quant').value.trim()) fp.set('quant', $('d-quant').value.trim()); if ($('d-maxsize').value) fp.set('maxSize', $('d-maxsize').value); if ($('d-fits').value) fp.set('fits', $('d-fits').value);
    const f = await api('/api/models/files?' + fp.toString());
    const vram = (status?.hardware?.gpus || []).reduce((a, g) => Math.max(a, g.usableBytes ?? g.vramBytes), 0);
    $('results').innerHTML = `<button class="b sec" onclick="dtBack()">← Back to the list</button><h3>${esc(repo)}</h3><table><tr><th>Quant</th><th>Size</th><th>File</th><th>Fit</th><th></th></tr>` + f.map(x =>
      `<tr><td>${esc(x.quant)}</td><td>${fmt(x.sizeBytes)}</td><td>${esc(x.fileName)}${x.isSplit ? ' <span class="tag">split</span>' : ''}${x.vision ? ` <span class="tag">vision +${fmt(x.mmprojBytes)}</span>` : ''}</td>
       <td>${x.fitsGpu ? '<span class="ok">fits GPU</span>' : x.fitsRam ? '<span class="dim">fits RAM</span>' : '<span class="err">too large</span>'}</td>
       <td><button class="b" onclick='dl(${JSON.stringify(repo)}, ${JSON.stringify(x.fileName)})'>Download</button></td></tr>`).join('') + '</table>';
  } catch (e) { $('results').innerHTML = '<button class="b sec" onclick="dtBack()">← Back to the list</button> ' + errHtml(e, 'showFiles(' + JSON.stringify(repo).replace(/"/g, "&quot;") + ')'); }
}
async function dl(repo, file, quant) {
  try { await api('/api/downloads', {method:'POST', body:{repo, file, quant}}); refreshDownloads(); }
  catch (e) { alert(e.message); }
}
const eta = s => { if (!isFinite(s) || s <= 0) return ''; s = Math.round(s); const m = Math.floor(s / 60); return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m left` : m ? `${m}m ${s % 60}s left` : `${s}s left`; };
const isActive = j => j.state === 'queued' || j.state === 'downloading';
const isOpen = j => isActive(j) || j.state === 'paused';
let lastJobStates = {};
function downloadCard(j) {
  const pct = (j.progress * 100).toFixed(1);
  const speed = j.state === 'downloading' && j.bytesPerSecond > 0 && j.totalBytes ? ` · ${fmt(j.bytesPerSecond)}/s · ${eta((j.totalBytes - j.bytesDone) / j.bytesPerSecond)}` : '';
  const color = j.state === 'completed' ? 'var(--ok)' : (j.state === 'failed' || j.state === 'cancelled' || j.state === 'needs-access') ? 'var(--acc)' : 'var(--acc2)';
  const btn = [];
  if (isActive(j)) btn.push(`<button class="b sec" onclick="pauseDl('${j.id}')">Pause</button><button class="b red" onclick="cancelDl('${j.id}')">Cancel</button>`);
  if (j.state === 'needs-access') btn.push(`<button class="b" onclick='openExternal(${JSON.stringify(j.accessUrl)})'>Open on Hugging Face</button>`);
  if (j.state === 'paused' || j.state === 'failed' || j.state === 'cancelled' || j.state === 'needs-access') btn.push(`<button class="b" onclick="resumeDl('${j.id}')">${j.state === 'paused' ? 'Resume' : 'Retry'}</button>`);
  if (!isActive(j)) btn.push(`<button class="b sec" title="Remove from the list (partial data kept unless you cancel and remove)" onclick="removeDl('${j.id}')">✕</button>`);
  return `<div class="card"><div class="row"><b style="flex:1;word-break:break-all">${esc(j.repo)} / ${esc(j.fileName)}</b>
    <span class="${j.state === 'completed' ? 'ok' : (j.state === 'failed' || j.state === 'needs-access') ? 'err' : 'dim'}">${j.state === 'needs-access' ? 'needs access' : j.state}${isOpen(j) && j.totalBytes ? ' ' + pct + '%' : ''}</span>${btn.join('')}</div>
    <div class="bar" style="margin:6px 0"><i style="width:${pct}%;background:${color}"></i></div>
    <div class="dim">${fmt(j.bytesDone)} / ${j.totalBytes ? fmt(j.totalBytes) : '?'}${speed}${j.verified ? ' · <span class="ok">SHA-256 verified</span>' : ''}
      ${j.note ? ' · ' + esc(j.note) : ''} ${j.error ? '<span class="err">' + esc(j.error) + '</span>' : ''}</div></div>`;
}
async function pauseDl(id) { await api('/api/downloads/' + id + '/pause', {method:'POST'}); refreshDownloads(); }
async function resumeDl(id) { await api('/api/downloads/' + id + '/resume', {method:'POST'}); refreshDownloads(); }
async function removeDl(id) { await api('/api/downloads/' + id + '?remove=1', {method:'DELETE'}); refreshDownloads(); }
async function downloadInput() {
  const input = $('dl-input').value.trim(); if (!input) return;
  $('dl-msg').className = 'dim'; $('dl-msg').textContent = 'Looking it up…';
  try {
    const ref = await api('/api/models/resolve?input=' + encodeURIComponent(input));
    if (ref.kind === 'repo' && !ref.quant) { $('dl-msg').textContent = 'Pick a file from this repository:'; await showFiles(ref.repo); return; }
    const job = await api('/api/downloads', {method:'POST', body:{input}});
    $('dl-msg').textContent = 'Started ' + job.fileName; $('dl-input').value = ''; refreshDownloads();
  } catch (e) { $('dl-msg').className = 'err'; $('dl-msg').textContent = e.message; }
}
async function importModel() {
  const path = $('imp-path').value.trim(); if (!path) return;
  $('imp-msg').className = 'dim'; $('imp-msg').textContent = 'Importing…';
  try {
    const r = await api('/api/models/import', {method:'POST', body:{path, mode: $('imp-mode').value, publisher: $('imp-pub').value || null, name: $('imp-name').value || null}});
    $('imp-msg').className = 'ok'; $('imp-msg').textContent = 'Imported as ' + r.id; $('imp-path').value = ''; refreshModels();
  } catch (e) { $('imp-msg').className = 'err'; $('imp-msg').textContent = e.message; }
}
async function refreshDownloads() {
  const jobs = await api('/api/downloads').catch(() => null);
  if (!jobs) return;
  const ordered = [...jobs.filter(isOpen), ...jobs.filter(j => !isOpen(j))];
  const finished = jobs.length - jobs.filter(isOpen).length;
  const html = ordered.length
    ? ordered.map(downloadCard).join('') + (finished ? '<button class="b sec" onclick="clearDownloads()">Clear finished</button>' : '')
    : '<span class="dim">No downloads</span>';
  if ($('downloads').innerHTML !== html) $('downloads').innerHTML = html;
  if ($('mydownloads').innerHTML !== html) $('mydownloads').innerHTML = html;

  const active = jobs.filter(isActive).length;
  const nav = document.querySelector('nav button[data-tab="models"]');
  nav.textContent = active ? `My Models (${active} ↓)` : 'My Models';

  // a download just finished: refresh the model list if it is visible
  let justDone = false;
  for (const j of jobs) { if (j.state === 'completed' && lastJobStates[j.id] && lastJobStates[j.id] !== 'completed') justDone = true; }
  lastJobStates = Object.fromEntries(jobs.map(j => [j.id, j.state]));
  if (justDone && $('models').classList.contains('on')) refreshModels();
}
async function cancelDl(id) { await api('/api/downloads/' + id, {method:'DELETE'}); refreshDownloads(); }
async function clearDownloads() { await api('/api/downloads/clear', {method:'POST'}); refreshDownloads(); }

