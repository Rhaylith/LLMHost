// ---------------------------------------------------------------- theme (light | dark | system) and accent colour
let uiTheme = 'system', uiAccent = '';
const darkQuery = matchMedia('(prefers-color-scheme: dark)');
function readableOn(hex) {                       // black or white text, whichever reads better on this colour
  const h = hex.replace('#', ''); const c = [0, 2, 4].map(i => parseInt(h.slice(i, i + 2), 16) / 255).map(v => v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
  const L = 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  return (1.05 / (L + 0.05)) >= ((L + 0.05) / 0.05) ? '#ffffff' : '#000000';
}
function applyTheme(theme, accent) {
  uiTheme = ['light', 'dark'].includes(theme) ? theme : 'system'; uiAccent = /^#[0-9a-f]{6}$/i.test(accent || '') ? accent : '';
  const dark = uiTheme === 'dark' || (uiTheme === 'system' && darkQuery.matches);
  const root = document.documentElement;
  root.dataset.theme = dark ? 'dark' : 'light';
  if (uiAccent) { root.style.setProperty('--acc2', uiAccent); root.style.setProperty('--on-acc', readableOn(uiAccent)); }
  else { root.style.removeProperty('--acc2'); root.style.removeProperty('--on-acc'); }
  try { localStorage.setItem('uiTheme', uiTheme); uiAccent ? localStorage.setItem('uiAccent', uiAccent) : localStorage.removeItem('uiAccent'); } catch (e) {}
  if ($('ap-theme')) { $('ap-theme').value = uiTheme; $('ap-accent').value = uiAccent || getComputedStyle(root).getPropertyValue('--acc2').trim().replace(/^#(.)(.)(.)$/, '#$1$1$2$2$3$3') || '#1f5fd6'; }
}
darkQuery.addEventListener('change', () => { if (uiTheme === 'system') applyTheme('system', uiAccent); });
async function saveAppearance(change) {
  try { const s = await api('/api/settings'); Object.assign(s, change); await api('/api/settings', {method: 'PUT', body: s}); } catch (e) { alert(e.message); }
}
async function loadTheme() { try { const s = await api('/api/settings'); applyTheme(s.uiTheme, s.uiAccent); } catch (e) { applyTheme(uiTheme, uiAccent); } }
$('ap-theme').onchange = e => { applyTheme(e.target.value, uiAccent); saveAppearance({uiTheme: e.target.value}); };
$('ap-accent').oninput = e => applyTheme(uiTheme, e.target.value);
$('ap-accent').onchange = e => saveAppearance({uiAccent: e.target.value});
$('ap-accent-reset').onclick = () => { applyTheme(uiTheme, ''); saveAppearance({uiAccent: null}); };
loadTheme();
$('ap-lang').onchange = e => { saveAppearance({uiLanguage: e.target.value}); loadI18n(e.target.value); };
