// ---------------------------------------------------------------- settings
async function loadSettings() {
  await refreshStatus();
  const hw = status.hardware;
  let live = null; try { live = await api('/api/hardware'); } catch {}
  const liveGpu = i => live?.gpus?.find(x => x.index === i) || {};
  $('hw').innerHTML = `<div><b>CPU</b> ${esc(hw.cpu)} (${hw.logicalCores} threads) · <b>RAM</b> ${fmt(hw.totalRamBytes)}${live ? ' (' + fmt(live.ramAvailableBytes) + ' available)' : ''}</div>` +
    hw.gpus.map(g => { const l = liveGpu(g.index); return `<div><b>GPU ${g.index}</b> ${esc(g.name)} <span class="tag">${esc(g.vendor)}</span>${g.integrated ? '<span class="tag">integrated</span>' : ''} VRAM ${fmt(g.vramBytes)}${g.integrated && g.sharedBytes ? ' + ' + fmt(g.sharedBytes) + ' shared' : ''}${l.freeBytes ? ' · ' + fmt(l.freeBytes) + ' free' : ''}${l.backendName ? ` <span class="tag">${esc(l.backendName)} · llama #${l.llamaIndex}</span>` : ''}</div>`; }).join('') +
    (hw.npus || []).map(n => `<div><b>NPU</b> ${esc(n.name)} <span class="tag">${esc(n.vendor)} ${esc(n.architecture)}</span> <span class="dim">detected, not used for inference</span></div>`).join('') +
    `<div class="dim" style="margin-top:6px">Active backend: ${esc(status.backend)} ${esc(status.systemInfo || '(initialises when the first model loads)')}</div>`;
  const s = await api('/api/settings');
  $('s-host').value = s.host; $('s-port').value = s.port; $('s-apikey').value = s.apiKey || ''; $('s-backend').value = s.backend;
  $('s-modelsdir').value = s.modelsDirectory || ''; $('s-modelsdir').placeholder = status.modelsDirectory + ' (default: DownloadedModels in the working directory)'; $('s-hftoken').value = s.huggingFaceToken || '';
  $('s-cors').checked = s.cors; $('s-anthropic').checked = s.anthropicApi; $('s-autoload').checked = s.autoLoadOnRequest; const nw = s.network || {}, nl = nw.limits || {}, nt = nw.tls || {}; $('n-allow').value = (nw.allowedNetworks || []).join(String.fromCharCode(10)); $('n-tls').value = nt.mode || 'off'; $('n-tls-port').value = nt.port || 1235; $('n-tls-cert').value = nt.certPath || ''; $('n-tls-key').value = nt.keyPath || ''; $('n-tls-pass').value = nt.pfxPassword || ''; $('n-body').value = nl.maxBodyMb || 32; $('n-upload').value = nl.maxUploadMb || 256; $('n-conn').value = nl.maxConnections || 0; $('n-rl-tok').value = nl.requestsPerMinutePerToken || 0; $('n-rl-ip').value = nl.requestsPerMinutePerIp || 0; $('n-cap-n').value = nl.maxChoices || 0; $('n-cap-t').value = nl.maxTokens || 0; const j = s.jit || {}; $('s-jit').checked = j.enabled !== false; $('s-jit-evict').checked = j.autoEvict !== false; $('s-jit-ttl').value = j.defaultTtlMinutes || 0; $('s-jit-max').value = j.maxLoadedModels || 0; $('s-jit-order').value = j.evictOrder || 'lru';
  $('s-maxdl').value = s.maxConcurrentDownloads || 2;
  $('s-catalog').value = s.catalogUrl || '';   $('s-feed').value = s.updateFeedUrl || ''; $('s-updates').checked = !!s.checkUpdates; $('s-modelupd').checked = !!s.checkModelUpdates; $('s-autotitle').checked = s.autoTitleChats !== false; $('s-logbodies').checked = !!s.logging?.logBodies; $('s-metricspub').checked = !!s.metricsPublic; $('s-autotitle').checked = s.autoTitleChats !== false; $("s-tray").checked = !!s.minimizeToTray; $('s-debug').checked = !!s.debugRequests; $('s-reastext').checked = s.anthropicReasoningAsText !== false;
  $('s-guard').value = s.loadGuardrail || 'balanced'; $('s-dedicated').checked = !!s.limitToDedicatedGpuMemory;
  $('s-showtpl').checked = !!s.alwaysShowTemplate;
  window._settings = s;
  $('s-load').innerHTML = ['contextSize','gpuLayers','threads','batchSize'].map(k => `<div><label>${k}</label><input data-k="${k}" value="${s.defaultLoad[k] ?? ''}"></div>`).join('') +
    `<div><label>device</label><select data-k="device"><option>cpu</option><option>auto</option><option>gpu</option></select></div>`;
  $('s-load').querySelector('select').value = s.defaultLoad.device;
  const base = location.origin;
  $('apiex').textContent = `Base URL: ${base}/v1\n\ncurl ${base}/v1/chat/completions -H "Content-Type: application/json" -d '{\n  "model": "${status.loaded[0]?.id || 'your-model'}",\n  "messages": [{"role":"user","content":"Hello!"}],\n  "stream": false\n}'\n\nEndpoints: GET /v1/models · POST /v1/chat/completions · POST /v1/completions · POST /v1/embeddings

Anthropic Messages API (base URL ${base}):
curl ${base}/v1/messages -H "content-type: application/json" -H "x-api-key: any" -H "anthropic-version: 2023-06-01" -d '{
  "model": "${status.loaded[0]?.id || 'your-model'}",
  "max_tokens": 256,
  "messages": [{"role":"user","content":"Hello!"}]
}'`;
}
// ---------------------------------------------------------------- Claude Code launcher
async function launchClaudeCode(model) {
  const last = localStorage.getItem('claudeDir') || '';
  const dir = prompt('Folder to start Claude Code in (blank = your user folder):', last);
  if (dir === null) return;
  localStorage.setItem('claudeDir', dir);
  try {
    const r = await api('/api/launch/claude-code', {method:'POST', body:{model: model || null, directory: dir || null}});
    alert('PowerShell opened with Claude Code using "' + r.model + '".');
  } catch (e) { alert(e.message); }
}

async function configureClaudeApp(model) {
  const last = localStorage.getItem('claudeAppDir') || '';
  const dir = prompt('Project folder to configure for the Claude Code app / CLI.\nBlank = your user-level settings (affects every project):', last);
  if (dir === null) return;
  localStorage.setItem('claudeAppDir', dir);
  try {
    const r = await api('/api/launch/claude-code/configure', {method:'POST', body:{model: model || null, directory: dir || null}});
    alert('Wrote the settings for "' + r.model + '" to:\n' + r.path + '\n\nOpen ' + (dir ? 'that folder' : 'a project') + ' in the Claude Code app (restart the session if it was already open).' + (r.note ? '\n\n' + r.note : ''));
  } catch (e) { alert(e.message); }
}
async function unconfigureClaudeApp() {
  const dir = prompt('Folder to restore (blank = user-level settings):', localStorage.getItem('claudeAppDir') || '');
  if (dir === null) return;
  try {
    const r = await api('/api/launch/claude-code/unconfigure', {method:'POST', body:{directory: dir || null}});
    alert(r.removed ? 'Removed the LLMHost entries from ' + r.path : 'Nothing to remove in ' + r.path);
  } catch (e) { alert(e.message); }
}

// ---------------------------------------------------------------- help page
function initHelp() {
  const base = location.origin, model = status?.loaded?.find(m => !m.isEmbedding)?.id || 'your-model';
  $('help-api').textContent =
`# OpenAI-compatible
curl ${base}/v1/chat/completions -H "Content-Type: application/json" -d '{
  "model": "${model}",
  "messages": [{"role":"user","content":"Hello!"}],
  "temperature": 0.7,          // sampling
  "max_completion_tokens": 200,
  "reasoning_effort": "off",   // off | low | medium | high
  "stop": ["###"],
  "stream": true
}'

# Tool calling (tool_choice: "auto" | "required" | {"type":"function","function":{"name":"get_weather"}})
  "tools": [{"type":"function","function":{"name":"get_weather","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]

# Structured output
  "response_format": {"type":"json_schema","json_schema":{"schema":{"type":"object","properties":{"answer":{"type":"string"}}}}}

# Other endpoints
POST ${base}/v1/responses           OpenAI Responses API (input, instructions, previous_response_id)
POST ${base}/v1/messages            Anthropic Messages API (thinking, tool_use)
POST ${base}/v1/embeddings
POST ${base}/api/v1/chat            native chat: input, integrations (MCP), reasoning, previous_response_id
POST ${base}/api/v1/models/load     {"model":"…","context_length":8192}
GET  ${base}/api/v1/models          downloaded models and loaded instances`;
}
$('help-filter').addEventListener('input', () => {
  const q = $('help-filter').value.trim().toLowerCase();
  document.querySelectorAll('#help-body .h').forEach(el => {
    el.style.display = !q || (el.dataset.k + ' ' + el.textContent).toLowerCase().includes(q) ? '' : 'none';
  });
});

// ---------------------------------------------------------------- MCP servers
async function loadMcp(connect) {
  $('mcp-msg').textContent = connect ? 'Connecting…' : '';
  let r; try { r = await api('/api/mcp' + (connect ? '?connect=1' : '')); } catch (e) { $('mcp-msg').textContent = e.message; return; }
  if (!connect || !$('mcp-config').value) $('mcp-config').value = r.config;
  $('mcp-servers').innerHTML = r.servers.length ? r.servers.map(s => `<div class="card"><b>${esc(s.name)}</b> <span class="tag">${s.transport}</span>
    <span class="${s.state === 'ready' ? 'ok' : s.state === 'failed' ? 'err' : 'dim'}">${s.state}</span> ${s.error ? `<span class="err">${esc(s.error)}</span>` : ''}
    <div class="dim">${s.tools.length ? s.tools.map(t => `<span class="tag" title="${esc(t.description)}">${esc(t.name)}</span>`).join('') : 'no tools loaded yet'}</div></div>`).join('')
    : '<span class="dim">No servers configured.</span>';
  $('mcp-msg').textContent = connect ? 'Done.' : '';
}
async function saveMcp() {
  try { await api('/api/mcp', {method:'PUT', body:{config: $('mcp-config').value}}); $('mcp-msg').textContent = 'Saved.'; await loadMcp(true); }
  catch (e) { $('mcp-msg').textContent = e.message; $('mcp-msg').className = 'err'; }
}

// ---------------------------------------------------------------- API tokens
async function loadTokens() {
  let r; try { r = await api('/api/tokens'); } catch (e) { $('tokens').innerHTML = `<span class="err">${esc(e.message)}</span>`; return; }
  $('s-auth').checked = r.requireAuth;
  if (!$('tk-perms').children.length)
    $('tk-perms').innerHTML = r.permissions.map(p => `<label style="margin:0 10px 0 0"><input type="checkbox" class="tkp" value="${p}" style="width:auto" ${['chat','embeddings','models:read'].includes(p) ? 'checked' : ''}> ${p}</label>`).join('');
  $('tokens').innerHTML = (r.legacyKey ? '<div class="dim">A single legacy API key is also set (full access).</div>' : '') +
    (r.tokens.length ? '<table><tr><th>Name</th><th>Token</th><th>Permissions</th><th>Last used</th><th></th></tr>' + r.tokens.map(t =>
      `<tr><td>${esc(t.name)}${t.disabled ? ' <span class="tag">disabled</span>' : ''}</td><td><code>${esc(t.prefix)}…</code></td><td>${t.permissions.map(p => `<span class="tag">${esc(p)}</span>`).join('')}</td>
      <td class="dim">${t.lastUsedAt ? new Date(t.lastUsedAt).toLocaleString() : 'never'}</td>
      <td style="white-space:nowrap"><button class="b sec" onclick="toggleToken('${t.id}', ${!t.disabled})">${t.disabled ? 'Enable' : 'Disable'}</button> <button class="b red" onclick="revokeToken('${t.id}')">Revoke</button></td></tr>`).join('') + '</table>'
      : '<span class="dim">No tokens yet.</span>');
}
async function createToken() {
  const name = $('tk-name').value.trim(); if (!name) return;
  const permissions = [...document.querySelectorAll('.tkp:checked')].map(x => x.value);
  try {
    const r = await api('/api/tokens', {method:'POST', body:{name, permissions}});
    $('tk-secret').style.display = ''; $('tk-secret').textContent = 'Token "' + name + '" — copy it now, it is not shown again:\n\n' + r.secret;
    $('tk-name').value = ''; loadTokens();
  } catch (e) { alert(e.message); }
}
async function revokeToken(id) { if (confirm('Revoke this token? Clients using it stop working.')) { await api('/api/tokens/' + id, {method:'DELETE'}); loadTokens(); } }
async function toggleToken(id, disabled) { await api('/api/tokens/' + id, {method:'PUT', body:{disabled}}); loadTokens(); }

