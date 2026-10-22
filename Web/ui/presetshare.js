// ---------------------------------------------------------------- presets: export, import (file / paste / https address) and duplicate
async function exportPreset() {
  const n = $('preset-sel').value; if (!n) return;
  const r = await api('/api/presets/' + encodeURIComponent(n) + '/export', {raw: true});
  if (!r.ok) { alert('Export failed'); return; }
  const blob = await r.blob();
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob);
  a.download = /filename="([^"]+)"/.exec(r.headers.get('content-disposition') || '')?.[1] || n + '.preset.json';
  a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}
async function duplicatePreset() {
  const n = $('preset-sel').value; if (!n) return;
  try { const r = await api('/api/presets/' + encodeURIComponent(n) + '/duplicate', {method: 'POST', body: {}}); await refreshPresets(); $('preset-sel').value = r.name; }
  catch (e) { alert(e.message); }
}
function openPresetImport(text) { $('pi-text').value = text || ''; $('pi-url').value = ''; $('pi-file').value = ''; $('pi-preview').innerHTML = ''; $('preset-import').showModal(); if (text) presetPreview(); }
$('pi-file').onchange = async e => { const f = e.target.files[0]; if (f) { $('pi-text').value = await f.text(); $('pi-url').value = ''; presetPreview(); } };
const piSource = () => $('pi-text').value.trim() ? {text: $('pi-text').value} : {url: $('pi-url').value.trim()};

// first show exactly what the preset would set; nothing is stored until it is confirmed
async function presetPreview() {
  const box = $('pi-preview'); box.innerHTML = '<span class="dim">Checking…</span>';
  try {
    const p = await api('/api/presets/import/preview', {method: 'POST', body: piSource()});
    const rows = Object.entries(p.fields).map(([k, v]) => `<tr><td>${esc(k)}</td><td><code>${esc(v)}</code></td></tr>`).join('');
    box.innerHTML = `<h4>${esc(p.name)}</h4>${p.description ? `<div class="dim">${esc(p.description)}</div>` : ''}
      ${p.systemPrompt ? `<div style="margin:6px 0"><b>System prompt</b><div class="dim" style="white-space:pre-wrap;max-height:120px;overflow:auto">${esc(p.systemPrompt)}</div></div>` : ''}
      <table>${rows || '<tr><td class="dim">No inference settings: the defaults apply.</td></tr>'}</table>
      ${p.dropped.length ? `<div class="warn" style="margin-top:6px">Ignored (load settings are never part of a preset): ${p.dropped.map(esc).join(', ')}</div>` : ''}
      ${p.unknown.length ? `<div class="warn">Ignored (unknown): ${p.unknown.map(esc).join(', ')}</div>` : ''}
      ${p.exists ? `<div class="err" style="margin-top:6px">A preset named "${esc(p.name)}" already exists.</div>` : ''}
      <div class="row" style="margin-top:8px">${p.exists
        ? `<button class="b red" onclick='presetImportNow(${JSON.stringify(p.name)}, true)'>Overwrite</button><button class="b" onclick='presetImportNow(${JSON.stringify(p.suggestedName)}, false)'>Save as "${esc(p.suggestedName)}"</button>`
        : `<button class="b" onclick='presetImportNow(null, false)'>Save preset</button>`}
        <button class="b sec" onclick="$('preset-import').close()">Cancel</button></div>`;
  } catch (e) { box.innerHTML = `<span class="err">${esc(e.message)}</span>`; }
}
async function presetImportNow(name, overwrite) {
  try {
    const r = await api('/api/presets/import', {method: 'POST', body: {...piSource(), name, overwrite}});
    $('preset-import').close(); await refreshPresets(); $('preset-sel').value = r.name;
  } catch (e) { $('pi-preview').insertAdjacentHTML('beforeend', `<div class="err">${esc(e.message)}</div>`); }
}
// a preset file dropped anywhere on the window opens the import dialog with its contents
addEventListener('dragover', e => { if ([...(e.dataTransfer?.types || [])].includes('Files')) e.preventDefault(); });
addEventListener('drop', async e => {
  const f = [...(e.dataTransfer?.files || [])].find(f => /\.(preset\.json|llmpreset)$/i.test(f.name));
  if (!f) return;
  e.preventDefault(); e.stopPropagation(); openPresetImport(await f.text());
}, true);
