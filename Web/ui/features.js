// ---------------------------------------------------------------- interface modes: user < power < developer
// This table is the one place that says which control needs which mode. Controls are hidden, never disabled. A new setting adds a line here.
// "Show all settings" (the search box at the top of the Settings tab) ignores the mode.
const MODE_ORDER = ['user', 'power', 'developer'];
const FEATURES = [
  // navigation
  ['nav button[data-tab="requests"]', 'developer'], ['nav button[data-tab="logs"]', 'developer'],
  // Settings tab: cards and sections (a section is the heading and everything up to the next one)
  ['#hwlive-card', 'power'], ['#link-card', 'power'], ['#jit-card', 'power'], ['#disk-card', 'power'], ['#net-card', 'developer'],
  ['section:MCP servers', 'developer'], ['section:Runtimes', 'power'], ['section:Server & application settings', 'developer'], ['section:API tokens', 'developer'], ['section:API', 'developer'],
  // Chat: samplers, presets, tools
  ['row:p-top_k', 'power'], ['row:p-min_p', 'power'], ['row:p-repeat_penalty', 'power'], ['row:p-frequency_penalty', 'power'], ['row:p-presence_penalty', 'power'],
  ['#adv-sampling', 'power'], ['#params h3', 'power'], ['#preset-sel', 'power'], ['#preset-name', 'power'], ['#preset-update', 'power'], ['#preset-reset', 'power'], ['#chat-settings-note', 'power'],
  ['#params button[onclick*="launchClaudeCode"]', 'power'], ['#params button[onclick*="configureClaudeApp"]', 'power'], ['#params button[onclick*="saveAsModelDefault"]', 'power'], ['#params button[onclick*="resetChatSettings"]', 'power'],
  ['#params button[onclick*="exportPreset"], #params button[onclick*="openPresetImport"], #params button[onclick*="duplicatePreset"]', 'power'],
  ['#compare-btn', 'power'],
  // Models: per-model configuration
  ['field:l-draft', 'power'], ['field:l-threads', 'power'], ['field:l-bthreads', 'power'], ['field:l-affinity', 'power'], ['field:l-prio', 'power'], ['field:l-useratio', 'power'],
  ['field:l-kvk', 'power'], ['field:l-kvv', 'power'], ['#l-gpubox', 'power'], ['#l-moe', 'power'], ['#l-lora', 'power'], ['#l-template', 'developer'],
  ['#loaded button[onclick*="testThreads"], #loaded button[onclick*="loadAnother"]', 'power'],
];

function applyFeatures() {
  const mark = (el, mode) => { if (el) el.dataset.min = mode; };
  for (const [sel, mode] of FEATURES) {
    if (sel.startsWith('section:')) {
      const h = [...document.querySelectorAll('#settings > h2')].find(x => x.textContent.trim().startsWith(sel.slice(8)));
      for (let el = h; el && (el === h || el.tagName !== 'H2'); el = el.nextElementSibling) mark(el, mode);
    } else if (sel.startsWith('row:')) {
      const row = document.getElementById(sel.slice(4))?.closest('.row'); mark(row, mode); mark(row?.previousElementSibling, mode);
    } else if (sel.startsWith('field:')) mark(document.getElementById(sel.slice(6))?.closest('div'), mode);
    else document.querySelectorAll(sel).forEach(el => mark(el, mode));
  }
}
let uiMode = 'power';
function setBodyMode(mode) { uiMode = MODE_ORDER.includes(mode) ? mode : 'power'; document.body.dataset.mode = uiMode; const s = $('ui-mode'); if (s) s.value = uiMode; }
async function loadUiMode() { try { setBodyMode((await api('/api/settings')).uiMode); } catch (e) { setBodyMode('power'); } }
async function changeUiMode(mode) {
  setBodyMode(mode);
  try { const s = await api('/api/settings'); s.uiMode = mode; await api('/api/settings', {method: 'PUT', body: s}); } catch (e) { alert(e.message); }
}
// the search box of the Settings tab: typing shows everything that matches, whatever the mode
function filterSettings() {
  const q = $('set-search').value.trim().toLowerCase();
  document.body.dataset.showAll = q ? '1' : '';
  document.querySelectorAll('#settings > .card').forEach(c => { c.style.display = !q || c.textContent.toLowerCase().includes(q) ? '' : 'none'; });
  document.querySelectorAll('#settings > h2').forEach(h => { const next = h.nextElementSibling; h.style.display = !q || h.textContent.toLowerCase().includes(q) || (next && next.style.display !== 'none') ? '' : 'none'; });
}
applyFeatures(); loadUiMode();
$('ui-mode').onchange = e => changeUiMode(e.target.value);
$('set-search').oninput = filterSettings;
