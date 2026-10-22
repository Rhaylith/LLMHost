// ---------------------------------------------------------------- completion notifications
// In the desktop window the tray shows them (the window knows when it is minimised); in a normal browser this page uses the Notifications API.
// A click goes to the view the event is about.
const IN_SHELL = !!window.chrome?.webview;
let notifLast = 0, notifSettings = {enabled: true, onlyWhenMinimised: true};

function openView(name) {
  const tab = {chat: 'chat', models: 'models', discover: 'discover', settings: 'settings'}[name] || 'chat';
  window.focus(); document.querySelector(`nav button[data-tab="${tab}"]`)?.click();
}
const pageInFront = () => document.visibilityState === 'visible' && document.hasFocus();

function showWebNotification(e) {
  if (IN_SHELL || typeof Notification === 'undefined' || Notification.permission !== 'granted') return;
  if (pageInFront() && notifSettings.onlyWhenMinimised && e.kind !== 'test') return;
  const n = new Notification(e.title, {body: e.body, tag: e.kind + e.id});
  n.onclick = () => { openView(e.view); n.close(); };
}
async function pollNotifications() {
  try {
    const r = await api('/api/notifications?since=' + notifLast);
    notifSettings = r.settings || notifSettings;
    if (notifLast === 0 && !window._notifStarted) { notifLast = r.last; window._notifStarted = true; return; }       // do not announce what happened before this page was opened
    for (const e of r.events) showWebNotification(e);
    notifLast = r.last;
  } catch (e) { /* offline */ }
}
setInterval(() => { if (!document.hidden || !IN_SHELL) pollNotifications(); }, 3000);
pollNotifications();

// an answer that finishes while the window is not in front
function notifyGenerationDone(title) {
  if (pageInFront() && notifSettings.onlyWhenMinimised) return;
  api('/api/notifications', {method: 'POST', body: {kind: 'generation', title: t('notify.answer_ready'), body: title || '', view: 'chat'}}).catch(() => {});
}

// ---- settings card
async function renderNotifySettings() {
  try {
    const s = (await api('/api/settings')).notifications || {};
    const set = (id, v) => { $(id).checked = !!v; };
    set('nt-on', s.enabled); set('nt-dl', s.downloads); set('nt-model', s.modelLoaded); set('nt-gen', s.generation); set('nt-upd', s.updateAvailable); set('nt-min', s.onlyWhenMinimised);
    $('nt-secs').value = s.loadSeconds ?? 10;
    $('nt-perm').style.display = !IN_SHELL && typeof Notification !== 'undefined' && Notification.permission === 'default' ? '' : 'none';
  } catch (e) {}
}
async function saveNotifySettings() {
  try {
    const s = await api('/api/settings');
    s.notifications = {enabled: $('nt-on').checked, downloads: $('nt-dl').checked, modelLoaded: $('nt-model').checked, generation: $('nt-gen').checked, updateAvailable: $('nt-upd').checked,
      onlyWhenMinimised: $('nt-min').checked, loadSeconds: Number($('nt-secs').value) || 0};
    await api('/api/settings', {method: 'PUT', body: s}); notifSettings = s.notifications;
  } catch (e) { alert(e.message); }
}
async function notifyPermission() { if (typeof Notification !== 'undefined') { await Notification.requestPermission(); renderNotifySettings(); } }
async function notifyTest() { try { await api('/api/notifications', {method: 'POST', body: {kind: 'test', title: 'LLMHost', body: t('notify.test_body'), view: 'settings'}}); } catch (e) { alert(e.message); } }
document.querySelectorAll('#notify-card input').forEach(i => i.onchange = saveNotifySettings);
renderNotifySettings();
