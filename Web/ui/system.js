// ---------------------------------------------------------------- runtimes
async function loadRuntimes() {
  let r; try { r = await api('/api/runtimes'); } catch (e) { $('runtimes').innerHTML = `<span class="err">${esc(e.message)}</span>`; return; }
  const s = r.survey;
  $('runtimes').innerHTML =
    `<div class="dim" style="margin-bottom:6px">This machine: AVX ${s.avx ? '✓' : '✗'} · AVX2 ${s.avx2 ? '✓' : '✗'} · AVX-512 ${s.avx512 ? '✓' : '✗'} · Vulkan loader ${s.vulkan ? '✓' : '✗'} · ${s.gpus} GPU(s)</div>` +
    '<table><tr><th></th><th>Runtime</th><th>Source</th><th>Version</th><th>Size</th><th>Status</th><th></th></tr>' +
    r.runtimes.map(x => `<tr><td>${x.active ? '<span class="ok">● active</span>' : x.selected ? '<span class="tag">selected</span>' : ''}</td>
      <td>${esc(x.name)} <span class="dim">${esc(x.id)}</span></td><td>${x.source}</td><td>${esc(x.version || '')}</td><td>${fmt(x.sizeBytes)}</td>
      <td>${x.compatible ? (x.recommended ? '<span class="ok">recommended</span>' : 'ok') : `<span class="err">${esc(x.reason || 'incompatible')}</span>`}</td>
      <td style="white-space:nowrap">${x.compatible && !x.selected ? `<button class="b sec" onclick="selectRuntime('${esc(x.id)}')">Use</button>` : ''}
        ${x.source === 'user' && !x.active ? `<button class="b sec" onclick="removeRuntime('${esc(x.id)}')">Remove</button>` : ''}</td></tr>`).join('') + '</table>' +
    `<div class="row" style="margin-top:8px"><button class="b sec" onclick="selectRuntime('auto')">Automatic${r.selected === 'auto' ? ' ✓' : ''}</button>
      <input id="rt-src" placeholder="Install a runtime: .zip URL, .zip file or folder with llama.dll + ggml*.dll"><button class="b" onclick="installRuntime()">Install</button>
      <button class="b sec" onclick="updateRuntimes()">Check for updates</button></div>
    <div class="dim" id="rt-msg" style="margin-top:6px">${r.restartRequired ? 'The selected runtime is applied after restarting the app.' : 'Runtimes are process wide: a change applies after a restart.'}</div>`;
}
async function selectRuntime(id) { try { await api('/api/runtimes/select', {method:'POST', body:{id}}); } catch (e) { alert(e.message); } loadRuntimes(); }
async function removeRuntime(id) { try { await api('/api/runtimes?id=' + encodeURIComponent(id), {method:'DELETE'}); } catch (e) { alert(e.message); } loadRuntimes(); }
async function installRuntime() {
  const source = $('rt-src').value.trim(); if (!source) return;
  $('rt-msg').textContent = 'Installing…';
  try { await api('/api/runtimes/install', {method:'POST', body:{source}}); $('rt-src').value = ''; await loadRuntimes(); }
  catch (e) { $('rt-msg').textContent = e.message; $('rt-msg').className = 'err'; }
}
async function updateRuntimes() {
  $('rt-msg').textContent = 'Checking…';
  try { const u = await api('/api/runtimes/update', {method:'POST'}); await loadRuntimes(); $('rt-msg').textContent = u.updated.length ? 'Updated: ' + u.updated.join(', ') : 'All installed runtimes are up to date.'; }
  catch (e) { $('rt-msg').textContent = e.message; }
}

async function saveSettings() {
  const s = window._settings;
  s.host = $('s-host').value; s.port = Number($('s-port').value); s.apiKey = $('s-apikey').value || null; s.backend = $('s-backend').value;
  s.modelsDirectory = $('s-modelsdir').value.trim() || null; s.huggingFaceToken = $('s-hftoken').value || null;
  s.cors = $('s-cors').checked; s.anthropicApi = $('s-anthropic').checked; s.autoLoadOnRequest = $('s-autoload').checked; s.network = {allowedNetworks: $('n-allow').value.split(/[\r\n,]+/).map(x => x.trim()).filter(Boolean), tls: {mode: $('n-tls').value, port: Number($('n-tls-port').value) || 1235, certPath: $('n-tls-cert').value.trim() || null, keyPath: $('n-tls-key').value.trim() || null, pfxPassword: $('n-tls-pass').value || null}, limits: {maxBodyMb: Number($('n-body').value) || 32, maxUploadMb: Number($('n-upload').value) || 256, maxConnections: Number($('n-conn').value) || 0, requestsPerMinutePerToken: Number($('n-rl-tok').value) || 0, requestsPerMinutePerIp: Number($('n-rl-ip').value) || 0, maxChoices: Number($('n-cap-n').value) || 0, maxTokens: Number($('n-cap-t').value) || 0}}; s.jit = {enabled: $('s-jit').checked, autoEvict: $('s-jit-evict').checked, defaultTtlMinutes: Number($('s-jit-ttl').value) || 0, maxLoadedModels: Number($('s-jit-max').value) || 0, evictOrder: $('s-jit-order').value};
  s.maxConcurrentDownloads = Number($('s-maxdl').value) || 2; s.requireAuth = $('s-auth').checked;
  s.catalogUrl = $('s-catalog').value.trim() || null;   s.updateFeedUrl = $('s-feed').value.trim() || null; s.checkUpdates = $('s-updates').checked; s.checkModelUpdates = $('s-modelupd').checked; s.autoTitleChats = $('s-autotitle').checked; s.minimizeToTray = $('s-tray').checked; s.debugRequests = $('s-debug').checked; s.anthropicReasoningAsText = $('s-reastext').checked;
  s.loadGuardrail = $('s-guard').value; s.limitToDedicatedGpuMemory = $('s-dedicated').checked;
  s.alwaysShowTemplate = $('s-showtpl').checked;
  $('s-load').querySelectorAll('[data-k]').forEach(el => { const k = el.dataset.k; s.defaultLoad[k] = el.tagName === 'SELECT' ? el.value : (el.value === '' ? null : (isNaN(el.value) ? el.value : Number(el.value))); });
  if (s.apiKey) { apiKey = s.apiKey; localStorage.setItem('apikey', apiKey); }
  document.querySelectorAll('#settings .bad').forEach(el => { el.classList.remove('bad'); el.title = ''; });
  try { await api('/api/settings', {method:'PUT', body: s}); }
  catch (e) {
    if (!e.errors) { $('s-msg').textContent = e.message; return; }
    const ids = {host:'s-host', port:'s-port', backend:'s-backend', apiKey:'s-apikey', modelsDirectory:'s-modelsdir', maxConcurrentDownloads:'s-maxdl', updateFeedUrl:'s-feed', catalogUrl:'s-catalog', loadGuardrail:'s-guard'};
    for (const er of e.errors) {
      const el = $(ids[er.field]) || document.querySelector(`#s-load [data-k="${er.field.split('.').pop()}"]`);
      if (el) { el.classList.add('bad'); el.title = er.message; }
    }
    $('s-msg').textContent = 'Not saved: ' + e.errors.map(x => x.message).join(' ');
    return;
  }
  $('s-msg').textContent = 'Saved.';
}

// ---------------------------------------------------------------- logs
async function pollLogs() {
  if (!$('logs').classList.contains('on')) return;
  const l = await api('/api/logs?n=300').catch(() => []);
  const box = $('logbox'); const atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 20;
  box.textContent = l.join('\n'); if (atEnd) box.scrollTop = box.scrollHeight;
}
setInterval(pollLogs, 1500);
setInterval(() => { if (!document.hidden) refreshDownloads(); }, 1000);
setInterval(() => { if (!document.hidden) refreshStatus(); }, 8000);

