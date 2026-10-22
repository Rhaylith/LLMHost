// ---------------------------------------------------------------- translations: locales/<lang>.json, t('key', {n}), data-i18n attributes
// English is the fallback for every language. Values may be plural objects {one, other, ...} (Intl.PluralRules) and use {name} placeholders.
// The server and the command line stay in English.
const LANGUAGES = {en: 'English', de: 'Deutsch'};
let LOCALE = {}, FALLBACK = {}, uiLang = 'en';
const localeCache = {};
async function fetchLocale(lang) {
  if (localeCache[lang]) return localeCache[lang];
  try { const r = await fetch(`/ui/locales/${lang}.json`); localeCache[lang] = r.ok ? await r.json() : {}; } catch (e) { localeCache[lang] = {}; }
  return localeCache[lang];
}
function t(key, vars) {
  let v = LOCALE[key] ?? FALLBACK[key];
  if (v === undefined) return key;
  if (typeof v === 'object') v = v[new Intl.PluralRules(uiLang).select(Number(vars?.n ?? 0))] ?? v.other ?? '';
  return String(v).replace(/\{(\w+)\}/g, (_, k) => vars?.[k] ?? '');
}
// only the first text of an element is replaced, so icons and "?" hot spots inside it stay
function setFirstText(el, text) {
  for (const n of el.childNodes) {
    if (n.nodeType === 3 && n.nodeValue.trim()) { n.nodeValue = n.nodeValue.match(/^\s*/)[0] + text + n.nodeValue.match(/\s*$/)[0]; return; }
  }
  el.insertBefore(document.createTextNode(text), el.firstChild);
}
function applyI18n(root = document) {
  root.querySelectorAll('[data-i18n]').forEach(el => setFirstText(el, t(el.dataset.i18n)));
  root.querySelectorAll('[data-i18n-title]').forEach(el => { el.title = t(el.dataset.i18nTitle); });
  root.querySelectorAll('[data-i18n-placeholder]').forEach(el => { el.placeholder = t(el.dataset.i18nPlaceholder); });
}
// "auto" follows the browser's language; anything we have no file for falls back to English
async function loadI18n(pref) {
  const wanted = (!pref || pref === 'auto' ? navigator.language : pref).toLowerCase().slice(0, 2);
  uiLang = LANGUAGES[wanted] ? wanted : 'en';
  FALLBACK = await fetchLocale('en');
  LOCALE = uiLang === 'en' ? FALLBACK : await fetchLocale(uiLang);
  document.documentElement.lang = uiLang;
  applyI18n();
  if ($('ap-lang') && !$('ap-lang').dataset.filled) {
    $('ap-lang').dataset.filled = '1';
    for (const [code, name] of Object.entries(LANGUAGES)) $('ap-lang').insertAdjacentHTML('beforeend', `<option value="${code}">${name}</option>`);
  }
  if ($('ap-lang')) $('ap-lang').value = pref || 'auto';
  if (typeof refreshModels === 'function') { try { refreshModels(); } catch (e) {} }
}
// numbers, bytes and dates follow the language too
const nf = (n, d = 1) => Number(n).toLocaleString(uiLang, {maximumFractionDigits: d});
const dateFmt = d => new Date(d).toLocaleString(uiLang);
async function initI18n() { let pref = 'auto'; try { pref = (await api('/api/settings')).uiLanguage || 'auto'; } catch (e) {} await loadI18n(pref); }
