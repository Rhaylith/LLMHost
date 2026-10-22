// ---------------------------------------------------------------- keyboard shortcuts, help overlay and quick open
// The table of actions and keys comes from /api/shortcuts (defaults plus the user's choices from the settings); the code below binds them.
let shortcutList = [], comboToAction = new Map();

function comboOf(e) {
  let key = e.key;
  if (['Control', 'Shift', 'Alt', 'Meta'].includes(key)) return '';
  if (key === ' ') key = 'Space';
  if (key.length === 1 && /[a-z]/i.test(key)) key = key.toUpperCase();
  const mods = [];
  if (e.ctrlKey) mods.push('Ctrl');
  if (e.altKey) mods.push('Alt');
  // Shift is part of the character for symbols such as "?" or "+": only letters and named keys keep it as a modifier
  if (e.shiftKey && (key.length > 1 || /[A-Z0-9]/.test(key))) mods.push('Shift');
  if (e.metaKey) mods.push('Meta');
  return [...mods, key].join('+');
}
async function loadShortcuts() {
  try { shortcutList = await api('/api/shortcuts'); } catch (e) { shortcutList = []; }
  comboToAction = new Map(shortcutList.filter(a => a.keys).map(a => [a.keys, a.id]));
  if (typeof renderShortcutSettings === 'function') renderShortcutSettings();
}
const inTextField = () => { const a = document.activeElement; return a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA' || a.tagName === 'SELECT' || a.isContentEditable); };
const goTab = name => document.querySelector(`nav button[data-tab="${name}"]`).click();
const lastAssistant = () => [...conv.messages].reverse().find(m => m.role === 'assistant');

const SHORTCUT_RUN = {
  newChat: () => { goTab('chat'); newChat(); $('input').focus(); },
  newFolder: () => { goTab('chat'); newFolder(); },
  quickOpen: () => openQuickOpen(), quickOpen2: () => openQuickOpen(),
  settings: () => goTab('settings'),
  runtimes: () => { goTab('settings'); setTimeout(() => $('runtimes-h').scrollIntoView(), 150); },
  help: () => showShortcutHelp(),
  focusInput: () => { goTab('chat'); $('input').focus(); },
  send: () => { if (!abort) send(); },
  stop: () => { if ($('quickopen').open) $('quickopen').close(); else if ($('shortcut-help').open) $('shortcut-help').close(); else if (abort) abort.abort(); },
  copyLast: () => { const m = lastAssistant(); if (m) navigator.clipboard.writeText(m.content || '').catch(() => {}); },
  prevVariant: () => { const m = lastAssistant(); if (m) switchVariant(m.id, -1); },
  nextVariant: () => { const m = lastAssistant(); if (m) switchVariant(m.id, 1); },
};

document.addEventListener('keydown', e => {
  if (capturing) return;
  const combo = comboOf(e);
  if (!combo) return;
  const id = comboToAction.get(combo);
  if (!id) return;
  const plain = !/(Ctrl|Alt|Meta)\+/.test(combo);
  // keys without Ctrl / Alt must not hijack typing; Escape is the exception
  if (plain && combo !== 'Escape' && inTextField()) return;
  if (id === 'copyLast' && window.getSelection()?.toString()) return;            // let the normal copy of a selection win
  e.preventDefault();
  try { SHORTCUT_RUN[id]?.(); } catch (err) { console.error(err); }
}, true);

// ---- help overlay
function showShortcutHelp() {
  const groups = {};
  for (const a of shortcutList) (groups[a.group] ||= []).push(a);
  $('shortcut-help-body').innerHTML = Object.entries(groups).map(([g, items]) => `<h4>${esc(g)}</h4><table>` + items.map(a =>
    `<tr><td>${esc(a.label)}</td><td style="text-align:right">${a.keys ? `<kbd>${esc(a.keys.replaceAll('+', ' + '))}</kbd>` : '<span class="dim">none</span>'}</td></tr>`).join('') + '</table>').join('')
    + '<p class="dim">Change them under Server &amp; Settings → Keyboard shortcuts.</p>';
  $('shortcut-help').showModal();
}

// ---- quick open: chats, models and commands with a fuzzy match
function fuzzy(query, text) {
  query = query.toLowerCase(); text = text.toLowerCase();
  if (!query) return 1;
  let qi = 0, score = 0, last = -2;
  for (let i = 0; i < text.length && qi < query.length; i++) {
    if (text[i] === query[qi]) { score += i === last + 1 ? 3 : 1; if (i === 0 || ' -_/.'.includes(text[i - 1])) score += 2; last = i; qi++; }
  }
  return qi === query.length ? score : 0;
}
let quickItems = [], quickShown = [], quickSel = 0;
async function openQuickOpen() {
  const items = [];
  for (const c of chatItems) items.push({kind: 'chat', label: c.title, hint: 'chat', run: () => { goTab('chat'); openChat(c.id); }});
  const local = await api('/api/models/local').catch(() => []);
  for (const m of local.filter(m => !m.isEmbedding)) items.push({kind: 'model', label: m.id, hint: m.loadedAs ? 'loaded · use in chat' : 'load model', run: () => {
    if (m.loadedAs) { goTab('chat'); if ([...$('p-model').options].some(o => o.value === m.loadedAs)) $('p-model').value = m.loadedAs; scheduleCtxBar(); }
    else { goTab('models'); openLoad(m.id, false); } }});
  for (const a of shortcutList.filter(a => !['quickOpen', 'quickOpen2', 'send', 'stop'].includes(a.id))) items.push({kind: 'command', label: a.label, hint: a.keys || '', run: SHORTCUT_RUN[a.id]});
  for (const t of document.querySelectorAll('nav button[data-tab]')) items.push({kind: 'command', label: 'Go to ' + t.textContent.trim(), hint: 'tab', run: () => t.click()});
  quickItems = items; $('quickopen-input').value = ''; drawQuick(); $('quickopen').showModal(); $('quickopen-input').focus();
}
function drawQuick() {
  const q = $('quickopen-input').value.trim();
  quickShown = quickItems.map(i => ({i, s: fuzzy(q, i.label)})).filter(x => x.s > 0).sort((a, b) => b.s - a.s || a.i.label.localeCompare(b.i.label)).slice(0, 30).map(x => x.i);
  if (quickSel >= quickShown.length) quickSel = 0;
  $('quickopen-list').innerHTML = quickShown.map((i, n) => `<div class="qo ${n === quickSel ? 'on' : ''}" data-n="${n}"><span class="tag">${i.kind}</span> ${esc(i.label)} <span class="dim" style="float:right">${esc(i.hint)}</span></div>`).join('') || '<div class="dim" style="padding:8px">Nothing matches.</div>';
  $('quickopen-list').querySelectorAll('.qo').forEach(el => el.onclick = () => runQuick(+el.dataset.n));
}
function runQuick(n) { const it = quickShown[n]; $('quickopen').close(); if (it) setTimeout(() => it.run?.(), 0); }
$('quickopen-input').oninput = () => { quickSel = 0; drawQuick(); };
$('quickopen-input').onkeydown = e => {
  if (e.key === 'ArrowDown') { quickSel = Math.min(quickShown.length - 1, quickSel + 1); drawQuick(); e.preventDefault(); }
  else if (e.key === 'ArrowUp') { quickSel = Math.max(0, quickSel - 1); drawQuick(); e.preventDefault(); }
  else if (e.key === 'Enter') { runQuick(quickSel); e.preventDefault(); }
  e.stopPropagation();
};

// ---- rebinding (Settings)
let capturing = null;
function renderShortcutSettings() {
  const el = $('shortcut-table'); if (!el) return;
  el.innerHTML = '<table>' + shortcutList.map(a => `<tr><td>${esc(a.label)}</td><td><kbd id="sk-${a.id}">${esc(a.keys || 'none')}</kbd></td>
    <td style="white-space:nowrap"><button class="b sec" onclick="captureShortcut('${a.id}')">Change</button> <button class="b sec" title="Switch this shortcut off" onclick="setShortcut('${a.id}', '')">Off</button> <button class="b sec" onclick="setShortcut('${a.id}', null)">Default</button></td></tr>`).join('') + '</table>';
}
function captureShortcut(id) {
  const k = $('sk-' + id); k.textContent = 'press the keys…'; capturing = id;
  const handler = e => {
    e.preventDefault(); e.stopPropagation();
    const combo = comboOf(e); if (!combo) return;
    document.removeEventListener('keydown', handler, true); capturing = null;
    if (combo === 'Escape' && id !== 'stop') { renderShortcutSettings(); return; }
    setShortcut(id, combo);
  };
  document.addEventListener('keydown', handler, true);
}
// value: a combination, '' (off) or null (back to the default). A combination that another action uses is refused.
async function setShortcut(id, value) {
  const a = shortcutList.find(x => x.id === id);
  const keys = value === null ? a.default : value;
  const clash = keys && shortcutList.find(x => x.id !== id && x.keys === keys);
  if (clash) { alert(`${keys} is already used by "${clash.label}". Change or switch off that one first.`); renderShortcutSettings(); return; }
  const overrides = {...(window._settings?.shortcuts || {})};
  if (value === null || keys === a.default && value !== '') delete overrides[id]; else overrides[id] = keys;
  try {
    const s = await api('/api/settings'); s.shortcuts = overrides;
    await api('/api/settings', {method: 'PUT', body: s});
    if (window._settings) window._settings.shortcuts = overrides;
  } catch (e) { alert(e.message); }
  await loadShortcuts();
}
loadShortcuts();
