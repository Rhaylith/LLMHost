// ---------------------------------------------------------------- tabs
document.querySelectorAll('nav button').forEach(b => b.onclick = () => {
  document.querySelectorAll('nav button').forEach(x => x.classList.toggle('on', x === b));
  document.querySelectorAll('main > section').forEach(s => s.classList.toggle('on', s.id === b.dataset.tab));
  if (b.dataset.tab === 'models') { refreshModels(); refreshDownloads(); }
  if (b.dataset.tab === 'discover') { refreshDownloads(); renderCatalog(); }
  if (b.dataset.tab === 'settings') { renderDisk(); loadSettings(); loadRuntimes(); loadTokens(); loadMcp(false); }
  if (b.dataset.tab === 'help') initHelp();
});

// ---------------------------------------------------------------- status
let status = null;
async function refreshStatus() {
  try {
    status = await api('/api/status');
    const l = status.loaded;
    $('status').innerHTML = `${esc(status.backend)} backend<br>${l.length} model(s) loaded`;
    const sel = $('p-model'), cur = sel.value;
    sel.innerHTML = '<option value="">(auto / load on demand)</option>' + l.filter(m => !m.isEmbedding).map(m => `<option>${esc(m.id)}</option>`).join('');
    if (cur && [...sel.options].some(o => o.value === cur)) sel.value = cur; else if (l.length) sel.value = l.find(m => !m.isEmbedding)?.id ?? '';
  } catch (e) { $('status').textContent = 'offline'; }
}

// ---------------------------------------------------------------- params
const sliders = ['temperature','top_p','top_k','min_p','repeat_penalty','frequency_penalty','presence_penalty'];
const defaults = {temperature:0.7, top_p:0.95, top_k:40, min_p:0.05, repeat_penalty:1.1, frequency_penalty:0, presence_penalty:0};
sliders.forEach(n => { const el = $('p-' + n); el.value = defaults[n]; const upd = () => el.nextElementSibling.textContent = el.value; el.oninput = upd; upd(); });
$('p-max_tokens').value = -1;

// advanced samplers: [element id, camelCase option, API field, default]
const ADV = [['mirostat','mirostat','mirostat',0], ['mirostat_tau','mirostatTau','mirostat_tau',5], ['mirostat_eta','mirostatEta','mirostat_eta',0.1],
  ['dry_multiplier','dryMultiplier','dry_multiplier',0], ['dry_base','dryBase','dry_base',1.75], ['dry_allowed_length','dryAllowedLength','dry_allowed_length',2],
  ['xtc_probability','xtcProbability','xtc_probability',0], ['xtc_threshold','xtcThreshold','xtc_threshold',0.1], ['top_n_sigma','topNSigma','top_n_sigma',0]];
function advParams(p) {
  for (const [id, , api, def] of ADV) { const v = Number($('p-' + id).value); if (v !== def) p[api] = v; }
  const order = $('p-samplers').value.split(',').map(s => s.trim()).filter(Boolean); if (order.length) p.samplers = order;
}
function advApply(g) {
  for (const [id, key, , def] of ADV) $('p-' + id).value = g[key] ?? def;
  $('p-samplers').value = (g.samplerOrder || []).join(',');
}
function advForServer() {
  const o = {};
  for (const [id, key] of ADV) o[key] = Number($('p-' + id).value);
  o.samplerOrder = $('p-samplers').value.split(',').map(s => s.trim()).filter(Boolean);
  return o;
}
function genParams() {
  const p = {};
  sliders.forEach(n => p[n] = Number($('p-' + n).value));
  const mt = Number($('p-max_tokens').value); if (mt > 0) p.max_tokens = mt;
  if ($('p-seed').value !== '') p.seed = Number($('p-seed').value);
  p.cache_prompt = $('p-cache').checked;
  if ($('p-reasoning').value) p.reasoning_effort = $('p-reasoning').value;
  if ($('p-overflow').value !== 'dropOldest') p.context_overflow = $('p-overflow').value;
  const stop = $('p-stop').value.split(',').map(s => s.replace(/\\n/g, '\n')).filter(Boolean); if (stop.length) p.stop = stop;
  const schema = $('p-schema').value.trim();
  if (schema) { try { p.response_format = {type:'json_schema', json_schema:{name:'out', schema: JSON.parse(schema)}}; } catch { alert('JSON schema is not valid JSON'); throw new Error('schema'); } }
  else if ($('p-json').checked) p.response_format = {type:'json_object'};
  advParams(p);
  return p;
}
function applyGen(g) {
  if (!g) return;
  const m = {temperature:'temperature', topP:'top_p', topK:'top_k', minP:'min_p', repeatPenalty:'repeat_penalty', frequencyPenalty:'frequency_penalty', presencePenalty:'presence_penalty'};
  for (const [k, id] of Object.entries(m)) if (g[k] !== undefined) { $('p-' + id).value = g[k]; $('p-' + id).oninput(); }
  if (g.maxTokens !== undefined) $('p-max_tokens').value = g.maxTokens;
  $('p-seed').value = g.seed ?? '';
  $('p-stop').value = (g.stop || []).join(',');
  $('p-reasoning').value = g.reasoningEffort || '';
  $('p-overflow').value = g.contextOverflow || 'dropOldest';
  $('p-json').checked = !!g.jsonMode;
  $('p-schema').value = g.jsonSchema || '';
  if (g.systemPrompt !== undefined) $('p-system').value = g.systemPrompt || '';
  advApply(g);
}
function genForServer() {
  const p = genParams();
  return {temperature:p.temperature, topP:p.top_p, topK:p.top_k, minP:p.min_p, repeatPenalty:p.repeat_penalty,
    frequencyPenalty:p.frequency_penalty, presencePenalty:p.presence_penalty, maxTokens:p.max_tokens ?? -1, seed:p.seed ?? null,
    stop:p.stop || [], reasoningEffort: $('p-reasoning').value || null, contextOverflow: $('p-overflow').value, systemPrompt:$('p-system').value || null, jsonMode: $('p-json').checked, jsonSchema: $("p-schema").value.trim() || null, ...advForServer()};
}
async function refreshPresets() {
  const all = await api('/api/presets');
  $('preset-sel').innerHTML = Object.keys(all).map(n => `<option>${esc(n)}</option>`).join('');
  window._presets = all;
}
function loadPreset() { const n = $('preset-sel').value; if (n && window._presets[n]) applyGen(window._presets[n]); }
async function savePreset() {
  const n = $('preset-name').value.trim(); if (!n) return;
  await api('/api/presets/' + encodeURIComponent(n), {method:'PUT', body: genForServer()});
  await refreshPresets(); $('preset-sel').value = n;
}

