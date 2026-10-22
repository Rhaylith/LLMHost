// ---------------------------------------------------------------- application updates
async function loadUpdate() {
  try {
    const u = await api('/api/update');
    $('up-current').textContent = u.current; $('up-channel').value = u.channel;
    showUpdate(u);
  } catch (e) { $('up-msg').textContent = e.message; }
}
function showUpdate(u) {
  const msg = $('up-msg'), btn = $('up-install');
  btn.style.display = 'none';
  if (!u.feedConfigured) { msg.className = 'dim'; msg.textContent = 'No update feed is set.'; return; }
  if (u.lastError) { msg.className = 'err'; msg.textContent = u.lastError; return; }
  if (u.latest) {
    msg.className = 'ok'; msg.innerHTML = `Version ${esc(u.latest.version)}${u.latest.prerelease ? ' (pre-release)' : ''} is available.` + (u.latest.notes ? `<div class="dim" style="white-space:pre-wrap">${esc(u.latest.notes)}</div>` : '');
    if (u.latest.canInstall) btn.style.display = ''; else msg.insertAdjacentHTML('beforeend', ` <a href="${esc(u.latest.url)}" target="_blank" rel="noopener">Download page</a>`);
  } else { msg.className = 'dim'; msg.textContent = u.lastChecked ? 'You have the latest version.' : 'Not checked yet.'; }
}
async function checkUpdate() { $('up-msg').textContent = 'Checking…'; try { showUpdate(await api('/api/update/check', {method: 'POST'})); } catch (e) { $('up-msg').className = 'err'; $('up-msg').textContent = e.message; } }
async function installUpdate() {
  if (!confirm('Download and install the update? LLMHost closes and starts again.')) return;
  $('up-msg').className = 'dim'; $('up-msg').textContent = 'Downloading and verifying…';
  try { await api('/api/update/install', {method: 'POST'}); $('up-msg').textContent = 'The updater is running; this window closes.'; } catch (e) { $('up-msg').className = 'err'; $('up-msg').textContent = e.message; }
}
$('up-channel').onchange = async e => {
  try { const s = await api('/api/settings'); s.update = Object.assign({}, s.update, {channel: e.target.value}); await api('/api/settings', {method: 'PUT', body: s}); checkUpdate(); } catch (err) { alert(err.message); }
};
loadUpdate();
