// ---------------------------------------------------------------- chat
let conv = newConv(), abort = null;
function newConv() { return {id: Math.random().toString(16).slice(2, 14), title: 'New chat', messages: [], all: [], docs: [], persisted: false, preset: null}; }
function newChat() { conv = newConv(); editing = null; renderMsgs(); renderAttachments(); renderChatList(); if (typeof refreshEffective === 'function') refreshEffective(); }
// ---- conversation tree: conv.all holds every message of every variant (id + parent), conv.messages is the active branch
const rid = () => Math.random().toString(16).slice(2, 12);
function addMsg(m) {
  m.id = m.id || rid();
  m.parent = conv.messages.length ? conv.messages[conv.messages.length - 1].id : null;
  conv.messages.push(m); conv.all.push(m);
  return m;
}
const childrenOf = pid => conv.all.filter(n => (n.parent ?? null) === (pid ?? null));
function setBranchVia(node) {            // make `node` part of the active branch: its ancestors, itself, its newest descendants
  const up = []; let p = node;
  while (p) { up.unshift(p); p = conv.all.find(n => n.id === p.parent); }
  const down = []; let cur = node;
  for (;;) { const ch = childrenOf(cur.id); if (!ch.length) break; cur = ch[ch.length - 1]; down.push(cur); }
  conv.messages = [...up, ...down];
}
function switchVariant(id, delta) {
  const m = conv.all.find(n => n.id === id); if (!m || abort) return;
  const sibs = childrenOf(m.parent); const i = sibs.indexOf(m) + delta;
  if (i < 0 || i >= sibs.length) return;
  setBranchVia(sibs[i]); renderMsgs(); persist();
}
function fromStored(list, leaf) {        // stored messages -> tree (chats saved without ids are one linear branch)
  let prev = null;
  const all = list.map(m => {
    const node = {id: m.id || rid(), parent: m.id ? (m.parentId ?? null) : prev, role: m.role, content: m.content, reasoning: m.reasoning || '',
      toolCalls: m.toolCalls || undefined, toolCallId: m.toolCallId, name: m.name, images: m.images || [],
      stats: m.stats || null, meta: m.stats ? '' : m.tokensPerSecond ? `${m.completionTokens} tokens · ${m.tokensPerSecond} tok/s` : ''};
    prev = node.id; return node;
  });
  return {all, leaf: leaf && all.some(n => n.id === leaf) ? leaf : all.length ? all[all.length - 1].id : null};
}
let editing = null;
async function regenerate(id) {
  if (abort) return;
  const i = conv.messages.findIndex(m => m.id === id); if (i < 0) return;
  conv.messages = conv.messages.slice(0, i);          // the new answer becomes a sibling of the old one
  renderMsgs(); await generateReply();
}
function startEdit(id) { if (abort) return; editing = id; renderMsgs(); }
function cancelEdit() { editing = null; renderMsgs(); }
async function saveEdit(id, resubmit) {
  const m = conv.all.find(n => n.id === id); const text = document.getElementById('edit-' + id)?.value ?? m.content;
  editing = null;
  if (m.role === 'user' && resubmit) {
    const i = conv.messages.findIndex(x => x.id === id);
    conv.messages = conv.messages.slice(0, i);        // the edited message is a new sibling; the old branch stays reachable
    addMsg({role: 'user', content: text, images: m.images ? [...m.images] : []});
    renderMsgs(); await generateReply();
  } else { m.content = text; renderMsgs(); persist(); }
}
function deleteMsg(id) {
  if (abort) return;
  const m = conv.all.find(n => n.id === id); if (!m) return;
  const doomed = new Set([id]);
  for (let grew = true; grew;) { grew = false; for (const n of conv.all) if (!doomed.has(n.id) && doomed.has(n.parent)) { doomed.add(n.id); grew = true; } }
  if (!confirm(doomed.size > 1 ? `Delete this message and the ${doomed.size - 1} message(s) that follow it?` : 'Delete this message?')) return;
  conv.all = conv.all.filter(n => !doomed.has(n.id));
  if (conv.messages.some(x => doomed.has(x.id))) {
    const sibs = childrenOf(m.parent);
    if (sibs.length) setBranchVia(sibs[sibs.length - 1]);
    else conv.messages = conv.messages.filter(x => !doomed.has(x.id));
  }
  renderMsgs(); persist();
}
function md(t) {
  let h = esc(t);
  h = h.replace(/&lt;think&gt;([\s\S]*?)(&lt;\/think&gt;|$)/g, (_, a) => `<div class="think">${a}</div>`);
  h = h.replace(/```(\w*)\n([\s\S]*?)(```|$)/g, (_, l, c) => `<pre><code>${c}</code></pre>`);
  h = h.replace(/`([^`\n]+)`/g, '<code>$1</code>').replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>');
  return h;
}
function renderMsgs() {
  const busy = !!abort;
  $('msgs').innerHTML = conv.messages.map((m, i) => {
    if (m.role === 'tool') return `<div class="msg tool"><div class="who">Tool result · ${esc(m.name || '')}</div><details><summary class="dim">${esc((m.content || '').slice(0, 90))}</summary><pre>${esc(m.content)}</pre></details></div>`;
    const calls = (m.toolCalls || []).map(c => `<div class="toolcall">🔧 <b>${esc(c.name)}</b> <code>${esc(c.arguments)}</code></div>`).join('');
    const sibs = childrenOf(m.parent), k = sibs.indexOf(m);
    const nav = sibs.length > 1 ? `<span class="variants"><button onclick="switchVariant('${m.id}',-1)" ${k === 0 || busy ? 'disabled' : ''}>‹</button> ${k + 1} / ${sibs.length} <button onclick="switchVariant('${m.id}',1)" ${k === sibs.length - 1 || busy ? 'disabled' : ''}>›</button></span>` : '';
    const acts = busy ? '' : `<span class="acts">${m.role === 'assistant' ? `<button title="Regenerate (keeps this answer as a variant)" onclick="regenerate('${m.id}')">↻</button>` : ''}<button title="Edit${m.role === 'user' ? ' and resubmit' : ''}" onclick="startEdit('${m.id}')">✎</button><button title="Delete" onclick="deleteMsg('${m.id}')">🗑</button></span>`;
    let body;
    if (editing === m.id) {
      body = `<textarea id="edit-${m.id}" rows="4" style="margin-top:4px">${esc(m.content)}</textarea>
        <div class="row" style="margin-top:4px">${m.role === 'user' ? `<button class="b" onclick="saveEdit('${m.id}', true)">Save &amp; resubmit</button>` : ''}<button class="b sec" onclick="saveEdit('${m.id}', false)">Save</button><button class="b sec" onclick="cancelEdit()">Cancel</button></div>`;
    } else body = `${m.images?.length ? `<div class="thumbs">${m.images.map(u => `<img src="${u}">`).join('')}</div>` : ''}${renderReasoning(m, i === conv.messages.length - 1)}${m.content || !calls ? `<div class="body ${m.role === 'assistant' ? 'md' : ''}">${m.role === 'assistant' ? renderAnswer(m.content) : md(m.content)}</div>` : ''}${calls}`;
    const src = m.sources?.length ? `<details class="sources"><summary class="dim">Sources (${m.sources.length})</summary>${m.sources.map(s => `<div class="toolcall"><b>[${s.n}]</b> ${esc(s.doc)}${s.chunk ? ' · part ' + s.chunk : ''}<br><span class="dim">${esc(s.preview)}…</span></div>`).join('')}</details>` : '';
    return `<div class="msg ${m.role}" data-msg="${m.id}"><div class="who">${m.role === 'user' ? 'You' : 'Assistant'} ${nav}${acts}</div>${body}${src}${msgFooter(m)}</div>`;
  }).join('');
  $('msgs').scrollTop = $('msgs').scrollHeight;
  if (typeof scheduleCtxBar === 'function') scheduleCtxBar();
}
async function persist() {
  const body = {title: conv.title, model: $('p-model').value || null, systemPrompt: $('p-system').value || null, generation: genForServer(), settings: {preset: conv.preset || null, generation: genForServer()},
    activeLeaf: conv.messages.length ? conv.messages[conv.messages.length - 1].id : null,
    documents: (conv.docs || []).map(d => ({id:d.id, name:d.name, chars:d.chars})),
    messages: conv.all.map(m => ({id:m.id, parentId:m.parent, role:m.role, content:m.content, reasoning:m.reasoning || null, images:m.images?.length ? m.images : null,
      toolCalls:m.toolCalls?.length ? m.toolCalls : null, toolCallId:m.toolCallId || null, name:m.role === 'tool' ? m.name : null, stats:m.stats || null}))};
  const saved = await api('/api/chats/' + conv.id, {method:'PUT', body});
  conv.title = saved.title; conv.persisted = true; renderChatList(); refreshEffective();
}
// ---------------------------------------------------------------- image attachments
let pendingImages = [];
function renderAttachments() {
  const el = $('attachments');
  const cur = status?.loaded?.find(m => m.id === $('p-model').value) || status?.loaded?.find(m => !m.isEmbedding);
  const warn = pendingImages.length && cur && !cur.vision ? '<span class="err">The selected model has no vision support (needs an mmproj file).</span>' : '';
  const docs = (conv.docs || []);
  el.style.display = pendingImages.length || docs.length || uploading ? 'flex' : 'none';
  el.innerHTML = pendingImages.map((u, i) => `<div class="att"><img src="${u}"><button onclick="removeImage(${i})" title="Remove">×</button></div>`).join('') +
    docs.map((d, i) => `<span class="tag" style="padding:4px 8px" title="${fmt(d.chars)} characters of text">📄 ${esc(d.name)} <a href="#" onclick="removeDoc(${i});return false" style="color:var(--acc);text-decoration:none">×</a></span>`).join('') +
    (uploading ? '<span class="dim">reading document…</span>' : '') + warn;
}
let uploading = false;
function removeDoc(i) { conv.docs.splice(i, 1); renderAttachments(); persist(); }
const DOC_EXT = /\.(pdf|docx|txt|md|markdown|csv|json|log|xml|html?|ya?ml|ini|cfg|py|cs|js|ts|java|c|cpp|h|go|rs|sql)$/i;
async function addDocument(file) {
  uploading = true; renderAttachments();
  try {
    const data = await new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(r.result); r.onerror = rej; r.readAsDataURL(file); });
    const d = await api('/api/documents', {method:'POST', body:{name: file.name, data}});
    (conv.docs ||= []).push({id: d.id, name: d.name, chars: d.chars});
    persist();
  } catch (e) { alert('Could not read ' + file.name + ': ' + e.message); }
  uploading = false; renderAttachments();
}
function removeImage(i) { pendingImages.splice(i, 1); renderAttachments(); }
function fileToDataUrl(file) {
  // downscale large images (keeps the payload and the number of image tokens reasonable)
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onerror = reject;
    r.onload = () => {
      const img = new Image();
      img.onerror = () => resolve(r.result);
      img.onload = () => {
        const max = 1344, s = Math.min(1, max / Math.max(img.width, img.height));
        if (s >= 1) return resolve(r.result);
        const c = document.createElement('canvas'); c.width = Math.round(img.width * s); c.height = Math.round(img.height * s);
        c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
        resolve(c.toDataURL('image/jpeg', 0.9));
      };
      img.src = r.result;
    };
    r.readAsDataURL(file);
  });
}
async function addImageFiles(files) {
  for (const f of files) {
    if (!f.type.startsWith('image/')) { if (DOC_EXT.test(f.name)) await addDocument(f); continue; }
    if (pendingImages.length >= 8) { alert('At most 8 images per message.'); break; }
    pendingImages.push(await fileToDataUrl(f));
  }
  renderAttachments();
}
$('attach').onclick = () => $('file').click();
$('file').onchange = async e => { await addImageFiles(e.target.files); e.target.value = ''; };
$('input').addEventListener('paste', e => {
  const files = [...(e.clipboardData?.files || [])].filter(f => f.type.startsWith('image/') || DOC_EXT.test(f.name));
  if (files.length) { e.preventDefault(); addImageFiles(files); }
});
$('conv').addEventListener('dragover', e => e.preventDefault());
$('conv').addEventListener('drop', e => { if (e.dataTransfer?.files?.length) { e.preventDefault(); addImageFiles(e.dataTransfer.files); } });
$('p-model').addEventListener('change', renderAttachments);
// per-model inference defaults are applied to the parameter panel when a model is picked
$('p-model').addEventListener('change', async () => {
  const id = $('p-model').value; if (!id) return;
  try { const d = await api('/api/models/defaults?id=' + encodeURIComponent(id)); if (d.hasGeneration) applyGen(d.generation); } catch {}
});

// rolling tokens/second over the last 10 s, refreshed once a second while generating
let tokTimes = [], tpsTimer = null;
function updateTps() {
  const now = performance.now();
  while (tokTimes.length && now - tokTimes[0] > 10000) tokTimes.shift();
  const el = $('tps');
  if (!tokTimes.length) { el.textContent = 'Last 10 s: waiting for tokens…'; return; }
  const span = Math.min(10000, now - tokTimes[0]) / 1000;
  el.textContent = `Last 10 s avg: ${(tokTimes.length / Math.max(span, 1)).toFixed(1)} tok/s`;
}
function startTps() {
  tokTimes = []; $('tps').style.display = ''; updateTps();
  clearInterval(tpsTimer); tpsTimer = setInterval(updateTps, 1000);
}
function stopTps() { clearInterval(tpsTimer); tpsTimer = null; $('tps').style.display = 'none'; }

// messages as the API wants them (assistant tool calls and tool results included)
function apiMessages(list, extraSystem) {
  const msgs = [];
  const sys = [$('p-system').value.trim(), extraSystem].filter(Boolean).join('\n\n');
  if (sys) msgs.push({role:'system', content:sys});
  for (const m of list) {
    if (m.role === 'tool') { msgs.push({role:'tool', tool_call_id:m.toolCallId, name:m.name, content:m.content}); continue; }
    if (m.role === 'assistant' && m.toolCalls?.length) {
      msgs.push({role:'assistant', content:m.content || null,
        tool_calls:m.toolCalls.map(c => ({id:c.id, type:'function', function:{name:c.name, arguments:c.arguments}}))});
      continue;
    }
    msgs.push({role:m.role, content: m.images?.length
      ? [{type:'text', text:m.content}, ...m.images.map(u => ({type:'image_url', image_url:{url:u}}))] : m.content});
  }
  return msgs;
}

// MCP: tools offered to the model, and the user's per-call confirmation
let mcpAlwaysAllow = new Set();
async function mcpTools() {
  if (!$('p-mcp').checked) return null;
  try {
    const r = await api('/api/mcp/tools');
    if (r.errors?.length) console.warn('MCP servers failed:', r.errors);
    return r.tools?.length ? r.tools : null;
  } catch (e) { console.warn(e); return null; }
}
function confirmToolCall(call) {
  if ($('p-mcp-confirm').value === 'never' || mcpAlwaysAllow.has(call.name)) return Promise.resolve('allow');
  return new Promise(resolve => {
    $('tc-name').textContent = call.name;
    let args = call.arguments; try { args = JSON.stringify(JSON.parse(call.arguments), null, 2); } catch {}
    $('tc-args').textContent = args;
    const done = v => { $('toolcalldlg').close(); resolve(v); };
    $('tc-allow').onclick = () => done('allow');
    $('tc-always').onclick = () => { mcpAlwaysAllow.add(call.name); done('allow'); };
    $('tc-deny').onclick = () => done('deny');
    $('toolcalldlg').oncancel = () => resolve('deny');
    $('toolcalldlg').showModal();
  });
}

// one streamed completion into `reply`; returns the tool calls the model made (if any)
async function streamReply(reply, msgs, params, tools) {
  const headers = {'Content-Type':'application/json'}; if (apiKey) headers['Authorization'] = 'Bearer ' + apiKey;
  const body = Object.assign({model: $('p-model').value || undefined, messages: msgs, stream: true, stream_options:{include_usage:true}}, params);
  if (tools) body.tools = tools;
  const r = await fetch('/v1/chat/completions', {method:'POST', headers, signal: abort.signal, body: JSON.stringify(body)});
  if (!r.ok) throw new Error((await r.json()).error?.message || r.statusText);
  const reader = r.body.getReader(), dec = new TextDecoder(); let buf = '';
  const calls = [];
  while (true) {
    const {done, value} = await reader.read(); if (done) break;
    buf += dec.decode(value, {stream:true});
    let i;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const line = buf.slice(0, i).trim(); buf = buf.slice(i + 2);
      if (!line.startsWith('data:')) continue;
      const d = line.slice(5).trim(); if (d === '[DONE]') continue;
      const j = JSON.parse(d);
      const dl = j.choices?.[0]?.delta;
      if (dl?.reasoning_content) { reply.thinkStart ??= performance.now(); reply.reasoning = (reply.reasoning || '') + dl.reasoning_content; tokTimes.push(performance.now()); renderMsgs(); }
      if (dl?.content) { if (reply.thinkStart && reply.thinkMs == null) reply.thinkMs = performance.now() - reply.thinkStart; reply.content += dl.content; tokTimes.push(performance.now()); renderMsgs(); }
      for (const tc of dl?.tool_calls || []) {
        const c = calls[tc.index] ??= {id:'', name:'', arguments:''};
        if (tc.id) c.id = tc.id;
        if (tc.function?.name) c.name = tc.function.name;
        if (tc.function?.arguments) c.arguments += tc.function.arguments;
        reply.toolCalls = calls.filter(Boolean); renderMsgs();
      }
      if (j.stats) reply.stats = {tokens: j.stats.completion_tokens, promptTokens: j.stats.prompt_tokens, tokensPerSecond: j.stats.tokens_per_second, ttftSeconds: j.stats.time_to_first_token, generationSeconds: j.stats.generation_time, stopReason: j.stats.finish_reason, promptCachedTokens: j.stats.cached_prompt_tokens, draftTokens: j.stats.draft_tokens, draftAccepted: j.stats.draft_accepted, thinkSeconds: reply.thinkMs ? +(reply.thinkMs / 1000).toFixed(1) : undefined};
      if (j.stats) reply.meta = `${j.usage?.completion_tokens ?? ''} tokens · ${j.stats.tokens_per_second} tok/s · first token ${j.stats.time_to_first_token}s` + (j.stats.cached_prompt_tokens ? ` · ${j.stats.cached_prompt_tokens} prompt tokens cached` : '');
    }
  }
  return calls.filter(Boolean);
}

// generate the assistant's answer after the last message; runs MCP tools (with confirmation) until it answers
async function generateReply() {
  let params; try { params = genParams(); } catch { return; }
  abort = new AbortController();
  $('send').style.display = 'none'; $('stop').style.display = '';
  startTps();
  try {
    const tools = await mcpTools();
    for (let round = 0; round < 8; round++) {
      const reply = {role:'assistant', content:''};
      addMsg(reply); renderMsgs();
      // attached documents: the whole text when it fits, otherwise the passages that match the last question
      let docContext = '';
      if (conv.docs?.length) {
        try {
          const lastUser = [...conv.messages].reverse().find(m => m.role === 'user');
          const ctxr = await api('/api/documents/context', {method:'POST', body:{ids: conv.docs.map(d => d.id), query: lastUser?.content || '', model: $('p-model').value || null}});
          docContext = ctxr.text || '';
          if (ctxr.mode === 'rag') reply.sources = ctxr.citations;
        } catch (e) { console.warn('document context failed', e); }
      }
      const calls = await streamReply(reply, apiMessages(conv.messages.slice(0, -1), docContext), params, tools);
      if (!calls.length || !tools) { delete reply.toolCalls; break; }
      reply.toolCalls = calls;
      for (const c of calls) {
        if (!c.id) c.id = 'call_' + Math.random().toString(16).slice(2, 12);
        const verdict = await confirmToolCall(c);
        let out;
        if (verdict === 'deny') out = 'The user declined this tool call.';
        else {
          try {
            const res = await api('/api/mcp/call', {method:'POST', body:{name:c.name, arguments:c.arguments}});
            out = res.content || '(no output)';
            if (res.isError) out = 'Tool error: ' + out;
          } catch (e) { out = 'Tool call failed: ' + e.message; }
        }
        addMsg({role:'tool', toolCallId:c.id, name:c.name, content:out});
      }
      renderMsgs();
    }
  } catch (e) {
    const last = conv.messages[conv.messages.length - 1];
    if (e.name !== 'AbortError' && last) last.content = (last.content || '') + `\n[error: ${e.message}]`;
  }
  abort = null; $('send').style.display = ''; $('stop').style.display = 'none';
  stopTps();
  renderMsgs(); refreshStatus();
  if (conv.messages.some(m => m.role === 'assistant' && m.content)) persist().then(maybeAutoTitle);
}

async function send() {
  const text = $('input').value.trim(); if ((!text && !pendingImages.length) || abort) return;
  try { genParams(); } catch { return; }
  $('input').value = '';
  addMsg({role:'user', content:text, images:[...pendingImages]});
  pendingImages = []; renderAttachments();
  await generateReply();
}
$('send').onclick = send;
$('stop').onclick = () => abort && abort.abort();
$('input').onkeydown = e => { if (e.key === 'Enter' && !e.shiftKey && !e.ctrlKey && !e.metaKey) { e.preventDefault(); send(); } };

