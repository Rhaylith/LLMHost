// ---------------------------------------------------------------- chat list: folders, search, selection, drag and drop, import / export
let chatFolders = [], chatItems = [], chatSelected = new Set(), chatSearchHits = null, chatLastClicked = null;
let openFolders = new Set(JSON.parse(localStorage.getItem('openFolders') || '[]'));
const saveOpenFolders = () => { try { localStorage.setItem('openFolders', JSON.stringify([...openFolders])); } catch (e) {} };

async function renderChatList() {
  [chatItems, chatFolders] = await Promise.all([api('/api/chats').catch(() => []), api('/api/chat-folders').catch(() => [])]);
  drawChatList();
}
function chatRow(c, depth) {
  const sel = chatSelected.has(c.id);
  return `<div class="it ${c.id === conv.id ? 'on' : ''} ${sel ? 'sel' : ''}" draggable="true" data-chat="${c.id}" style="padding-left:${8 + depth * 14}px" title="${esc(c.title)}">
    ${c.pinned ? '<span title="Pinned">📌</span> ' : ''}<span class="ttl">${esc(c.title)}</span><button class="more" data-menu="${c.id}" title="More">⋯</button></div>`;
}
function folderRow(f, depth) {
  const open = openFolders.has(f.id), n = chatItems.filter(c => c.folderId === f.id).length;
  return `<div class="it folder" draggable="true" data-folder="${f.id}" style="padding-left:${4 + depth * 14}px">${open ? '▾' : '▸'} 📁 <span class="ttl">${esc(f.name)}</span> <span class="dim">${n || ''}</span><button class="more" data-fmenu="${f.id}" title="More">⋯</button></div>`;
}
function folderTree(parentId, depth) {
  let html = '';
  for (const f of chatFolders.filter(x => (x.parentId || null) === parentId)) {
    html += folderRow(f, depth);
    if (openFolders.has(f.id)) html += folderTree(f.id, depth + 1) + chatItems.filter(c => c.folderId === f.id).sort(byPinThenDate).map(c => chatRow(c, depth + 1)).join('');
  }
  return html;
}
const byPinThenDate = (a, b) => (b.pinned - a.pinned) || (new Date(b.updatedAt) - new Date(a.updatedAt));
function drawChatList() {
  const el = $('chatlist');
  const bulk = $('chat-bulk');
  bulk.style.display = chatSelected.size ? 'flex' : 'none';
  bulk.querySelector('span').textContent = chatSelected.size + ' selected';
  if (chatSearchHits) {
    el.innerHTML = chatSearchHits.length ? chatSearchHits.map(h => `<div class="it hit" data-hit="${h.chatId}" data-mid="${h.messageId || ''}"><b class="ttl">${esc(h.title)}</b><div class="dim snip">${esc(h.snippet)}</div></div>`).join('')
      : '<div class="dim" style="padding:6px">No chat matches.</div>';
  } else {
    const known = new Set(chatFolders.map(f => f.id));
    const top = chatItems.filter(c => !c.folderId || !known.has(c.folderId)).sort(byPinThenDate);
    el.innerHTML = folderTree(null, 0) + top.map(c => chatRow(c, 0)).join('');
  }
  wireChatList(el);
}
function wireChatList(el) {
  el.querySelectorAll('[data-chat]').forEach(r => {
    const id = r.dataset.chat;
    r.onclick = e => {
      if (e.target.closest('.more')) return;
      if (e.ctrlKey || e.metaKey) { chatSelected.has(id) ? chatSelected.delete(id) : chatSelected.add(id); chatLastClicked = id; drawChatList(); return; }
      if (e.shiftKey && chatLastClicked) {
        const ids = [...el.querySelectorAll('[data-chat]')].map(x => x.dataset.chat), a = ids.indexOf(chatLastClicked), b = ids.indexOf(id);
        ids.slice(Math.min(a, b), Math.max(a, b) + 1).forEach(i => chatSelected.add(i)); drawChatList(); return;
      }
      chatSelected.clear(); chatLastClicked = id; openChat(id);
    };
    r.oncontextmenu = e => { e.preventDefault(); chatMenu(id, e.clientX, e.clientY); };
    r.ondragstart = e => { e.dataTransfer.setData('text/plain', 'chat:' + (chatSelected.has(id) ? [...chatSelected].join(',') : id)); };
  });
  el.querySelectorAll('[data-menu]').forEach(b => b.onclick = e => { e.stopPropagation(); const r = b.getBoundingClientRect(); chatMenu(b.dataset.menu, r.left, r.bottom); });
  el.querySelectorAll('[data-fmenu]').forEach(b => b.onclick = e => { e.stopPropagation(); const r = b.getBoundingClientRect(); folderMenu(b.dataset.fmenu, r.left, r.bottom); });
  el.querySelectorAll('[data-folder]').forEach(r => {
    const id = r.dataset.folder;
    r.onclick = e => { if (e.target.closest('.more')) return; openFolders.has(id) ? openFolders.delete(id) : openFolders.add(id); saveOpenFolders(); drawChatList(); };
    r.oncontextmenu = e => { e.preventDefault(); folderMenu(id, e.clientX, e.clientY); };
    r.ondragstart = e => { e.dataTransfer.setData('text/plain', 'folder:' + id); };
    r.ondragover = e => { e.preventDefault(); r.classList.add('drop'); };
    r.ondragleave = () => r.classList.remove('drop');
    r.ondrop = e => { e.preventDefault(); e.stopPropagation(); r.classList.remove('drop'); dropOn(id, e.dataTransfer.getData('text/plain')); };
  });
  el.querySelectorAll('[data-hit]').forEach(r => r.onclick = () => openChat(r.dataset.hit, r.dataset.mid));
  // dropping on the empty part of the list moves things back to the top level
  el.ondragover = e => e.preventDefault();
  el.ondrop = e => { e.preventDefault(); dropOn(null, e.dataTransfer.getData('text/plain')); };
}
async function dropOn(folderId, payload) {
  try {
    const [kind, ids] = payload.split(/:(.*)/s);
    if (kind === 'chat') for (const id of ids.split(',')) await api(`/api/chats/${id}/meta`, {method:'PUT', body:{folderId}});
    else if (kind === 'folder') { const f = chatFolders.find(x => x.id === ids); if (f && f.id !== folderId) await api('/api/chat-folders/' + f.id, {method:'PUT', body:{name: f.name, parentId: folderId, order: f.order}}); }
    if (folderId) { openFolders.add(folderId); saveOpenFolders(); }
  } catch (e) { alert(e.message); }
  chatSelected.clear(); renderChatList();
}
async function openChat(id, messageId) {
  const c = await api('/api/chats/' + id); conv = c; editing = null; conv.docs = (c.documents || []).map(d => ({id:d.id, name:d.name, chars:d.chars}));
  { const t = fromStored(c.messages, c.activeLeaf); conv.all = t.all; conv.messages = []; const leaf = t.all.find(n => n.id === t.leaf); if (leaf) setBranchVia(leaf); }
  if (messageId) { const hit = conv.all.find(n => n.id === messageId); if (hit) setBranchVia(hit); }
  if (c.generation) applyGen(c.generation);
  $('p-system').value = c.systemPrompt || '';
  if (c.model && [...$('p-model').options].some(o => o.value === c.model)) $('p-model').value = c.model;
  renderMsgs(); renderAttachments(); drawChatList();
  if (messageId) { const el = document.querySelector(`[data-msg="${messageId}"]`); if (el) { el.scrollIntoView({block: 'center'}); el.classList.add('flash'); setTimeout(() => el.classList.remove('flash'), 1500); } }
}

// ---- search (server side, over every chat)
let chatSearchTimer = null;
$('chat-search').oninput = () => {
  clearTimeout(chatSearchTimer);
  const q = $('chat-search').value.trim();
  if (!q) { chatSearchHits = null; drawChatList(); return; }
  chatSearchTimer = setTimeout(async () => {
    try { chatSearchHits = (await api('/api/chats/search?q=' + encodeURIComponent(q))).hits; } catch (e) { chatSearchHits = []; }
    drawChatList();
  }, 250);
};

// ---- small context menu
function showMenu(items, x, y) {
  const m = $('ctxmenu');
  m.innerHTML = items.map((it, i) => it === '-' ? '<hr>' : `<div class="mi" data-i="${i}">${esc(it.label)}</div>`).join('');
  m.style.display = 'block';
  m.style.left = Math.min(x, innerWidth - 190) + 'px'; m.style.top = Math.min(y, innerHeight - items.length * 28 - 10) + 'px';
  m.querySelectorAll('.mi').forEach(el => el.onclick = async () => { m.style.display = 'none'; try { await items[+el.dataset.i].run(); } catch (e) { alert(e.message); } });
}
document.addEventListener('click', () => { $('ctxmenu').style.display = 'none'; });
function chatMenu(id, x, y) {
  const c = chatItems.find(i => i.id === id); if (!c) return;
  const targets = chatSelected.has(id) && chatSelected.size > 1 ? [...chatSelected] : [id];
  const moveItems = [{label: '(top level)', run: () => moveChats(targets, null)}, ...chatFolders.map(f => ({label: '→ ' + f.name, run: () => moveChats(targets, f.id)}))];
  showMenu([
    {label: 'Rename', run: () => renameChat(id)},
    {label: c.pinned ? 'Unpin' : 'Pin to top', run: async () => { await api(`/api/chats/${id}/meta`, {method:'PUT', body:{pinned: !c.pinned}}); renderChatList(); }},
    {label: 'Duplicate', run: async () => { await api(`/api/chats/${id}/duplicate`, {method:'POST'}); renderChatList(); }},
    '-', ...moveItems.map(m => ({label: 'Move ' + m.label, run: m.run})),
    '-',
    {label: 'Export as Markdown', run: () => exportChat(id, 'markdown')}, {label: 'Export as text', run: () => exportChat(id, 'text')}, {label: 'Export as JSON', run: () => exportChat(id, 'json')},
    '-', {label: targets.length > 1 ? `Delete ${targets.length} chats` : 'Delete', run: () => deleteChats(targets)},
  ], x, y);
}
function folderMenu(id, x, y) {
  const f = chatFolders.find(i => i.id === id); if (!f) return;
  const sibs = chatFolders.filter(s => (s.parentId || null) === (f.parentId || null)).sort((a, b) => a.order - b.order);
  const i = sibs.findIndex(s => s.id === id);
  const swap = async d => { const o = sibs[i + d]; if (!o) return; await api('/api/chat-folders/' + f.id, {method:'PUT', body:{name: f.name, parentId: f.parentId, order: o.order}}); await api('/api/chat-folders/' + o.id, {method:'PUT', body:{name: o.name, parentId: o.parentId, order: f.order}}); renderChatList(); };
  showMenu([
    {label: 'Rename', run: async () => { const n = prompt('Folder name', f.name); if (n) { await api('/api/chat-folders/' + id, {method:'PUT', body:{name: n, parentId: f.parentId, order: f.order}}); renderChatList(); } }},
    {label: 'New sub-folder', run: () => newFolder(id)},
    {label: 'Move up', run: () => swap(-1)}, {label: 'Move down', run: () => swap(1)},
    '-',
    {label: 'Delete folder (keep chats)', run: async () => { if (confirm('Delete the folder "' + f.name + '"? Its chats move up one level.')) { await api('/api/chat-folders/' + id, {method:'DELETE'}); renderChatList(); } }},
    {label: 'Delete folder and its chats', run: async () => { if (confirm('Delete the folder "' + f.name + '" AND every chat inside it?')) { await api('/api/chat-folders/' + id + '?chats=delete', {method:'DELETE'}); renderChatList(); } }},
  ], x, y);
}
async function newFolder(parentId) {
  const name = prompt('Folder name'); if (!name) return;
  const id = Math.random().toString(16).slice(2, 12);
  await api('/api/chat-folders/' + id, {method:'PUT', body:{name, parentId: parentId || null}});
  if (parentId) { openFolders.add(parentId); saveOpenFolders(); }
  renderChatList();
}
async function moveChats(ids, folderId) { for (const id of ids) await api(`/api/chats/${id}/meta`, {method:'PUT', body:{folderId}}); if (folderId) { openFolders.add(folderId); saveOpenFolders(); } chatSelected.clear(); renderChatList(); }
async function renameChat(id) {
  const c = chatItems.find(i => i.id === id); const t = prompt('Chat title', c?.title || ''); if (!t) return;
  await api(`/api/chats/${id}/rename`, {method:'PUT', body:{title: t}});
  if (conv.id === id) conv.title = t;
  renderChatList();
}
async function deleteChats(ids) {
  if (!confirm(ids.length > 1 ? `Delete ${ids.length} chats?` : 'Delete this chat?')) return;
  await api('/api/chats/bulk-delete', {method:'POST', body:{ids}});
  if (ids.includes(conv.id)) newChat();
  chatSelected.clear(); renderChatList();
}
function bulkDelete() { deleteChats([...chatSelected]); }
function clearSelection() { chatSelected.clear(); drawChatList(); }

// ---- export / import
async function exportChat(id, format) {
  const r = await api(`/api/chats/${id}/export?format=${format}`, {raw: true});
  const blob = await r.blob();
  const name = /filename="([^"]+)"/.exec(r.headers.get('content-disposition') || '')?.[1] || 'chat.' + format;
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 2000);
}
function importChats() { $('chat-import-file').click(); }
$('chat-import-file').onchange = async e => {
  const f = e.target.files[0]; e.target.value = ''; if (!f) return;
  try { const r = await api('/api/chats/import', {method:'POST', body:{data: await f.text()}}); alert('Imported ' + r.imported + ' chat(s).'); renderChatList(); }
  catch (err) { alert(err.message); }
};

// ---- title made up by the model after the first answer
async function maybeAutoTitle() {
  const answers = conv.messages.filter(m => m.role === 'assistant' && m.content).length;
  if (answers !== 1) return;
  try { const r = await api(`/api/chats/${conv.id}/auto-title`, {method:'POST'}); if (r.source !== 'unchanged') { conv.title = r.title; renderChatList(); } } catch (e) {}
}
