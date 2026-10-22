// ---------------------------------------------------------------- per-chat settings: where each value comes from, "differs" dots, save as default, reset
// The server decides what wins (request > chat > preset > model default > global); here the form is compared with the base the chat follows.
const GEN_FIELDS = {'p-temperature': 'temperature', 'p-top_p': 'topP', 'p-top_k': 'topK', 'p-min_p': 'minP', 'p-repeat_penalty': 'repeatPenalty',
  'p-frequency_penalty': 'frequencyPenalty', 'p-presence_penalty': 'presencePenalty', 'p-max_tokens': 'maxTokens', 'p-seed': 'seed', 'p-stop': 'stop',
  'p-reasoning': 'reasoningEffort', 'p-overflow': 'contextOverflow', 'p-json': 'jsonMode', 'p-schema': 'jsonSchema', 'p-system': 'systemPrompt'};
let effBase = null, effOverrides = {}, effLayer = 'global';

const sameSetting = (a, b) => {
  const blank = v => v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0) || v === false;
  if (blank(a) && blank(b)) return true;
  if (typeof a === 'number' || typeof b === 'number') return Math.abs(Number(a) - Number(b)) < 1e-3;
  return JSON.stringify(a) === JSON.stringify(b);
};
async function refreshEffective() {
  try {
    const p = new URLSearchParams();
    if (conv.persisted) p.set('chat', conv.id);
    if ($('p-model').value) p.set('model', $('p-model').value);
    if (conv.preset) p.set('preset', conv.preset);
    const r = await api('/api/effective-settings?' + p.toString());
    effBase = r.base; effOverrides = r.overrides || {}; effLayer = r.baseLayer;
  } catch (e) { effBase = null; }
  markDiffs();
}
function markDiffs() {
  if (!effBase) return;
  let g; try { g = genForServer(); } catch (e) { return; }
  let n = 0;
  for (const [id, key] of Object.entries(GEN_FIELDS)) {
    const el = $(id); if (!el) continue;
    const lab = el.closest('.row')?.previousElementSibling || el.previousElementSibling;
    const differs = !sameSetting(g[key], effBase[key]);
    if (differs) n++;
    if (lab && lab.tagName === 'LABEL') { lab.classList.toggle('diff', differs); lab.title = differs ? `Differs from ${conv.preset ? 'the preset "' + conv.preset + '"' : effLayer === 'model' ? 'the default of this model' : 'the global default'}` : ''; }
  }
  const from = conv.preset ? `preset "${conv.preset}"` : effLayer === 'model' ? "this model's defaults" : 'the global defaults';
  $('chat-settings-note').textContent = n ? `${n} setting${n > 1 ? 's' : ''} of this chat differ from ${from} (marked ●).` : `This chat follows ${from}.`;
}
async function saveAsModelDefault() {
  if (!conv.persisted) { alert('Send a message first: the chat has to exist before its settings can become the model default.'); return; }
  try { await persist(); await api(`/api/chats/${conv.id}/settings/save-as`, {method: 'POST', body: {target: 'modelDefault'}}); await refreshEffective(); alert('Saved as the default of this model.'); }
  catch (e) { alert(e.message); }
}
async function resetChatSettings() {
  let base = effBase;
  if (conv.persisted) { try { await persist(); const r = await api(`/api/chats/${conv.id}/settings/reset`, {method: 'POST'}); base = r.base; } catch (e) { alert(e.message); return; } }
  if (base) applyGen(base);
  await refreshEffective();
}
// the preset chosen with "Load" is the one this chat follows
const _loadPreset = loadPreset;
loadPreset = function () { const n = $('preset-sel').value; conv.preset = n && window._presets?.[n] ? n : null; _loadPreset(); refreshEffective(); };
document.addEventListener('input', e => { if (e.target.closest?.('#params')) markDiffs(); });
document.addEventListener('change', e => { if (e.target.id === 'p-model') refreshEffective(); else if (e.target.closest?.('#params')) markDiffs(); });
setTimeout(refreshEffective, 800);

// ---- presets as files: "modified" state, update / reset, and live refresh when a file changes on disk
async function updateActivePreset() {
  if (!conv.preset) { alert('Load a preset first, or use Save with a new name.'); return; }
  if (!confirm('Overwrite the preset "' + conv.preset + '" with the values of this panel?')) return;
  try { await api('/api/presets/' + encodeURIComponent(conv.preset), {method: 'PUT', body: genForServer()}); await refreshPresets(); $('preset-sel').value = conv.preset; await refreshEffective(); }
  catch (e) { alert(e.message); }
}
async function resetToPreset() {
  const p = conv.preset ? window._presets?.[conv.preset] : null;
  if (p) applyGen(p); else if (effBase) applyGen(effBase);
  await refreshEffective();
}
const _markDiffs = markDiffs;
markDiffs = function () {
  _markDiffs();
  const has = !!conv.preset;
  $('preset-update').disabled = !has; $('preset-reset').disabled = !has;
  const note = $('chat-settings-note');
  if (has && effBase) { try { const g = genForServer(); if (Object.entries(GEN_FIELDS).some(([, k]) => !sameSetting(g[k], effBase[k]))) note.textContent = '● Preset "' + conv.preset + '" modified. ' + note.textContent; } catch (e) {} }
};
let presetVersion = null;
setInterval(async () => {
  if (document.hidden) return;
  try {
    const r = await api('/api/preset-list');
    if (presetVersion !== null && r.version !== presetVersion) { await refreshPresets(); if (conv.preset && $('preset-sel').querySelector(`option[value="${CSS.escape(conv.preset)}"]`)) $('preset-sel').value = conv.preset; refreshEffective(); }
    presetVersion = r.version;
  } catch (e) {}
}, 3000);
