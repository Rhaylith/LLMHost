// ---------------------------------------------------------------- live hardware load (header + Settings)
const hwHist = {cpu: [], gpu: [], ram: []};
function hwPush(a, v) { a.push(v ?? 0); while (a.length > 60) a.shift(); }
function hwSpark(canvas, series) {
  const c = canvas.getContext('2d'), w = canvas.width, h = canvas.height;
  c.clearRect(0, 0, w, h);
  const draw = (arr, color) => {
    if (arr.length < 2) return;
    c.beginPath(); c.strokeStyle = color; c.lineWidth = 1.2;
    arr.forEach((v, i) => { const x = i * (w - 1) / 59, y = h - 1 - Math.min(100, v) / 100 * (h - 2); i ? c.lineTo(x, y) : c.moveTo(x, y); });
    c.stroke();
  };
  draw(series.cpu, '#58a6ff'); draw(series.gpu, '#3fb950');
}
const pct = v => v == null ? '–' : Math.round(v) + '%';
function hwRender(s) {
  const gpu = (s.gpus || [])[0];
  const ramPct = s.ramTotalBytes ? (s.ramUsedBytes / s.ramTotalBytes) * 100 : null;
  hwPush(hwHist.cpu, s.cpuPercent); hwPush(hwHist.gpu, gpu?.utilization); hwPush(hwHist.ram, ramPct);
  $('hwlive-text').textContent = `CPU ${pct(s.cpuPercent)} · RAM ${pct(ramPct)}` + (gpu ? ` · GPU ${pct(gpu.utilization)}` + (gpu.dedicatedUsedBytes != null ? ` · VRAM ${fmt(gpu.dedicatedUsedBytes)}` : '') : '');
  hwSpark($('hwlive-spark'), hwHist);
  if ($('settings').classList.contains('on')) {
    const bar = (v, label) => `<div style="display:flex;align-items:center;gap:8px;margin:2px 0"><span style="width:120px">${label}</span><div style="flex:1;max-width:320px;height:8px;background:var(--line,#333);border-radius:4px"><div style="width:${Math.min(100, v ?? 0)}%;height:8px;background:var(--ok,#3fb950);border-radius:4px"></div></div><span style="width:48px;text-align:right">${pct(v)}</span></div>`;
    let html = bar(s.cpuPercent, 'Processor') + bar(ramPct, `Memory (${fmt(s.ramUsedBytes)} of ${fmt(s.ramTotalBytes)})`);
    html += `<div class="dim">This app: ${fmt(s.process.workingSetBytes)} in memory (${fmt(s.process.privateBytes)} private)</div>`;
    for (const g of s.gpus || []) {
      const vramPct = g.dedicatedUsedBytes != null && g.dedicatedTotalBytes ? g.dedicatedUsedBytes / g.dedicatedTotalBytes * 100 : null;
      html += `<h3>${esc(g.name)}</h3>` + bar(g.utilization, 'GPU load') +
        bar(vramPct, g.dedicatedUsedBytes != null ? `VRAM (${fmt(g.dedicatedUsedBytes)} of ${fmt(g.dedicatedTotalBytes)})` : 'VRAM') +
        `<div class="dim">${g.processDedicatedBytes != null ? 'This app: ' + fmt(g.processDedicatedBytes) + ' dedicated' + (g.processSharedBytes ? ' + ' + fmt(g.processSharedBytes) + ' shared' : '') + ' · ' : ''}temperature ${g.temperatureC != null ? g.temperatureC + ' °C' : 'n/a'}</div>`;
    }
    if (s.models?.length) html += '<h3>Loaded models</h3>' + s.models.map(m => `<div class="dim">${esc(m.id)}: expected ${m.estimatedGpuBytes != null ? fmt(m.estimatedGpuBytes) + ' on the GPU, ' : ''}${m.estimatedCpuBytes != null ? fmt(m.estimatedCpuBytes) + ' in system memory' : 'no estimate'}${m.weightSharers > 1 ? ' (weights shared by ' + m.weightSharers + ' instances)' : ''}</div>`).join('');
    if (s.notes?.length) html += '<div class="dim" style="margin-top:6px">' + s.notes.map(esc).join('<br>') + '</div>';
    $('hwlive-body').innerHTML = html;
  }
}
async function hwPoll() {
  if (document.hidden) return;
  try { hwRender(await api('/api/hardware/live')); } catch { $('hwlive-text').textContent = ''; }
}
setInterval(hwPoll, 2000); hwPoll();
$('hwlive').onclick = () => document.querySelector('nav button[data-tab="settings"]').click();

refreshStatus(); refreshPresets(); renderChatList(); renderMsgs();
