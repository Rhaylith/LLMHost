// ---------------------------------------------------------------- split view and compare: 2-4 panes, one input, every pane is an ordinary chat
// Each pane has its own model and a few parameters and streams its own answer. With "send to all" one prompt fans out to every pane (panes on the
// same model run in parallel on the server, different models are loaded on demand); a table then compares speed and first-token time.
const MAX_PANES = 4;
let panes = [], compareOn = false, focusedPane = 0, paneModels = [], paneWarned = new Set();

const paneId = () => Math.random().toString(16).slice(2, 14).padEnd(12, '0');
function newPane(model = '') { return {chatId: paneId(), model, temperature: '', maxTokens: '', system: '', messages: [], busy: false, ctl: null, stats: null, state: ''}; }
function savePaneLayout() { try { localStorage.setItem('panesLayout', JSON.stringify({on: compareOn, panes: panes.map(p => ({chatId: p.chatId, model: p.model, temperature: p.temperature, maxTokens: p.maxTokens, system: p.system}))})); } catch (e) {} }

async function toggleCompare(force) {
  compareOn = force ?? !compareOn;
  $('chat').classList.toggle('compare', compareOn);
  $('panes').style.display = compareOn ? 'flex' : 'none';
  if (compareOn) {
    await loadPaneModels();
    if (!panes.length) {
      let saved = null; try { saved = JSON.parse(localStorage.getItem('panesLayout') || 'null'); } catch (e) {}
      if (saved?.panes?.length) {
        for (const s of saved.panes) {
          const p = Object.assign(newPane(), s);
          const c = await api('/api/chats/' + p.chatId).catch(() => null);
          if (c) p.messages = (c.messages || []).map(m => ({id: m.id, role: m.role, content: m.content, reasoning: m.reasoning || '', stats: m.stats || null}));
          panes.push(p);
        }
      } else { panes = [newPane($('p-model').value || ''), newPane($('p-model').value || '')]; }
    }
    drawPanes();
  }
  savePaneLayout();
}
async function loadPaneModels() {
  const loaded = (status?.loaded || []).filter(m => !m.isEmbedding).map(m => ({id: m.id, label: m.id, loaded: true}));
  const local = await api('/api/models/local').catch(() => []);
  const rest = local.filter(m => !m.isEmbedding && !m.loadedAs).map(m => ({id: m.id, label: m.id + ' (loads on demand)', loaded: false}));
  paneModels = [...loaded, ...rest];
}
function addPane() { if (panes.length >= MAX_PANES) return; panes.push(newPane(panes[panes.length - 1]?.model || '')); drawPanes(); savePaneLayout(); }
function closePane(i) { if (panes.length <= 1) { toggleCompare(false); return; } panes[i].ctl?.abort(); panes.splice(i, 1); focusedPane = Math.min(focusedPane, panes.length - 1); drawPanes(); savePaneLayout(); }

function paneHeader(p, i) {
  const opts = ['<option value="">(first loaded model)</option>', ...paneModels.map(m => `<option value="${esc(m.id)}" ${m.id === p.model ? 'selected' : ''}>${esc(m.label)}</option>`)].join('');
  return `<div class="pane-head"><select data-p="${i}" data-f="model" title="Model of this pane">${opts}</select>
    <input data-p="${i}" data-f="temperature" type="number" step="0.1" min="0" max="2" placeholder="temp" value="${esc(p.temperature)}" title="Temperature (blank = default)">
    <input data-p="${i}" data-f="maxTokens" type="number" min="1" placeholder="max tok" value="${esc(p.maxTokens)}" title="Max tokens (blank = default)">
    <button class="b sec" title="Close this pane" onclick="closePane(${i})">✕</button></div>
    <input class="pane-sys" data-p="${i}" data-f="system" placeholder="System prompt of this pane" value="${esc(p.system)}">`;
}
function paneBody(p) {
  return p.messages.map(m => m.role === 'user'
    ? `<div class="msg user"><div class="body">${esc(m.content)}</div></div>`
    : `<div class="msg assistant">${m.reasoning ? `<details class="think"><summary>Reasoning</summary><div class="think-body">${esc(m.reasoning)}</div></details>` : ''}<div class="body md">${renderAnswer(m.content)}</div></div>`).join('')
    + (p.state ? `<div class="dim" style="padding:4px 0">${esc(p.state)}</div>` : '');
}
function paneFooter(p) {
  const s = p.stats; if (!s) return '';
  return `${s.tokens ?? '?'} tokens · ${s.tokensPerSecond ?? '?'} tok/s · first token ${s.ttftSeconds ?? '?'}s${s.stopReason ? ' · ' + esc(s.stopReason) : ''}`;
}
function drawPanes() {
  $('panes-grid').style.gridTemplateColumns = `repeat(${panes.length}, minmax(0, 1fr))`;
  $('panes-grid').innerHTML = panes.map((p, i) => `<div class="pane ${i === focusedPane ? 'focus' : ''}" data-i="${i}">${paneHeader(p, i)}
    <div class="pane-msgs" id="pane-msgs-${i}">${paneBody(p)}</div>
    <div class="pane-foot dim" id="pane-foot-${i}">${paneFooter(p)}</div>
    <div class="row" style="padding:4px 8px"><button class="b sec" onclick="keepPane(${i})" title="Continue this conversation in the normal chat view">Keep this</button></div></div>`).join('');
  $('panes-grid').querySelectorAll('.pane').forEach(el => el.onclick = () => { focusedPane = +el.dataset.i; $('panes-grid').querySelectorAll('.pane').forEach(x => x.classList.toggle('focus', x === el)); });
  $('panes-grid').querySelectorAll('[data-f]').forEach(el => el.onchange = async () => {
    const p = panes[+el.dataset.p]; p[el.dataset.f] = el.value; savePaneLayout();
    if (el.dataset.f === 'model') await warnForModel(p.model);
  });
  $('add-pane').style.display = panes.length >= MAX_PANES ? 'none' : '';
  drawDiff();
}
function updatePane(i) {
  const p = panes[i]; const box = $('pane-msgs-' + i); if (!box) return;
  const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
  box.innerHTML = paneBody(p); if (atBottom) box.scrollTop = box.scrollHeight;
  $('pane-foot-' + i).innerHTML = paneFooter(p);
}

// a model that is not loaded yet needs memory: ask the estimate before loading a second one next to the first
async function warnForModel(id) {
  if (!id || paneWarned.has(id) || (status?.loaded || []).some(m => m.id === id)) return true;
  paneWarned.add(id);
  try {
    const e = await api('/api/models/estimate?id=' + encodeURIComponent(id));
    const others = (status?.loaded || []).length;
    if (e.verdict !== 'fits' || others > 0) return confirm(`${id}: ${e.summary}\n\nThis model is not loaded and ${others} other model(s) are using memory. Load it anyway?`);
  } catch (err) { /* the estimate is only advice */ }
  return true;
}

// ---- one pane's streamed answer
async function streamPane(i, text) {
  const p = panes[i];
  p.messages.push({id: rid(), role: 'user', content: text});
  const reply = {id: rid(), role: 'assistant', content: '', reasoning: '', stats: null};
  p.messages.push(reply);
  const history = p.messages.slice(0, -1).map(m => ({role: m.role, content: m.content}));
  const msgs = [...(p.system ? [{role: 'system', content: p.system}] : []), ...history];
  const body = {model: p.model || undefined, messages: msgs, stream: true, stream_options: {include_usage: true}};
  if (p.temperature !== '') body.temperature = Number(p.temperature);
  if (p.maxTokens !== '') body.max_tokens = Number(p.maxTokens);
  p.busy = true; p.stats = null; p.ctl = new AbortController(); p.state = (status?.loaded || []).some(m => m.id === p.model) || !p.model ? 'Waiting for the first token…' : 'Loading the model…';
  const t0 = performance.now(); let tFirst = 0, n = 0;
  updatePane(i);
  try {
    const headers = {'Content-Type': 'application/json'}; if (apiKey) headers['Authorization'] = 'Bearer ' + apiKey;
    const r = await fetch('/v1/chat/completions', {method: 'POST', headers, signal: p.ctl.signal, body: JSON.stringify(body)});
    if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error?.message || r.statusText);
    const reader = r.body.getReader(), dec = new TextDecoder(); let buf = '';
    let last = 0;
    for (;;) {
      const {done, value} = await reader.read(); if (done) break;
      buf += dec.decode(value, {stream: true});
      let k;
      while ((k = buf.indexOf('\n\n')) >= 0) {
        const line = buf.slice(0, k).trim(); buf = buf.slice(k + 2);
        if (!line.startsWith('data:')) continue;
        const d = line.slice(5).trim(); if (d === '[DONE]') continue;
        const j = JSON.parse(d); const dl = j.choices?.[0]?.delta;
        if (dl?.reasoning_content) { reply.reasoning += dl.reasoning_content; if (!tFirst) tFirst = performance.now(); }
        if (dl?.content) { reply.content += dl.content; if (!tFirst) tFirst = performance.now(); n++; }
        if (j.stats) reply.stats = {tokens: j.stats.completion_tokens, promptTokens: j.stats.prompt_tokens, tokensPerSecond: j.stats.tokens_per_second, ttftSeconds: j.stats.time_to_first_token,
          generationSeconds: j.stats.generation_time, stopReason: j.stats.finish_reason, promptCachedTokens: j.stats.cached_prompt_tokens, draftTokens: j.stats.draft_tokens, draftAccepted: j.stats.draft_accepted};
        p.state = '';
        if (performance.now() - last > 50) { last = performance.now(); updatePane(i); }
      }
    }
  } catch (e) {
    p.state = e.name === 'AbortError' ? 'Stopped.' : 'Error: ' + e.message;
  }
  p.busy = false; p.ctl = null;
  p.stats = reply.stats || (n ? {tokens: n, tokensPerSecond: '?', ttftSeconds: tFirst ? ((tFirst - t0) / 1000).toFixed(2) : '?'} : null);
  reply.wallSeconds = (performance.now() - t0) / 1000;
  updatePane(i);
  await savePane(i);
}
async function savePane(i) {
  const p = panes[i];
  if (!p.messages.some(m => m.role === 'assistant' && m.content)) return;
  let parent = null;
  const messages = p.messages.map(m => { const o = {id: m.id, parentId: parent, role: m.role, content: m.content, reasoning: m.reasoning || null, stats: m.stats || null}; parent = m.id; return o; });
  try {
    await api('/api/chats/' + p.chatId, {method: 'PUT', body: {title: 'New chat', model: p.model || null, systemPrompt: p.system || null, messages, activeLeaf: parent}});
    renderChatList();
  } catch (e) { p.state = 'Could not save: ' + e.message; }
}

// ---- send to all / to the focused pane, stop, compare table
async function paneSend() {
  const text = $('panes-input').value.trim(); if (!text || panes.some(p => p.busy)) return;
  const targets = $('panes-all').checked ? panes.map((_, i) => i) : [focusedPane];
  for (const i of targets) if (!(await warnForModel(panes[i].model))) return;
  $('panes-input').value = '';
  $('panes-send').style.display = 'none'; $('panes-stop').style.display = '';
  await Promise.all(targets.map(i => streamPane(i, text)));
  $('panes-send').style.display = ''; $('panes-stop').style.display = 'none';
  drawDiff(); savePaneLayout();
}
function paneStop() { panes.forEach(p => p.ctl?.abort()); }
function drawDiff() {
  const rows = panes.map((p, i) => ({i, p, s: p.stats})).filter(r => r.s && r.s.tokens);
  if (rows.length < 2) { $('panes-diff').innerHTML = ''; return; }
  const num = v => Number(v); const best = (key, dir) => { const vals = rows.map(r => num(r.s[key])).filter(Number.isFinite); return vals.length ? (dir > 0 ? Math.max(...vals) : Math.min(...vals)) : null; };
  const fast = best('tokensPerSecond', 1), first = best('ttftSeconds', -1);
  $('panes-diff').innerHTML = `<table><tr><th>Pane</th><th>Model</th><th>Tokens</th><th>Tokens/s</th><th>First token</th><th>Stop</th></tr>` + rows.map(r =>
    `<tr><td>${r.i + 1}</td><td>${esc(r.p.model || '(default)')}</td><td>${r.s.tokens}</td><td class="${num(r.s.tokensPerSecond) === fast ? 'ok' : ''}">${r.s.tokensPerSecond}</td><td class="${num(r.s.ttftSeconds) === first ? 'ok' : ''}">${r.s.ttftSeconds}s</td><td>${esc(r.s.stopReason || '')}</td></tr>`).join('') + '</table>';
}
async function keepPane(i) {
  const p = panes[i];
  await savePane(i);
  await toggleCompare(false);
  await openChat(p.chatId).catch(() => {});
}
$('panes-input').onkeydown = e => { if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey) { e.preventDefault(); paneSend(); } };
try { const s = JSON.parse(localStorage.getItem('panesLayout') || 'null'); if (s?.on) setTimeout(() => toggleCompare(true), 600); } catch (e) {}
