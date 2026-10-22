// ---------------------------------------------------------------- first-run wizard: welcome and mode, hardware, models folder, recommended models, token, done
// Every step has "Skip"; nothing here blocks the app.
let wiz = {step: 0, data: null, mode: 'user', downloaded: null};
const WIZ_STEPS = ['welcome', 'hardware', 'folder', 'models', 'token', 'done'];

async function maybeOnboard() { try { const d = await api('/api/onboarding'); if (d.needed) openWizard(d); } catch (e) {} }
async function openWizard(data) {
  wiz = {step: 0, data: data || await api('/api/onboarding'), mode: 'user', downloaded: null};
  $('wizard').showModal(); drawWizard();
}
function wizClose() { $('wizard').close(); }
function wizOpenRuntimes() { wizClose(); document.querySelector('nav button[data-tab="settings"]').click(); }
async function wizFinish(open) {
  try { await api('/api/onboarding/complete', {method: 'POST', body: {mode: wiz.mode}}); } catch (e) {}
  setBodyMode(wiz.mode); wizClose();
  if (open && wiz.downloaded) { try { await api('/api/models/load', {method: 'POST', body: {model: wiz.downloaded}}); } catch (e) {} await refreshStatus(); }
  if (open) document.querySelector('nav button[data-tab="chat"]').click();
}
const wizGb = b => (b / 1073741824).toFixed(b < 10737418240 ? 1 : 0) + ' GB';
function wizNav(canNext = true) {
  const last = wiz.step === WIZ_STEPS.length - 1;
  return `<div class="row" style="margin-top:14px">${wiz.step > 0 ? `<button class="b sec" onclick="wizGo(-1)">${esc(t('wizard.back'))}</button>` : ''}<span style="flex:1"></span>
    <button class="b sec" onclick="wizFinish(false)">${esc(t('wizard.skip'))}</button>
    ${last ? `<button class="b" onclick="wizFinish(true)">${esc(t('wizard.start'))}</button>` : `<button class="b" onclick="wizGo(1)" ${canNext ? '' : 'disabled'}>${esc(t('wizard.next'))}</button>`}</div>`;
}
function wizGo(d) { wiz.step = Math.max(0, Math.min(WIZ_STEPS.length - 1, wiz.step + d)); drawWizard(); }
function drawWizard() {
  const d = wiz.data, h = d.hardware, body = $('wizard-body');
  $('wizard-steps').textContent = `${wiz.step + 1} / ${WIZ_STEPS.length}`;
  const step = WIZ_STEPS[wiz.step];
  if (step === 'welcome') {
    body.innerHTML = `<h3>${esc(t('wizard.welcome'))}</h3><p>${esc(t('wizard.welcome_text'))}</p>
      ${['user', 'power', 'developer'].map(m => `<label class="card" style="display:block;cursor:pointer"><input type="radio" name="wz-mode" value="${m}" style="width:auto" ${wiz.mode === m ? 'checked' : ''} onchange="wiz.mode='${m}'"> <b>${esc(t('wizard.mode_' + m))}</b><div class="dim">${esc(t('wizard.mode_' + m + '_text'))}</div></label>`).join('')}${wizNav()}`;
  } else if (step === 'hardware') {
    const gpus = h.gpus.length ? h.gpus.map(g => `<li>${esc(g.name)} <span class="dim">${esc(g.vendor)}${g.integrated ? ' · ' + esc(t('wizard.integrated')) : ''} · ${wizGb(g.usableBytes)}</span></li>`).join('') : `<li class="dim">${esc(t('wizard.no_gpu'))}</li>`;
    const feats = [['AVX', h.cpuFeatures.avx], ['AVX2', h.cpuFeatures.avx2], ['AVX-512', h.cpuFeatures.avx512]].filter(f => f[1]).map(f => f[0]).join(', ') || '–';
    body.innerHTML = `<h3>${esc(t('wizard.hardware'))}</h3>
      <ul><li>${esc(h.cpu)} <span class="dim">· ${h.physicalCores} / ${h.logicalCores}</span></li><li>${esc(t('wizard.ram'))}: ${wizGb(h.ramTotalBytes)} <span class="dim">(${wizGb(h.ramAvailableBytes)} ${esc(t('wizard.free'))})</span></li>${gpus}
      <li>${esc(t('wizard.cpu_features'))}: ${feats}</li><li>Vulkan: ${h.vulkan ? '<span class="ok">✓</span>' : '–'}</li></ul>
      <p>${h.recommendedRuntime ? esc(t('wizard.runtime', {name: h.recommendedRuntime.name})) + (h.recommendedRuntime.installed ? '' : ` <button class="b sec" onclick="wizOpenRuntimes()">${esc(t('wizard.open_runtimes'))}</button>`) : esc(t('wizard.runtime_auto'))}</p>
      <p class="dim">${esc(t('wizard.budget', {size: wizGb(h.budgetBytes)}))}</p>${wizNav()}`;
  } else if (step === 'folder') {
    const low = d.folder.freeBytes < 20 * 1073741824;
    body.innerHTML = `<h3>${esc(t('wizard.folder'))}</h3><p>${esc(t('wizard.folder_text'))}</p>
      <div class="row"><input id="wz-folder" value="${esc(d.folder.path)}"><button class="b sec" onclick="wizFolder()">${esc(t('wizard.use_folder'))}</button></div>
      <p class="${low ? 'err' : 'dim'}" id="wz-free">${esc(t('wizard.free_space', {size: wizGb(d.folder.freeBytes)}))}${low ? ' · ' + esc(t('wizard.low_space')) : ''}</p><div id="wz-fmsg" class="dim"></div>${wizNav()}`;
  } else if (step === 'models') {
    body.innerHTML = `<h3>${esc(t('wizard.models'))}</h3><p class="dim">${esc(t('wizard.models_text'))}</p>` + (d.recommendations.length ? d.recommendations.map(r =>
      `<div class="card"><b>${esc(r.name)}</b> <span class="tag">${esc(t('wizard.slot_' + r.slot))}</span> <span class="dim">~${wizGb(r.estimatedBytes)}</span>${r.fitsFreeSpace ? '' : ' <span class="err">' + esc(t('wizard.no_space')) + '</span>'}
       <div class="dim">${esc(r.reason)}</div><div class="row" style="margin-top:6px"><button class="b" onclick='wizDownload(${JSON.stringify(r.repo)}, ${JSON.stringify(r.quant)}, this)'>${esc(t('wizard.download'))}</button><span class="dim"></span></div></div>`).join('') : `<p class="dim">${esc(t('wizard.no_models'))}</p>`) + wizNav();
  } else if (step === 'token') {
    body.innerHTML = `<h3>${esc(t('wizard.token'))}</h3><p>${esc(t('wizard.token_text'))}</p>
      <div class="row"><input id="wz-token" type="password" placeholder="hf_…"><button class="b sec" onclick="wizToken()">${esc(t('wizard.save_token'))}</button></div><div id="wz-tmsg" class="dim">${d.hasToken ? esc(t('wizard.has_token')) : ''}</div>${wizNav()}`;
  } else {
    body.innerHTML = `<h3>${esc(t('wizard.done'))}</h3><p>${esc(t(wiz.downloaded ? 'wizard.done_model' : 'wizard.done_text'))}</p><p class="dim">${esc(t('wizard.rerun'))}</p>${wizNav()}`;
  }
}
async function wizFolder() {
  const to = $('wz-folder').value.trim(), msg = $('wz-fmsg');
  if (!to) return;
  try { await api('/api/models/directory/move', {method: 'POST', body: {to, mode: 'switchOnly'}}); await new Promise(r => setTimeout(r, 300)); wiz.data = await api('/api/onboarding'); drawWizard(); $('wz-fmsg').textContent = '✓'; }
  catch (e) { msg.className = 'err'; msg.textContent = e.message; }
}
async function wizDownload(repo, quant, btn) {
  btn.disabled = true;
  const span = btn.nextElementSibling;
  try {
    const job = await api('/api/downloads', {method: 'POST', body: {repo, quant}});
    const poll = async () => {
      const jobs = await api('/api/downloads').catch(() => []); const j = jobs.find(x => x.id === job.id);
      if (!j) return;
      span.textContent = j.state === 'completed' ? '✓' : j.state === 'failed' ? (j.error || '') : (j.progress * 100).toFixed(0) + ' %';
      if (j.state === 'completed') { wiz.downloaded = wiz.downloaded || j.finalPath || null; return; }
      if (j.state === 'downloading' || j.state === 'queued') setTimeout(poll, 1000);
    };
    poll();
  } catch (e) { btn.disabled = false; span.textContent = e.message; }
}
async function wizToken() {
  const tok = $('wz-token').value.trim(), msg = $('wz-tmsg'); if (!tok) return;
  try {
    const r = await api('/api/hf/whoami', {method: 'POST', body: {token: tok}});
    if (!r.valid) { msg.className = 'err'; msg.textContent = r.error; return; }
    const s = await api('/api/settings'); s.huggingFaceToken = tok; await api('/api/settings', {method: 'PUT', body: s});
    msg.className = 'ok'; msg.textContent = '✓ ' + r.name;
  } catch (e) { msg.className = 'err'; msg.textContent = e.message; }
}
