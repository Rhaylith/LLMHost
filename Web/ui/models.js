// ---------------------------------------------------------------- models
let loadTarget = null;
async function refreshModels() {
  await refreshStatus();
  $('mdir').textContent = status.modelsDirectory;
  $('loaded').innerHTML = status.loaded.length ? `<table><tr><th>Model</th><th>Device</th><th>GPU layers</th><th>Threads</th><th>Context</th><th>Size</th><th>Requests</th><th></th></tr>` +
    status.loaded.map(m => `<tr><td>${esc(m.id)} ${m.vision ? '<span class="tag">vision</span>' : ''} ${ttlTag(m)}</td><td class="ok">${m.device.toUpperCase()}</td><td>${m.gpuLayers >= 999 ? 'all' : m.gpuLayers}</td><td>${m.threads}</td><td>${m.contextSize}</td><td>${fmt(m.sizeBytes)}</td><td>${m.requests}</td>
      <td style="white-space:nowrap">${m.isEmbedding ? '' : `<button class="b sec" title="Open PowerShell with Claude Code using this model" onclick='launchClaudeCode(${JSON.stringify(m.id)})'>Claude Code</button> `}<button class="b sec" title="Load a second instance of this model (shares the weights; own context and settings)" onclick='loadAnother(${JSON.stringify(m.modelId)})'>+ instance</button> <button class="b sec" title="Measure generation speed at several thread counts" onclick='testThreads(${JSON.stringify(m.id)}, this)'>Test threads</button> <button class="b sec" onclick="unload('${esc(m.id)}')">Eject</button></td></tr>`).join('') + '</table>' : '<span class="dim">Nothing loaded.</span>';
  const local = await api('/api/models/local'); if (localSort === 'size') local.sort((a, b) => b.sizeBytes - a.sizeBytes);
  updateMap = Object.fromEntries(((await api('/api/models/updates').catch(() => ({results: []}))).results || []).map(r => [r.modelId, r]));
  $('local').innerHTML = local.length ? `<div class="row" style="margin-bottom:6px"><button class="b sec" title="Compare your downloaded files with Hugging Face" onclick="checkUpdates(this)">Check for updates</button><select title="Sort the list" onchange="localSort = this.value; refreshModels()"><option value="name"${localSort === 'name' ? ' selected' : ''}>by name</option><option value="size"${localSort === 'size' ? ' selected' : ''}>largest first</option></select><span class="dim" id="upd-msg"></span></div><table><tr><th>Model</th><th>Arch</th><th>Quant</th><th>Context</th><th>Size</th><th></th></tr>` +
    local.map(m => `<tr><td>${esc(m.id)} ${capBadges(m.capabilities)} ${updateBadge(m)}</td><td>${esc(m.architecture)}</td><td>${esc(m.quantization)}</td><td>${m.contextLength || ''}</td><td>${fmt(m.sizeBytes)}</td>
      <td style="white-space:nowrap">${m.loadedAs ? '<span class="ok">loaded</span>' : `<button class="b" onclick='openLoad(${JSON.stringify(m.id)}, ${m.isEmbedding})'>Load</button>`}
      <button class="b sec" title="Default load and inference settings for this model" onclick='openLoad(${JSON.stringify(m.id)}, ${m.isEmbedding}, "defaults")'>⚙</button>
      <button class="b sec" title="Architecture, licence and capabilities" onclick='showLocalInfo(${JSON.stringify(m.id)})'>Details</button>
      <button class="b sec" onclick='del(${JSON.stringify(m.id)})'>Delete</button></td></tr>`).join('') + '</table>' : '<span class="dim">No models yet — head to Discover.</span>';
}
function fillLoad(d) {
  $('l-device').value = d.device || 'cpu';
  $('l-gpulayers').value = d.gpuLayers ?? '';
  $('l-useratio').checked = d.gpuRatio != null; $('l-gpuratio').value = d.gpuRatio != null ? Math.round(d.gpuRatio * 100) : 50; updateRatioText();
  $('l-ctx').value = d.contextSize || 4096;
  $('l-threads').value = d.threads || 0; $('l-bthreads').value = d.batchThreads || 0; $('l-affinity').value = d.cpuAffinity || ''; $('l-prio').value = d.threadPriority || 'normal';
  $('l-batch').value = d.batchSize || 512;
  $('l-ttl').value = d.ttlMinutes || 0;
  $('l-kvk').value = d.kvCacheTypeK || 'f16'; $('l-kvv').value = d.kvCacheTypeV || 'f16';
  $('l-main').value = d.mainGpu || 0; $('l-conc').value = d.maxConcurrent || 4;
  $('l-drafttokens').value = d.draftTokens || 0; window._draftWanted = d.draftModel || '';
  fillTemplate(d.promptTemplate);
  window._lorasWanted = d.loras || [];
  $('l-pooling').value = d.pooling || 'auto'; $('l-embbatch').value = d.embedBatchSize || 0; $('l-normalize').checked = d.normalize !== false;
  $('l-moecpu').value = d.moeCpuLayers || 0; $('l-moeexp').value = d.moeExpertCount || 0;
  $('l-flash').value = d.flashAttentionMode && d.flashAttentionMode !== 'auto' ? d.flashAttentionMode : (d.flashAttention ? 'on' : 'auto'); $('l-mmap').checked = d.useMmap !== false; $('l-mlock').checked = !!d.useMlock;
  $('l-vision').checked = d.vision !== false;
}
// ---- multi-GPU rows: [{index, name, on, ratio}] in priority order
let gpuRows = [];
function renderGpuRows() {
  $('l-gpus').innerHTML = gpuRows.length ? gpuRows.map((g, i) => `<div class="row" style="margin:3px 0">
      <input type="checkbox" style="width:auto" ${g.on ? 'checked' : ''} onchange="gpuRows[${i}].on=this.checked;scheduleFit()">
      <span style="flex:1">GPU ${g.index} · ${esc(g.name)} <span class="dim">${fmt(g.freeBytes || g.vramBytes)} free${g.llamaIndex != null ? '' : ' · unmapped'}</span></span>
      <input type="number" step="0.1" min="0" placeholder="share" style="width:80px" value="${g.ratio ?? ''}" oninput="gpuRows[${i}].ratio=this.value===''?null:Number(this.value);scheduleFit()">
      <button class="b sec" style="padding:2px 8px" onclick="moveGpu(${i},-1)">▲</button><button class="b sec" style="padding:2px 8px" onclick="moveGpu(${i},1)">▼</button></div>`).join('')
    : '<span class="dim">No GPU detected.</span>';
}
function moveGpu(i, d) { const j = i + d; if (j < 0 || j >= gpuRows.length) return; [gpuRows[i], gpuRows[j]] = [gpuRows[j], gpuRows[i]]; renderGpuRows(); scheduleFit(); }
async function loadGpuRows(d) {
  let hw = null; try { hw = await api('/api/hardware'); } catch {}
  const list = (hw?.gpus || []);
  const chosen = d.gpuDevices || [];
  const order = [...chosen.map(i => list.find(g => g.index === i)).filter(Boolean), ...list.filter(g => !chosen.includes(g.index))];
  gpuRows = order.map(g => ({index: g.index, name: g.name, vramBytes: g.vramBytes, freeBytes: g.freeBytes, llamaIndex: g.llamaIndex,
    on: chosen.includes(g.index), ratio: chosen.includes(g.index) && d.tensorSplit ? d.tensorSplit[chosen.indexOf(g.index)] : null}));
  $('l-splitmode').value = d.splitMode || 'layer';
  renderGpuRows();
}
function collectLoad() {
  const gl = $('l-gpulayers').value;
  const on = gpuRows.filter(g => g.on);
  const ratios = on.map(g => g.ratio);
  return {gpuDevices: on.length ? on.map(g => g.index) : null,
    tensorSplit: on.length > 1 && ratios.every(r => r != null && r > 0) ? ratios : null, splitMode: $('l-splitmode').value,
    device: $('l-device').value, gpuLayers: gl === '' ? null : Number(gl), gpuRatio: $('l-useratio').checked ? Number($('l-gpuratio').value) / 100 : null,contextSize: Number($('l-ctx').value), threads: Number($('l-threads').value), batchThreads: Number($('l-bthreads').value), cpuAffinity: $('l-affinity').value.trim() || null, threadPriority: $('l-prio').value,
    batchSize: Number($('l-batch').value), uBatchSize: Number($('l-batch').value), ttlMinutes: Number($('l-ttl').value), kvCacheTypeK: $('l-kvk').value, kvCacheTypeV: $('l-kvv').value,
    identifier: $('l-id').value || null, mainGpu: Number($('l-main').value), maxConcurrent: Number($('l-conc').value) || 4, draftModel: $('l-draft').value || null, draftTokens: Number($('l-drafttokens').value) || 0, pooling: $('l-pooling').value, embedBatchSize: Number($('l-embbatch').value) || 0, normalize: $('l-normalize').checked, loras: [...document.querySelectorAll('#l-lora-list .lora-row')].filter(r => r.querySelector('input[type=checkbox]').checked).map(r => ({id: r.dataset.id, scale: Number(r.querySelector('input[type=number]').value)})),promptTemplate: collectTemplate(), moeCpuLayers: Number($('l-moecpu').value) || 0, moeExpertCount: Number($('l-moeexp').value) || 0, flashAttentionMode: $('l-flash').value, flashAttention: $('l-flash').value === 'on', useMmap: $('l-mmap').checked, useMlock: $('l-mlock').checked,
    embeddings: $('l-emb').checked, vision: $('l-vision').checked};
}
function fillGen(g) {
  $('g-temperature').value = g.temperature ?? 0.7; $('g-top_p').value = g.topP ?? 0.95; $('g-top_k').value = g.topK ?? 40; $('g-min_p').value = g.minP ?? 0.05;
  $('g-repeat').value = g.repeatPenalty ?? 1.1; $('g-max').value = g.maxTokens ?? -1; $('g-system').value = g.systemPrompt || '';
}
function collectGen() {
  return {temperature: Number($('g-temperature').value), topP: Number($('g-top_p').value), topK: Number($('g-top_k').value), minP: Number($('g-min_p').value),
    repeatPenalty: Number($('g-repeat').value), maxTokens: Number($('g-max').value), systemPrompt: $('g-system').value || null};
}
// mode: 'load' (Load button) or 'defaults' (the gear dialog: only edits the model's saved defaults)
// ---- prompt template section of the load dialog
const tplUnesc = s => (s || '').replace(/\n/g, '\n').replace(/\t/g, '\t');
const tplEsc = s => (s || '').replace(/\n/g, '\n').replace(/\t/g, '\t');
const TPL_FIELDS = [['sys-p','system','prefix'],['sys-s','system','suffix'],['usr-p','user','prefix'],['usr-s','user','suffix'],
  ['ast-p','assistant','prefix'],['ast-s','assistant','suffix'],['tool-p','tool','prefix'],['tool-s','tool','suffix']];
function tplShow() {
  const m = $('l-tpl-mode').value;
  $('l-tpl-jinja').style.display = m === 'jinja' ? '' : 'none';
  $('l-tpl-manual').style.display = m === 'manual' ? '' : 'none';
}
function fillTemplate(t) {
  t = t || {};
  $('l-tpl-mode').value = t.mode || 'auto';
  $('l-tpl-text').value = t.jinjaText || '';
  const f = t.manualFormat || {};
  for (const [id, role, side] of TPL_FIELDS) $('l-tpl-' + id).value = tplEsc((f[role] || {})[side]);
  $('l-tpl-bos').value = tplEsc(f.bos); $('l-tpl-gen').value = tplEsc(f.generationPrefix);
  $('l-tpl-stop').value = (f.stopStrings || []).map(tplEsc).join(',');
  tplShow();
}
function collectTemplate() {
  const f = {bos: tplUnesc($('l-tpl-bos').value), generationPrefix: tplUnesc($('l-tpl-gen').value),
    stopStrings: $('l-tpl-stop').value.split(',').map(tplUnesc).filter(Boolean)};
  for (const [id, role, side] of TPL_FIELDS) (f[role] ||= {})[side] = tplUnesc($('l-tpl-' + id).value);
  return {mode: $('l-tpl-mode').value, jinjaText: $('l-tpl-text').value || null, manualFormat: f};
}
$('l-tpl-mode').onchange = tplShow;
$('l-tpl-fetch').onclick = async () => {
  try { const r = await api('/api/models/template?model=' + encodeURIComponent(loadTarget)); $('l-tpl-text').value = r.template || ''; if (!r.template) $('l-tpl-out').textContent = 'The model file has no chat template.'; }
  catch (e) { $('l-tpl-out').style.display = ''; $('l-tpl-out').textContent = e.message; }
};
$('l-tpl-preview').onclick = async () => {
  const out = $('l-tpl-out'); out.style.display = '';
  try {
    const r = await api('/api/models/template/preview', {method:'POST', body:{model: loadTarget, template: collectTemplate()}});
    out.textContent = r.error ? 'Error: ' + r.error : r.prompt + (r.stopStrings?.length ? '\n— stops at: ' + r.stopStrings.join(' ') : '') + '\n— rendered by: ' + r.engine;
  } catch (e) { out.textContent = e.message; }
};
async function showTemplateInfo(id) {
  $('l-tpl-info').textContent = '';
  $('l-tpl-out').style.display = 'none';
  try {
    const r = await api('/api/models/template?model=' + encodeURIComponent(id));
    $('l-tpl-info').textContent = r.hasTemplate ? `The model file has a chat template (${r.template.length} characters; reasoning: ${r.reasoning}, tools: ${r.toolFormat}).` : 'The model file has no chat template; ChatML is used unless you set one here.';
  } catch {}
  try { const s = window._settings || await api('/api/settings'); $('l-template').open = !!s.alwaysShowTemplate || $('l-tpl-mode').value !== 'auto'; } catch {}
}
// LoRA adapters of the same architecture, with a strength each
async function showLoras(id) {
  $('l-lora').style.display = 'none'; $('l-lora-list').innerHTML = '';
  try {
    const list = await api('/api/models/adapters?model=' + encodeURIComponent(id));
    if (!list.length) return;
    const wanted = window._lorasWanted || [];
    $('l-lora-list').innerHTML = list.map(a => {
      const w = wanted.find(x => x.id === a.id);
      return `<div class="lora-row row" data-id="${esc(a.id)}"><label style="flex:1"><input type="checkbox" style="width:auto" ${w ? 'checked' : ''}> ${esc(a.id)} <span class="dim">${fmt(a.sizeBytes)}</span></label><input type="number" step="0.05" value="${w ? w.scale : 1}" style="width:80px" title="Strength: 1 = as trained, 0 = off"></div>`;
    }).join('');
    $('l-lora').style.display = '';
  } catch {}
}
// mixture-of-experts options are only offered for MoE models
async function showMoe(id) {
  $('l-moe').style.display = 'none';
  try {
    const list = await api('/api/models/local');
    const m = list.find(x => x.id === id);
    if (m?.capabilities?.moe) $('l-moe').style.display = '';
    $('l-embed').style.display = (m?.capabilities?.embedding || m?.capabilities?.rerank || m?.isEmbedding) ? '' : 'none';
  } catch {}
}
// models that can act as draft for speculative decoding (same vocabulary, much smaller) + suggested downloads
async function loadDrafts(id) {
  const sel = $('l-draft');
  sel.innerHTML = '<option value="">none</option>';
  sel.title = '';
  try {
    const r = await api('/api/models/drafts?model=' + encodeURIComponent(id));
    sel.innerHTML += r.candidates.map(c => `<option value="${esc(c.id)}">${esc(c.id)} (${fmt(c.sizeBytes)})</option>`).join('');
    if (!r.candidates.length && r.suggestions.length) sel.title = 'No suitable model downloaded. Suggested: ' + r.suggestions.map(s => s.repo).join(', ');
  } catch {}
  const wanted = window._draftWanted || '';
  if (wanted && ![...sel.options].some(o => o.value === wanted)) sel.innerHTML += `<option value="${esc(wanted)}">${esc(wanted)}</option>`;
  sel.value = wanted;
}
async function loadAnother(modelId) { await openLoad(modelId, false); window._another = true; $('loaddlg-title').textContent = 'Load another instance'; $('l-id').placeholder = 'blank = automatic name (name:2, name:3 ...)'; }
async function openLoad(id, emb, mode = 'load') {
  window._another = false;
  const lc = status?.hardware?.logicalCores;
  if (lc) $('l-threads-label').textContent = `CPU threads (0 = default: ${status?.hardware?.physicalCores || Math.max(1, Math.round(lc / 2))} physical of ${lc} logical)`;
  // start from this model's saved defaults, falling back to the global ones
  let d = {load: {}, generation: {}};
  try { d = await api('/api/models/defaults?id=' + encodeURIComponent(id)); } catch {}
  fillLoad(d.load || {}); fillGen(d.generation || {});
  loadGpuRows(d.load || {});
  $('l-id').value = '';
  $('loaddlg-title').textContent = mode === 'defaults' ? 'Default settings for this model' : 'Load model';
  $('l-go').style.display = mode === 'defaults' ? 'none' : '';
  $('l-clear').style.display = d.hasLoad || d.hasGeneration ? '' : 'none';
  $('l-badge').textContent = d.hasLoad || d.hasGeneration ? 'saved defaults applied' : 'global defaults';
  loadDrafts(id); showTemplateInfo(id); showMoe(id); showLoras(id);
  loadTarget = id; $('loadname').textContent = id; $('l-emb').checked = emb; $('l-msg').textContent = ''; $('l-msg').className = 'dim';
  $('l-force').style.display = 'none'; $('l-fit').textContent = 'Estimating memory use…';
  $('loaddlg').showModal();
  updateFit();
}
$('l-save').onclick = async () => {
  try {
    await api('/api/models/defaults', {method:'PUT', body:{id: loadTarget, load: collectLoad(), generation: collectGen()}});
    $('l-msg').textContent = 'Saved as the default for this model (used by the UI, the API and the CLI).'; $('l-msg').className = 'ok';
    $('l-clear').style.display = '';
  } catch (e) { $('l-msg').textContent = e.message; $('l-msg').className = 'err'; }
};
$('l-clear').onclick = async () => {
  await api('/api/models/defaults?id=' + encodeURIComponent(loadTarget), {method:'DELETE'});
  $('l-msg').textContent = 'Saved defaults removed; the global defaults apply.'; $('l-msg').className = 'dim'; $('l-clear').style.display = 'none';
};
async function doLoad(force) {
  const options = collectLoad();
  if (force) options.skipFitCheck = true;
  $('l-go').disabled = true; $('l-force').style.display = 'none'; $('l-msg').className = 'dim';
  $('l-msg').textContent = 'Loading… this can take a while for large models';
  try {
    const r = await api('/api/models/load', {method:'POST', body:{model: loadTarget, options, allowDuplicate: !!window._another}});
    $('loaddlg').close();
    const warn = (status?.loaded || []).find(m => m.id === r.id)?.warnings;
    if (r.warnings?.length) alert(r.warnings.join('\n'));
  } catch (e) {
    $('l-msg').textContent = e.message; $('l-msg').className = 'err';
    if (/fit in memory|will probably not fit/.test(e.message)) $('l-force').style.display = '';
  }
  $('l-go').disabled = false; refreshModels();
}
$('l-go').onclick = () => doLoad(false);
$('l-force').onclick = () => doLoad(true);

// live "will it fit?" estimate
let fitTimer = null;
function scheduleFit() { clearTimeout(fitTimer); fitTimer = setTimeout(updateFit, 250); }
async function updateFit() {
  if (!loadTarget || !$('loaddlg').open) return;
  const o = collectLoad();
  const q = new URLSearchParams({id: loadTarget, ctx: o.contextSize, device: o.device, kvK: o.kvCacheTypeK, kvV: o.kvCacheTypeV, flash: o.flashAttentionMode, vision: o.vision});
  if (o.gpuLayers !== null) q.set('gpuLayers', o.gpuLayers);
  if (o.gpuRatio !== null) q.set('gpuRatio', o.gpuRatio);
  if (o.gpuDevices) q.set('devices', o.gpuDevices.join(','));
  if (o.tensorSplit) q.set('split', o.tensorSplit.join(','));
  q.set('splitMode', o.splitMode);
  try {
    const e = await api('/api/models/estimate?' + q);
    window._blocks = e.blockCount; updateRatioText();
    const col = e.verdict === 'fits' ? 'var(--ok)' : e.verdict === 'tight' ? '#d29922' : 'var(--acc)';
    const label = {fits: 'Should fit', tight: 'Tight', 'wont-fit': 'Will not fit'}[e.verdict];
    $('l-fit').innerHTML = `<b style="color:${col}">${label}</b> — weights ${fmt(e.weightsBytes)} + KV cache ${fmt(e.kvCacheBytes)} + compute ${fmt(e.computeBytes)}${e.projectorBytes ? ' + vision ' + fmt(e.projectorBytes) : ''}` +
      ` · ${e.offloadedLayers}/${e.blockCount} layers on GPU${e.moe ? ' · MoE' : ''}<br>` +
      e.devices.map(d => `${esc(d.name)}: ${fmt(d.needBytes)} / ${fmt(d.budgetBytes)}`).join(' · ') +
      (e.maxGpuLayers >= 0 && e.verdict !== 'fits' ? `<br>At most ${e.maxGpuLayers >= 999 ? 'all' : e.maxGpuLayers} layers fit the GPU memory.` : '') +
      (e.suggestedContext ? `<br>A context of ${e.suggestedContext} would fit.` : '') +
      (e.warnings.length ? '<br><span class="dim">' + e.warnings.map(esc).join('<br>') + '</span>' : '');
  } catch (err) { $('l-fit').innerHTML = `<span class="err">${esc(err.message)}</span>`; }
}
// "share of the layers" slider: the same rounding as the server (ratio x (blocks + 1), the output layer counts as one more)
function updateRatioText() {
  const r = Number($('l-gpuratio').value) / 100, b = window._blocks || 0;
  const n = Math.round(r * (b + 1));
  $('l-ratio-out').textContent = Math.round(r * 100) + '%' + (b ? ' ≈ ' + (n >= b + 1 ? 'all' : n) + ' of ' + (b + 1) + ' layers' : '');
  $('l-gpuratio').disabled = !$('l-useratio').checked;
}
$('l-gpuratio').addEventListener('input', updateRatioText); $('l-useratio').addEventListener('change', updateRatioText);
['l-useratio','l-gpuratio','l-device','l-gpulayers','l-ctx','l-kvk','l-kvv','l-flash','l-vision','l-batch'].forEach(id => { $(id).addEventListener('input', scheduleFit); $(id).addEventListener('change', scheduleFit); });
async function testThreads(id, btn) {
  const old = btn.textContent; btn.disabled = true; btn.textContent = 'Testing...';
  try {
    const r = await api('/api/models/benchmark-threads', {method:'POST', body:{id}});
    const lines = r.rows.map(x => x.threads + ' threads: ' + x.tokensPerSecond + ' tok/s');
    alert(['Generation speed by thread count:', ...lines, '', 'Fastest: ' + r.best + ' threads (now ' + r.current + ').'].join(String.fromCharCode(10)));
  } catch (e) { alert(e.message || e); }
  btn.disabled = false; btn.textContent = old;
}
async function unload(id) { await api('/api/models/unload', {method:'POST', body:{id}}); refreshModels(); }
async function del(id) { if (confirm('Delete ' + id + ' from disk?')) { await api('/api/models/local?id=' + encodeURIComponent(id), {method:'DELETE'}).catch(e => alert(e.message)); refreshModels(); } }


// where a loaded model came from and when it will be unloaded for being idle
function ttlTag(m) {
  const origin = m.loadedBy === 'jit' ? '<span class="tag" title="Loaded because a request named it; it may be unloaded to make room">on demand</span>' : '';
  if (!m.ttlSeconds || m.busy) return origin;
  const left = Math.max(0, Math.round(m.ttlSeconds - (Date.now() - new Date(m.lastUsed).getTime()) / 1000));
  return origin + `<span class="tag" title="Unloaded after ${Math.round(m.ttlSeconds / 60)} min without use">unloads in ${left >= 90 ? Math.round(left / 60) + ' min' : left + ' s'}</span>`;
}
