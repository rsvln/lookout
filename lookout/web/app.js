let refreshTimer = null;

// Every view has its own address: /log, /last, /stats, /stats/events (a gallery from the stats), /event/<id>,
// /config, /about, with the filters in the query string. So F5 shows the same view, links can be shared and
// the browser's Back / Forward move between views. "/" opens the view seen last.
const TABS = ['log', 'last', 'stats', 'search', 'config', 'about'];
const val = id => document.getElementById(id).value;

// Settings that are not part of a view (lines, refresh intervals) and the last address of every tab
// stay in this browser's localStorage.
const PREFS_KEY = 'lookout.prefs';
const PREFS = ['log-lines', 'refresh-interval', 'last-refresh'];

function readPrefs() {
  try { return JSON.parse(localStorage.getItem(PREFS_KEY)) || {}; } catch { return {}; }
}

function writePrefs(update) {
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(Object.assign(readPrefs(), update))); } catch { }
}

document.addEventListener('change', e => {
  if (PREFS.includes(e.target.id)) writePrefs({ fields: Object.fromEntries(PREFS.map(id => [id, val(id)])) });
});

// A select gets the value even if the option isn't there yet (e.g. a camera from a link), so the view matches the address.
function setSelect(id, value) {
  const el = document.getElementById(id);
  if (value && ![...el.options].some(o => o.value === value))
    el.insertAdjacentHTML('beforeend', `<option value="${esc(value)}">${esc(value)}</option>`);
  el.value = value;
}

function buildUrl(path, query) {
  const q = new URLSearchParams(Object.entries(query).filter(([, v]) => v !== undefined && v !== null && v !== '')).toString();
  return path + (q ? '?' + q : '');
}

function parseRoute() {
  const parts = location.pathname.split('/').filter(Boolean).map(decodeURIComponent);
  const q = Object.fromEntries(new URLSearchParams(location.search));
  if (parts[0] === 'event' && parts[1]) return { view: 'event', id: parts[1], q };
  if (parts[0] === 'stats' && parts[1] === 'events') return { view: 'stats', gallery: true, q };
  if (parts[0] === 'search') return { view: 'search', q };
  return { view: TABS.includes(parts[0]) ? parts[0] : 'log', q };
}

// Addresses built from the toolbar values; `o` overrides some of them. Defaults are left out.
function logUrl() {
  return buildUrl('/log', { type: val('filter-type'), camera: val('filter-camera'), q: val('filter-text') });
}

function lastUrl(o = {}) {
  const camera = o.camera ?? val('last-camera');
  const limit = o.limit ?? val('last-limit');
  return buildUrl('/last', { camera, label: o.label ?? val('last-label'), limit: limit === (camera ? '20' : '1') ? '' : limit });
}

// label: "config" (default) = what the bot sends, "all" = everything Frigate saw, otherwise one object.
// gallery: undefined = the numbers, {} = the events behind them, { hour } / { day } = one bar of a chart.
function statsUrl(o = {}, gallery) {
  const period = o.period ?? val('stat-period');
  const label = o.label ?? val('stat-label');
  return buildUrl(gallery ? '/stats/events' : '/stats', {
    period: period === '24h' ? '' : period,
    camera: o.camera ?? val('stat-camera'),
    label: label === 'config' ? '' : label === '' ? 'all' : label,
    hour: gallery?.hour, day: gallery?.day
  });
}

// history.state.n counts the views opened in this tab, so "Back" can tell whether there is one to return to.
let navIndex = history.state?.n ?? 0;

function navigate(url, replace) {
  if (url !== location.pathname + location.search) {
    if (replace) history.replaceState({ n: navIndex }, '', url);
    else history.pushState({ n: ++navIndex }, '', url);
  }
  return render();
}

window.addEventListener('popstate', e => { navIndex = e.state?.n ?? 0; render(); });

// "Back" buttons of the page: the previous view if this tab came from one, otherwise the parent view.
function goBack(parentUrl) {
  if (navIndex > 0) history.back();
  else navigate(parentUrl, true);
}

// Tabs and links inside the page change the view without reloading it; a tab opens where it was left,
// a click on the open tab goes to its start.
document.addEventListener('click', e => {
  const a = e.target.closest('a[href^="/"]');
  if (!a || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || a.target || a.hasAttribute('download')) return;
  const url = new URL(a.href);
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/js/') || url.pathname.startsWith('/css/') || url.pathname === '/login') return;
  e.preventDefault();
  const tab = a.dataset.tab;
  if (tab) navigate(parseRoute().view === tab ? '/' + tab : (readPrefs().urls || {})[tab] || '/' + tab);
  else navigate(url.pathname + url.search);
});

async function render() {
  const r = parseRoute();
  const url = location.pathname + location.search;
  const urls = readPrefs().urls || {};
  if (r.view !== 'event') urls[r.view] = url;
  writePrefs({ urls, last: url });

  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === r.view));
  document.querySelectorAll('.panel').forEach(p => p.classList.toggle('active', p.id === 'panel-' + r.view));
  document.title = 'Lookout · ' + (document.querySelector(`.tab[data-tab="${r.view}"]`)?.textContent || r.id || '');
  clearInterval(lastTimer);
  const q = r.q;

  if (r.view === 'log') {
    setSelect('filter-type', q.type || '');
    setSelect('filter-camera', q.camera || '');
    document.getElementById('filter-text').value = q.q || '';
    applyFilters();
  } else if (r.view === 'last') {
    await loadMeta();
    setSelect('last-camera', q.camera || '');
    setSelect('last-label', q.label || '');
    setSelect('last-limit', q.limit || (q.camera ? '20' : '1'));
    updateLastLimitLabel();
    loadLast();
    setLastRefresh();
  } else if (r.view === 'stats') {
    await loadMeta();
    setSelect('stat-period', q.period || '24h');
    setSelect('stat-camera', q.camera || '');
    setSelect('stat-label', q.label === undefined ? 'config' : q.label === 'all' ? '' : q.label);
    statGallery = r.gallery ? { hour: q.hour === undefined ? undefined : parseInt(q.hour), day: q.day } : false;
    loadStats();
  } else if (r.view === 'event') {
    loadEvent(r.id);
  } else if (r.view === 'search') {
    await loadMeta();
    setSelect('search-camera', q.camera || '');
    setSelect('search-label', q.label || '');
    document.getElementById('search-q').value = q.q || '';
    document.getElementById('search-from').value = q.from || '';
    document.getElementById('search-to').value = q.to || '';
    if (q.q || q.camera || q.label || q.from || q.to) loadSearch();
  } else if (r.view === 'config') {
    if (ME.role && ME.role !== 'admin') { navigate('/log', true); return; }
    loadConfig();
  } else if (r.view === 'about') {
    loadAbout();
  }
}

// One event: the snapshot at full width, its card with the video buttons below.
async function loadEvent(id) {
  const body = document.getElementById('event-body');
  body.innerHTML = '';
  const res = await fetch('/api/event/' + encodeURIComponent(id));
  if (!res.ok) { body.innerHTML = `<div class="empty">${esc(t(res.status === 404 ? 'web.no_events' : 'web.error_events'))}</div>`; return; }
  const r = await res.json();
  document.title = 'Lookout · ' + r.camera + ' · ' + r.label + ' · ' + r.start_local;
  body.innerHTML = `<div class="event-view">
      <img src="/api/snapshot/${encodeURIComponent(r.id)}" onclick="openLightbox(this.src)" onerror="this.remove()" alt="">
      ${eventCard(r)}
    </div>`;
}

const I18N = /*I18N*/{};
const t = (k, ...a) => (I18N[k] ?? k).replace(/\{(\d+)\}/g, (_, i) => a[i]);
const EMOJI = { person: '👤', car: '🚗', dog: '🐕', cat: '🐈', bird: '🐦' };
const labelName = l => (EMOJI[l] ? EMOJI[l] + ' ' : '') + (I18N['label.' + l] || l);

let metaLoaded = false;
async function loadMeta() {
  if (metaLoaded) return;
  const res = await fetch('/api/meta');
  if (!res.ok) return;
  const meta = await res.json();
  const fill = (cls, items, fmt) => document.querySelectorAll(cls).forEach(sel => {
    sel.innerHTML = '<option value="">{{web.all}}</option>' +
      items.map(v => `<option value="${esc(v)}">${esc(fmt(v))}</option>`).join('');
  });
  fill('.meta-camera', meta.cameras, v => v);
  fill('.meta-label', meta.labels, labelName);
  // Stats default to what the bot is configured to send.
  document.getElementById('stat-label').insertAdjacentHTML('afterbegin', '<option value="config">{{web.filter_config}}</option>');
  metaLoaded = true;
}

function ago(unix) {
  const s = Math.max(0, Date.now() / 1000 - unix);
  if (s < 60) return t('web.just_now');
  if (s < 3600) return t('web.min_ago', Math.floor(s / 60));
  if (s < 86400) return t('web.h_ago', Math.floor(s / 3600));
  return t('web.d_ago', Math.floor(s / 86400));
}

let lastTimer = null;
function setLastRefresh() {
  clearInterval(lastTimer);
  const ms = parseInt(document.getElementById('last-refresh').value);
  if (ms > 0) lastTimer = setInterval(loadLast, ms);
}

// One camera: show its history (20 by default); all cameras: N latest of each (1 by default).
function updateLastLimitLabel() {
  const cam = document.getElementById('last-camera').value;
  document.getElementById('last-limit-label').textContent = cam ? t('web.events_limit') : t('web.per_camera');
}

async function loadLast() {
  const p = new URLSearchParams();
  const cam = document.getElementById('last-camera').value;
  const lbl = document.getElementById('last-label').value;
  const lim = document.getElementById('last-limit').value;
  if (cam) p.set('camera', cam);
  if (lbl) p.set('label', lbl);
  p.set('limit', lim);
  const box = document.getElementById('last-cards');
  const res = await fetch('/api/last?' + p);
  if (!res.ok) { box.innerHTML = `<div class="empty">${esc(t('web.error_events'))}</div>`; return; }
  const rows = await res.json();
  if (!rows.length) { box.innerHTML = `<div class="empty">${esc(t('web.no_events'))}</div>`; return; }

  if (!cam && lim !== '1') {
    const groups = [];
    rows.forEach(r => {
      if (!groups.length || groups[groups.length - 1].camera !== r.camera) groups.push({ camera: r.camera, rows: [] });
      groups[groups.length - 1].rows.push(r);
    });
    box.innerHTML = groups.map(g => `<div class="section"><h3>📷 ${esc(g.camera)}</h3>
      <div class="cards">${g.rows.map(eventCard).join('')}</div></div>`).join('');
  } else {
    box.innerHTML = `<div class="cards">${rows.map(eventCard).join('')}</div>`;
  }
}

// Inline icons, drawn in the button's text color.
const ICON_PLAY = '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M7 4.5v15a1 1 0 0 0 1.5.86l12.5-7.5a1 1 0 0 0 0-1.72L8.5 3.64A1 1 0 0 0 7 4.5z"/></svg>';
const ICON_DOWNLOAD = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 3v12"/><path d="M7 10l5 5 5-5"/><path d="M4 15v5h16v-5"/></svg>';
const ICON_SHARE = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="18" cy="5" r="2.4"/><circle cx="6" cy="12" r="2.4"/><circle cx="18" cy="19" r="2.4"/><path d="M8.2 10.8l7.6-5.1M8.2 13.2l7.6 5.1"/></svg>';

function eventCard(r) {
  return `
    <div class="card">
      <div class="img"><img loading="lazy" src="/api/snapshot/${encodeURIComponent(r.id)}" onclick="openLightbox(this.src)" onerror="this.replaceWith(document.createTextNode(t('web.no_snapshot')))" alt=""></div>
      <div class="meta">
        <div class="row1"><span class="lbl">${esc(labelName(r.label))}${r.sub_label ? ` <span class="sub">(${esc(r.sub_label)})</span>` : ''}</span>
          <span class="score">${Math.round(r.score * 100)}%</span></div>
        <div class="row1"><span class="cam">${esc(r.camera)}${r.end_time === null ? ` <span class="live">● ${esc(t('web.in_progress'))}</span>` : ''}</span>
          <span class="when" title="${esc(ago(r.start_time))}"><a href="/event/${encodeURIComponent(r.id)}">${esc(r.start_local)}</a></span></div>
        ${r.zones && r.zones.length ? `<div class="zones">${esc(r.zones.join(', '))}</div>` : ''}
        ${r.faces ? `<div class="zones">👤 ${esc(r.faces)}</div>` : ''}
        ${r.ai_text ? `<div class="zones">${esc(r.ai_text)}</div>` : ''}
        <div class="actions">
          <button class="act" data-id="${esc(r.id)}" onclick="openVideo(this.dataset.id)">${ICON_PLAY} ${esc(t('web.video'))}</button>
          <a class="act" href="/api/clip/${encodeURIComponent(r.id)}?download=1" title="${esc(t('web.download'))}">${ICON_DOWNLOAD}</a>
          <button class="act" data-id="${esc(r.id)}" onclick="shareClip(this.dataset.id)" title="${esc(t('web.share_clip'))}">${ICON_SHARE}</button>
        </div>
      </div>
    </div>`;
}

let aboutLoaded = false;
async function loadAbout() {
  if (aboutLoaded) return;
  const res = await fetch('/api/about');
  if (!res.ok) return;
  const data = await res.json();
  document.getElementById('about-readme').innerHTML = data.readme;
  document.querySelectorAll('#about-readme a[href^="http"]').forEach(a => { a.target = '_blank'; a.rel = 'noopener'; });
  aboutLoaded = true;
  // Syntax highlighting for code blocks, with the Config editor's colors; plain text if the bundle is unavailable.
  try {
    const { highlightCodeBlocks } = await import('/js/yaml-editor.js?v=%VERSION%');
    highlightCodeBlocks(document.getElementById('about-readme'));
  } catch (e) {
    console.warn('Code highlighting is not available', e);
  }
}

function openLightbox(src) {
  const img = document.getElementById('lightbox-img');
  img.src = src;
  img.style.display = '';
  document.getElementById('lightbox-video').style.display = 'none';
  document.getElementById('lightbox').classList.add('show');
}

function openVideo(id) {
  const video = document.getElementById('lightbox-video');
  document.getElementById('lightbox-img').style.display = 'none';
  video.style.display = '';
  video.onerror = () => { closeLightbox(); showToast(t('web.video_error'), 'err'); };
  video.src = '/api/clip/' + encodeURIComponent(id);
  document.getElementById('lightbox').classList.add('show');
  video.play().catch(() => {});
}

// Closes on a click outside the video, so its controls stay usable.
function closeLightbox(e) {
  if (e && e.target.id === 'lightbox-video') return;
  const video = document.getElementById('lightbox-video');
  video.onerror = null;
  video.pause();
  video.removeAttribute('src');
  video.load();
  document.getElementById('lightbox').classList.remove('show');
}

// onClick(i) is the name of a function called with the bar's index; bars with a value become clickable.
function heatmapHtml(h) {
  const days = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'].map(d => t('web.weekday.' + d));
  const max = Math.max(1, ...h.flat());
  const head = `<div></div>` + [...Array(24)].map((_, i) => `<div class="heat-h">${i}</div>`).join('');
  const rows = h.map((hrs, d) => `<div class="heat-lab">${esc(days[d])}</div>` + hrs.map((v, hour) =>
    `<div class="heat-cell${v ? ' clickable' : ''}" style="background:rgba(88,166,255,${v ? (0.12 + 0.88 * v / max).toFixed(2) : 0.04})" title="${esc(days[d])} ${String(hour).padStart(2, '0')}:00 — ${v}"${v ? ` onclick="statHourClick(${hour})"` : ''}></div>`
  ).join('')).join('');
  return `<div class="section"><h3>${t('web.heatmap')}</h3><div class="heatmap">${head}${rows}</div></div>`;
}

function searchUrl() {
  return buildUrl('/search', { q: val('search-q'), camera: val('search-camera'), label: val('search-label'), from: val('search-from'), to: val('search-to') });
}

async function loadSearch() {
  const p = new URLSearchParams();
  ['q', 'camera', 'label', 'from', 'to'].forEach(k => { const v = val('search-' + (k === 'q' ? 'q' : k)); if (v) p.set(k === 'q' ? 'q' : k, v); });
  const body = document.getElementById('search-body');
  const res = await fetch('/api/search?' + p);
  if (!res.ok) { body.innerHTML = `<div class="empty">${esc(t('web.error_events'))}</div>`; return; }
  const data = await res.json();
  const more = data.total > data.events.length ? ` <span style="color:var(--muted)">${esc(t('web.gallery.shown', data.events.length, data.total))}</span>` : '';
  body.innerHTML = data.events.length
    ? `<div class="section"><h3>${esc(t('web.gallery.count', data.total))}${more}</h3><div class="cards">${data.events.map(eventCard).join('')}</div></div>`
    : `<div class="empty">${esc(t('web.search.empty'))}</div>`;
}

async function shareClip(id) {
  try {
    const res = await fetch('/api/sign/clip/' + encodeURIComponent(id));
    const data = await res.json();
    if (!res.ok || !data.url) throw new Error();
    await navigator.clipboard.writeText(data.url);
    showToast(t('web.share_copied'), 'ok');
  } catch { showToast(t('web.share_failed'), 'err'); }
}

function barChart(values, labels, peakIdx, onClick) {
  const max = Math.max(1, ...values);
  return `<div class="bars">${values.map((v, i) => `
      <div class="bar${onClick && v ? ' clickable' : ''}" title="${esc(labels[i])}: ${v}${onClick && v ? ' — ' + esc(t('web.gallery.open')) : ''}"${onClick && v ? ` onclick="${onClick}(${i})"` : ''}>
        <span class="n">${v || ''}</span>
        <div class="fill${i === peakIdx ? ' peak' : ''}" style="height:${v ? Math.max(2, v / max * 100) : 0}%"></div>
      </div>`).join('')}</div>
    <div class="bar-axis">${labels.map(l => `<span>${esc(l)}</span>`).join('')}</div>`;
}

function filterStats(camera, label) {
  navigate(statsUrl({ camera, label }));
}

// The gallery of the events behind the current Stats view, optionally narrowed to one hour of day
// ({ hour }) or one day ({ day }); false = the numbers. "Back" returns to the numbers.
let statGallery = false;
let statDays = [];

// A matrix cell first narrows the stats to its camera and object; clicking it again opens its events.
function statCellClick(camera, label) {
  const f = statFilter();
  if (f[1] === camera && f[2] === label) openStatGallery();
  else filterStats(camera, label);
}

function openStatGallery(extra) {
  navigate(statsUrl({}, extra || {}));
}

const statHourClick = h => openStatGallery({ hour: h });
const statDayClick = i => openStatGallery({ day: statDays[i] });
const hourRange = h => String(h).padStart(2, '0') + ':00–' + String((h + 1) % 24).padStart(2, '0') + ':00';

async function loadStatGallery() {
  const [period, cam, lbl] = statFilter();
  const p = new URLSearchParams({ period });
  if (cam) p.set('camera', cam);
  if (lbl) p.set('label', lbl);
  if (statGallery.hour !== undefined) p.set('hour', statGallery.hour);
  if (statGallery.day) p.set('day', statGallery.day);
  const body = document.getElementById('stats-body');
  const res = await fetch('/api/events?' + p);
  if (!res.ok) { body.innerHTML = `<div class="empty">${esc(t('web.error_events'))}</div>`; return; }
  const data = await res.json();
  const sel = document.getElementById('stat-period');
  const what = [cam ? '📷 ' + cam : t('web.all_cameras'),
                lbl === 'config' ? t('web.filter_config') : lbl ? labelName(lbl) : t('web.all_objects'),
                sel.options[sel.selectedIndex].text,
                ...(statGallery.hour !== undefined ? [hourRange(statGallery.hour)] : []),
                ...(statGallery.day ? [statGallery.day.slice(8) + '.' + statGallery.day.slice(5, 7)] : [])].join(' · ');
  const more = data.total > data.events.length ? ` <span style="color:var(--muted)">${esc(t('web.gallery.shown', data.events.length, data.total))}</span>` : '';
  body.innerHTML = `<div class="section"><h3>${esc(what)} — ${esc(t('web.gallery.count', data.total))}${more}</h3>
    ${data.events.length ? `<div class="cards">${data.events.map(eventCard).join('')}</div>` : `<div class="empty">${esc(t('web.no_events_period'))}</div>`}</div>`;
}

function statFilter() {
  return ['stat-period', 'stat-camera', 'stat-label'].map(id => document.getElementById(id).value);
}

function statOverview(filter) {
  return [filter[0], '', 'config'];
}

function isStatOverview(filter) {
  return filter.join('|') === statOverview(filter).join('|');
}

// The previous view, or with none in this tab: from a gallery to its numbers, from a filtered view to the overview.
function statBack() {
  goBack(statGallery ? statsUrl() : statsUrl({ camera: '', label: 'config' }));
}

async function loadStats() {
  const current = statFilter();
  document.getElementById('stat-back').style.display = statGallery || navIndex > 0 || !isStatOverview(current) ? '' : 'none';
  if (statGallery) return loadStatGallery();
  const p = new URLSearchParams({ period: document.getElementById('stat-period').value });
  const cam = document.getElementById('stat-camera').value;
  const lbl = document.getElementById('stat-label').value;
  if (cam) p.set('camera', cam);
  if (lbl) p.set('label', lbl);
  const body = document.getElementById('stats-body');
  const res = await fetch('/api/stat?' + p);
  if (!res.ok) { body.innerHTML = `<div class="empty">${esc(t('web.error_stats'))}</div>`; return; }
  const st = await res.json();

  const topCam = st.cameras[0];
  let html = `<div class="kpis">
    <div class="kpi"><div class="k">${t('web.kpi.events')}</div><div class="v">${st.total}</div></div>
    <div class="kpi"><div class="k">${t('web.kpi.alerts')}</div><div class="v" style="color:var(--red)">${st.alerts}</div></div>
    <div class="kpi"><div class="k">${t('web.kpi.detections')}</div><div class="v" style="color:var(--yellow)">${st.detections}</div></div>
    <div class="kpi"><div class="k">${t('web.kpi.busiest')}</div><div class="v small">${topCam ? esc(topCam) + ' · ' + Object.values(st.matrix[topCam]).reduce((a, b) => a + b, 0) : '—'}</div></div>
    <div class="kpi"><div class="k">${t('web.kpi.peak')}</div><div class="v small">${st.peakHour >= 0 ? String(st.peakHour).padStart(2, '0') + ':00–' + String((st.peakHour + 1) % 24).padStart(2, '0') + ':00' : '—'}</div></div>
  </div>`;

  if (!st.total) { body.innerHTML = html + `<div class="empty">${esc(t('web.no_events_period'))}</div>`; return; }

  const max = Math.max(...st.cameras.flatMap(c => Object.values(st.matrix[c])));
  const cell = (v, c, l) => v
    ? `<td class="clickable" data-c="${esc(c)}" data-l="${esc(l)}" onclick="statCellClick(this.dataset.c, this.dataset.l)" title="${esc(t('web.matrix.cell_hint'))}" style="background:rgba(88,166,255,${(0.08 + 0.5 * v / max).toFixed(2)})">${v}</td>`
    : `<td class="zero">·</td>`;
  html += `<div class="section"><h3>${t('web.matrix.title')}</h3><div style="overflow-x:auto"><table class="matrix">
    <tr><th>${t('web.matrix.camera')}</th>${st.labels.map(l => `<th>${esc(labelName(l))}</th>`).join('')}<th>${t('web.matrix.total')}</th><th>${t('web.matrix.last_event')}</th></tr>
    ${st.cameras.map(c => {
      const row = st.matrix[c];
      const sum = Object.values(row).reduce((a, b) => a + b, 0);
      const last = st.lastByCamera[c];
      return `<tr><td class="clickable" data-c="${esc(c)}" onclick="filterStats(this.dataset.c)">${esc(c)}</td>${st.labels.map(l => cell(row[l] || 0, c, l)).join('')}
        <td class="clickable" data-c="${esc(c)}" onclick="statCellClick(this.dataset.c, statFilter()[2])" title="${esc(t('web.matrix.cell_hint'))}">${sum}</td><td style="color:var(--muted)">${last ? ago(last) : ''}</td></tr>`;
    }).join('')}
    <tr class="total"><td>${t('web.matrix.total')}</td>${st.labels.map(l => `<td>${st.labelTotals[l]}</td>`).join('')}<td class="clickable" onclick="openStatGallery()" title="${esc(t('web.gallery.open'))}">${st.total}</td><td></td></tr>
  </table></div></div>`;

  html += `<div class="section"><h3>${t('web.by_hour')}</h3>${barChart(st.hours, st.hours.map((_, i) => String(i)), st.peakHour, 'statHourClick')}</div>`;
  statDays = st.days.map(d => d.day);
  if (st.days.length > 2)
    html += `<div class="section"><h3>${t('web.by_day')}</h3>${barChart(st.days.map(d => d.count), st.days.map(d => d.day.slice(8) + '.' + d.day.slice(5, 7)), -1, 'statDayClick')}</div>`;
  if (st.heatmap) html += heatmapHtml(st.heatmap);

  body.innerHTML = html;
  const exp = new URLSearchParams({ period: document.getElementById('stat-period').value, format: 'csv' });
  if (cam) exp.set('camera', cam);
  if (lbl) exp.set('label', lbl);
  document.getElementById('stat-csv').href = '/api/stat?' + exp;
  exp.set('format', 'json');
  document.getElementById('stat-json').href = '/api/stat?' + exp;
}

function setRefresh() {
  clearInterval(refreshTimer);
  const ms = parseInt(document.getElementById('refresh-interval').value);
  if (ms > 0) refreshTimer = setInterval(loadLog, ms);
}

let allLines = [];
let logShown = false;

async function loadLog() {
  const n = document.getElementById('log-lines').value;
  const res = await fetch('/api/log?lines=' + n);
  const data = await res.json();
  allLines = data.lines;
  fillLogCameras();
  applyFilters(true);
}

// Cameras met in the loaded log lines. The camera column also holds Telegram chat ids (numbers), those are skipped.
// The selected camera (also one from the address) stays in the list even if its lines scrolled out.
function fillLogCameras() {
  const sel = document.getElementById('filter-camera');
  const current = sel.value;
  const cams = new Set();
  allLines.forEach(l => { const c = l.split('\t')[3]; if (c && !/^-?\d+$/.test(c)) cams.add(c); });
  if (current) cams.add(current);
  const list = [...cams].sort();
  if (sel.dataset.key !== list.join('|')) {
    sel.innerHTML = '<option value="">{{web.all}}</option>' + list.map(c => `<option value="${esc(c)}">${esc(c)}</option>`).join('');
    sel.dataset.key = list.join('|');
  }
  sel.value = current;
}

function applyFilters(keepPosition) {
  const type = document.getElementById('filter-type').value.toLowerCase();
  const camera = document.getElementById('filter-camera').value;
  const text = document.getElementById('filter-text').value.toLowerCase();

  const filtered = allLines.filter(line => {
    const parts = line.split('\t');
    if (parts.length < 5) return !type && !camera && !text;
    const [ts, t, id, cam, ...msgParts] = parts;
    const msg = msgParts.join('\t');
    if (type && !t.toLowerCase().includes(type)) return false;
    if (camera && cam !== camera) return false;
    if (text && !msg.toLowerCase().includes(text) && !id.toLowerCase().includes(text)) return false;
    return true;
  });

  const container = document.getElementById('log-container');
  // Newest first, so fresh lines are at the top without scrolling. The list opens at the top (the browser
  // would restore the old scroll position after F5); while reading further down, new lines added above
  // by auto-refresh don't move the text under the eye; a filter change starts from the top again.
  const prevTop = container.scrollTop, prevHeight = container.scrollHeight;
  container.innerHTML = filtered.slice().reverse().map(formatLine).join('');
  container.scrollTop = !keepPosition || !logShown || prevTop <= 5 ? 0 : prevTop + container.scrollHeight - prevHeight;
  logShown = true;
}

function clearFilters() {
  navigate('/log');
}

const EVENT_COLORS = [
  '#1a3a2a', '#2a1a3a', '#3a2a1a', '#1a2a3a', '#3a1a2a',
  '#1a3a3a', '#3a1a1a', '#2a3a1a', '#1a1a3a', '#3a3a1a',
  '#0d2a1a', '#2a0d1a', '#1a2a0d', '#0d1a2a', '#2a1a0d',
  '#0d2a2a', '#2a0d0d', '#1a0d2a', '#0d0d2a', '#2a2a0d',
  '#153020', '#201530', '#302015', '#152030', '#301520',
  '#153030', '#301515', '#203015', '#151530', '#303015',
];

function idToColor(id) {
  if (!id) return 'transparent';
  let hash = 0;
  for (let i = 0; i < id.length; i++) {
    hash = ((hash << 5) - hash) + id.charCodeAt(i);
    hash |= 0;
  }
  return EVENT_COLORS[Math.abs(hash) % EVENT_COLORS.length];
}

function formatLine(line) {
  const parts = line.split('\t');
  if (parts.length < 5) return `<div class="log-line"><span class="log-msg">${esc(line)}</span></div>`;
  const [ts, type, id, camera, ...msgParts] = parts;
  const msg = msgParts.join('\t');
  const isError = msg.toLowerCase().includes('error');
  const bg = idToColor(id);
  return `<div class="log-line" style="background:${bg}; margin:0 -16px; padding:1px 16px;">
    <span class="log-ts">${esc(ts)}</span>
    <span class="log-type ${esc(type)}">${esc(type)}</span>
    <span class="log-id" title="${esc(id)}">${esc(id)}</span>
    <span class="log-camera">${esc(camera)}</span>
    <span class="log-msg${isError ? ' error' : ''}">${esc(msg)}</span>
  </div>`;
}

function esc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// Config editor: CodeMirror from /js/yaml-editor.js, or the plain text area if it cannot be loaded.
let configEditor = null;
function getConfigEditor() {
  return configEditor ??= (async () => {
    const area = document.getElementById('config-editor');
    try {
      const { createYamlEditor } = await import('/js/yaml-editor.js?v=%VERSION%');
      const host = document.getElementById('config-editor-host');
      const editor = createYamlEditor(host, area.value);
      area.style.display = 'none';
      host.style.display = '';
      return editor;
    } catch (e) {
      console.warn('YAML editor is not available, using the plain text area', e);
      return { getValue: () => area.value, setValue: v => { area.value = v; }, focus: () => area.focus() };
    }
  })();
}

let configMode = 'form';

function setConfigMode(mode) {
  configMode = mode;
  document.getElementById('config-form').style.display = mode === 'form' ? 'flex' : 'none';
  document.getElementById('config-yaml').style.display = mode === 'yaml' ? 'flex' : 'none';
  document.getElementById('config-mode-form').classList.toggle('primary', mode === 'form');
  document.getElementById('config-mode-yaml').classList.toggle('primary', mode === 'yaml');
  if (mode === 'yaml') loadConfigYaml();
  else loadConfigForm();
}

async function loadConfig() { setConfigMode(configMode); }

async function loadConfigYaml() {
  const res = await fetch('/api/config');
  if (!res.ok) return;
  const data = await res.json();
  (await getConfigEditor()).setValue(data.content);
}

function fieldInput(f) {
  if (f.type === 'checkbox')
    return `<label class="chk"><input type="checkbox" data-path="${esc(f.path)}"${f.value === 'true' ? ' checked' : ''}> ${esc(f.label)}</label>`;
  if (f.type === 'select')
    return `<label>${esc(f.label)}<select data-path="${esc(f.path)}">${(f.options || []).map(o => `<option value="${esc(o)}"${o === f.value ? ' selected' : ''}>${esc(optionLabel(f, o))}</option>`).join('')}</select></label>`;
  if (f.type === 'textarea')
    return `<label>${esc(f.label)}<textarea data-path="${esc(f.path)}" rows="3">${esc(f.value)}</textarea></label>`;
  const type = f.type === 'number' ? 'number' : 'text';
  return `<label>${esc(f.label)}<input type="${type}" data-path="${esc(f.path)}" value="${esc(f.value)}"></label>`;
}

function optionLabel(f, o) {
  if (f.path && /notifiers\[\d+\]\.type$/.test(f.path) && I18N['web.config.notifier.' + o])
    return t('web.config.notifier.' + o);
  return o;
}

let formTree = [];
let cameraTemplate = [];
let notifierTemplates = {};
let originalCameras = new Set();
let removedCameras = [];
let removedNotifiers = [];

const CAMERA_NAME_RE = /^[A-Za-z0-9][A-Za-z0-9_.-]*$/;
const NOTIFIER_TYPES = ['ntfy', 'discord', 'matrix', 'webhook', 'telegram'];

function settingsTree(data) {
  return (data.groups || []).map(g => {
    if (g.id === 'frigate')
      return {
        id: g.id, title: g.title, fields: g.fields,
        children: (data.cameras || []).map(c => ({ id: 'cam:' + c.camera, title: c.camera, fields: c.fields }))
      };
    if (g.id === 'notifiers')
      return {
        id: g.id, title: g.title, fields: [],
        children: (data.notifiers || []).map(n => ({
          id: 'n:' + n.index,
          title: t('web.config.notifier.' + n.type),
          ntype: n.type,
          originalIndex: n.index,
          fields: n.fields
        }))
      };
    if (g.id === 'options') {
      const quiet = (g.fields || []).filter(f => f.path.startsWith('options.quiet.'));
      const rest = (g.fields || []).filter(f => !f.path.startsWith('options.quiet.'));
      return {
        id: g.id, title: g.title, fields: rest,
        children: quiet.length ? [{ id: 'quiet', title: t('web.config.group.quiet'), fields: quiet }] : []
      };
    }
    return { id: g.id, title: g.title, fields: g.fields || [] };
  });
}

function treeHas(nodes, id) {
  return nodes.some(n => n.id === id || (n.children && treeHas(n.children, id)));
}

function frigateNode() { return formTree.find(n => n.id === 'frigate'); }
function notifiersNode() { return formTree.find(n => n.id === 'notifiers'); }

function renderTreeNav(nodes, selected, depth) {
  return nodes.map(n => {
    const kids = n.children && n.children.length;
    const add = n.id === 'frigate'
      ? `<button type="button" class="tree-item tree-add" style="--d:${depth + 1}" data-add-camera="1">${esc(t('web.config.add_camera'))}</button>`
      : n.id === 'notifiers'
      ? `<button type="button" class="tree-item tree-add" style="--d:${depth + 1}" data-add-notifier="1">${esc(t('web.config.add_notifier'))}</button>`
      : '';
    return `<div class="tree-branch" style="--d:${depth}">
      <button type="button" class="tree-item${selected === n.id ? ' on' : ''}" data-node="${esc(n.id)}">${esc(n.title)}</button>
      ${kids ? renderTreeNav(n.children, selected, depth + 1) : ''}
      ${add}
    </div>`;
  }).join('');
}

function renderTreePanes(nodes) {
  let html = '';
  (function walk(list) {
    for (const n of list) {
      const cam = n.id.startsWith('cam:') ? n.id.slice(4) : '';
      const ni = n.id.startsWith('n:') ? n.id.slice(2) : '';
      const del = cam
        ? `<button type="button" class="btn tree-del" data-del-camera="${esc(cam)}">${esc(t('web.config.remove_camera'))}</button>`
        : ni !== ''
        ? `<button type="button" class="btn tree-del" data-del-notifier="${esc(ni)}">${esc(t('web.config.remove_notifier'))}</button>`
        : '';
      const hint = n.id === 'notifiers'
        ? `<p class="tree-hint">${esc(t('web.config.notifiers_hint'))}</p>`
        : '';
      const grid = cam ? 'settings-grid cams' : 'settings-grid';
      html += `<div class="tree-pane" data-pane="${esc(n.id)}"><div class="tree-pane-head"><h3>${esc(n.title)}</h3>${del}</div>${hint}`
        + (n.fields && n.fields.length
          ? `<div class="${grid}">${n.fields.map(fieldInput).join('')}</div>`
          : '')
        + `</div>`;
      if (n.children) walk(n.children);
    }
  })(nodes);
  return html;
}

function captureFields() {
  document.querySelectorAll('#config-form [data-path]').forEach(el => {
    const v = el.type === 'checkbox' ? (el.checked ? 'true' : 'false') : el.value;
    (function walk(list) {
      for (const n of list) {
        const f = (n.fields || []).find(x => x.path === el.dataset.path);
        if (f) f.value = v;
        if (n.children) walk(n.children);
      }
    })(formTree);
  });
}

function renderSettings(selected) {
  const host = document.getElementById('config-form');
  selected = selected || readPrefs().configNode || 'frigate';
  if (!treeHas(formTree, selected)) selected = formTree[0] ? formTree[0].id : 'frigate';
  host.innerHTML = `<nav class="tree-nav">${renderTreeNav(formTree, selected, 0)}</nav><div class="tree-main">${renderTreePanes(formTree)}</div>`;
  host.querySelectorAll('.tree-item[data-node]').forEach(btn => btn.onclick = () => showConfigNode(btn.dataset.node));
  host.querySelector('[data-add-camera]')?.addEventListener('click', startAddCamera);
  host.querySelector('[data-add-notifier]')?.addEventListener('click', startAddNotifier);
  host.querySelectorAll('[data-del-camera]').forEach(btn => btn.onclick = () => removeCamera(btn.dataset.delCamera));
  host.querySelectorAll('[data-del-notifier]').forEach(btn => btn.onclick = () => removeNotifier(btn.dataset.delNotifier));
  host.querySelectorAll('select[data-path]').forEach(sel => {
    if (!/^notifiers\[\d+\]\.type$/.test(sel.dataset.path)) return;
    sel.onchange = () => changeNotifierType(sel.dataset.path, sel.value);
  });
  showConfigNode(selected);
}

function showConfigNode(id) {
  const host = document.getElementById('config-form');
  host.querySelectorAll('.tree-item[data-node]').forEach(el => el.classList.toggle('on', el.dataset.node === id));
  host.querySelectorAll('.tree-pane').forEach(el => el.classList.toggle('on', el.dataset.pane === id));
  writePrefs({ configNode: id });
}

function startAddCamera(ev) {
  const btn = ev.currentTarget;
  if (document.querySelector('.tree-add-input')) return;
  const wrap = document.createElement('div');
  wrap.className = 'tree-add-form';
  wrap.style.setProperty('--d', getComputedStyle(btn).getPropertyValue('--d') || '1');
  wrap.innerHTML = `<input type="text" class="tree-add-input" placeholder="${esc(t('web.config.camera_name'))}" maxlength="64" spellcheck="false">`;
  btn.replaceWith(wrap);
  const input = wrap.querySelector('input');
  input.focus();
  let done = false;
  const finish = commit => {
    if (done) return;
    done = true;
    const name = input.value.trim();
    captureFields();
    renderSettings(readPrefs().configNode);
    if (commit) commitAddCamera(name);
  };
  input.onkeydown = e => {
    if (e.key === 'Enter') { e.preventDefault(); finish(true); }
    if (e.key === 'Escape') { e.preventDefault(); finish(false); }
  };
  input.onblur = () => finish(!!input.value.trim());
}

function commitAddCamera(name) {
  if (!name) return;
  if (!CAMERA_NAME_RE.test(name)) { showToast(t('web.config.camera_invalid'), 'err'); return; }
  const fr = frigateNode();
  if (!fr) return;
  fr.children = fr.children || [];
  if (fr.children.some(c => c.id === 'cam:' + name)) { showToast(t('web.config.camera_exists', name), 'err'); return; }
  removedCameras = removedCameras.filter(x => x !== name);
  fr.children.push({
    id: 'cam:' + name,
    title: name,
    fields: (cameraTemplate || []).map(f => Object.assign({}, f, { path: String(f.path).split('{camera}').join(name) }))
  });
  renderSettings('cam:' + name);
}

function removeCamera(name) {
  if (!name || !confirm(t('web.config.remove_camera_confirm', name))) return;
  const fr = frigateNode();
  if (!fr?.children) return;
  captureFields();
  fr.children = fr.children.filter(c => c.id !== 'cam:' + name);
  if (originalCameras.has(name) && !removedCameras.includes(name)) removedCameras.push(name);
  renderSettings('frigate');
}

function notifierFieldsFromTemplate(type, index) {
  const tmpl = notifierTemplates[type] || notifierTemplates.ntfy || [];
  return tmpl.map(f => Object.assign({}, f, {
    path: String(f.path).replace(/notifiers\[\d+\]/, 'notifiers[' + index + ']'),
    value: String(f.path).endsWith('.type') ? type : (f.value || '')
  }));
}

function reindexNotifiers() {
  const n = notifiersNode();
  if (!n?.children) return;
  n.children.forEach((c, i) => {
    c.id = 'n:' + i;
    c.fields = (c.fields || []).map(f => Object.assign({}, f, {
      path: String(f.path).replace(/notifiers\[\d+\]/, 'notifiers[' + i + ']')
    }));
  });
}

function startAddNotifier(ev) {
  const btn = ev.currentTarget;
  if (document.querySelector('.tree-add-input')) return;
  const wrap = document.createElement('div');
  wrap.className = 'tree-add-form';
  wrap.style.setProperty('--d', getComputedStyle(btn).getPropertyValue('--d') || '1');
  wrap.innerHTML = `<select class="tree-add-input">${NOTIFIER_TYPES.map(x => `<option value="${x}">${esc(t('web.config.notifier.' + x))}</option>`).join('')}</select>`;
  btn.replaceWith(wrap);
  const sel = wrap.querySelector('select');
  sel.focus();
  let done = false;
  const finish = commit => {
    if (done) return;
    done = true;
    const type = sel.value;
    captureFields();
    renderSettings(readPrefs().configNode);
    if (commit) commitAddNotifier(type);
  };
  sel.onchange = () => finish(true);
  sel.onkeydown = e => {
    if (e.key === 'Enter') { e.preventDefault(); finish(true); }
    if (e.key === 'Escape') { e.preventDefault(); finish(false); }
  };
  sel.onblur = () => setTimeout(() => finish(false), 150);
}

function commitAddNotifier(type) {
  type = NOTIFIER_TYPES.includes(type) ? type : 'ntfy';
  const n = notifiersNode();
  if (!n) return;
  n.children = n.children || [];
  const i = n.children.length;
  n.children.push({
    id: 'n:' + i,
    title: t('web.config.notifier.' + type),
    ntype: type,
    originalIndex: null,
    fields: notifierFieldsFromTemplate(type, i)
  });
  renderSettings('n:' + i);
}

function removeNotifier(index) {
  index = +index;
  const n = notifiersNode();
  if (!n?.children || index < 0 || index >= n.children.length) return;
  const child = n.children[index];
  if (!confirm(t('web.config.remove_notifier_confirm', child.title))) return;
  captureFields();
  if (child.originalIndex != null && !removedNotifiers.includes(child.originalIndex))
    removedNotifiers.push(child.originalIndex);
  n.children.splice(index, 1);
  reindexNotifiers();
  renderSettings('notifiers');
}

function changeNotifierType(path, type) {
  const m = /^notifiers\[(\d+)\]\.type$/.exec(path);
  if (!m) return;
  const i = +m[1];
  const child = notifiersNode()?.children?.[i];
  if (!child) return;
  type = NOTIFIER_TYPES.includes(type) ? type : 'ntfy';
  captureFields();
  child.ntype = type;
  child.title = t('web.config.notifier.' + type);
  child.fields = notifierFieldsFromTemplate(type, i);
  renderSettings('n:' + i);
}

async function loadConfigForm() {
  const host = document.getElementById('config-form');
  const res = await fetch('/api/settings');
  if (!res.ok) { host.innerHTML = `<div class="empty">${esc(t('web.config.invalid', res.status))}</div>`; return; }
  const data = await res.json();
  cameraTemplate = data.cameraTemplate || [];
  notifierTemplates = data.notifierTemplates || {};
  originalCameras = new Set((data.cameras || []).map(c => c.camera));
  removedCameras = [];
  removedNotifiers = [];
  formTree = settingsTree(data);
  renderSettings();
}

function collectFields() {
  const fields = {};
  document.querySelectorAll('#config-form [data-path]').forEach(el => {
    fields[el.dataset.path] = el.type === 'checkbox' ? (el.checked ? 'true' : 'false') : el.value;
  });
  return fields;
}

function setBusy(text) {
  document.getElementById('busy-text').textContent = text || '';
  document.getElementById('busy').classList.toggle('show', !!text);
}

// Sends a config request; returns true on success, shows the server's error otherwise.
async function configRequest(url, body) {
  try {
    const res = await fetch(url, { method: 'POST', headers: {'Content-Type': 'application/json'}, body: body ? JSON.stringify(body) : null });
    const data = await res.json().catch(() => ({}));
    if (res.ok && data.ok) return true;
    showToast(t('web.config.invalid', data.error || res.status), 'err');
  } catch (e) {
    showToast(t('web.config.invalid', e.message), 'err');
  }
  return false;
}

// Language the page was rendered in; its texts come from the server, so another one needs a reload.
const PAGE_LOCALE = '%WEBLOCALE%';

// After applying, waits until the service is back on MQTT (up to 20 s). If the web UI language changed,
// reloads the page and shows the result there.
async function waitForService() {
  let st = {};
  for (let i = 0; i < 20 && !st.mqtt; i++) {
    if (i > 0) await new Promise(r => setTimeout(r, 1000));
    try {
      const res = await fetch('/api/status');
      if (res.ok) st = await res.json();
    } catch { }
  }
  const result = st.mqtt ? 'applied' : 'applied_nomqtt';
  if (st.locale && st.locale !== PAGE_LOCALE) {
    try { sessionStorage.setItem('lookout.toast', result); } catch { }
    location.reload();
    return;
  }
  showToast(t('web.config.' + result), st.mqtt ? 'ok' : 'err');
}

async function saveConfig(apply) {
  setBusy(apply ? t('web.config.applying') : t('web.config.saving'));
  try {
    const ok = configMode === 'form'
      ? await configRequest('/api/settings', { fields: collectFields(), apply, removeCameras, removeNotifiers })
      : await configRequest('/api/config', { content: (await getConfigEditor()).getValue(), apply });
    if (!ok) return;
    if (configMode === 'form') {
      const fr = frigateNode();
      originalCameras = new Set((fr?.children || []).map(c => c.title));
      removedCameras = [];
      const nn = notifiersNode();
      (nn?.children || []).forEach((c, i) => { c.originalIndex = i; });
      removedNotifiers = [];
    }
    if (apply) { setBusy(t('web.config.waiting')); await waitForService(); }
    else showToast(t('web.config.saved'), 'ok');
  } finally {
    setBusy(null);
  }
}

async function applyConfig() {
  setBusy(t('web.config.applying'));
  try {
    if (!await configRequest('/api/apply')) return;
    setBusy(t('web.config.waiting'));
    await waitForService();
  } finally {
    setBusy(null);
  }
}

function showToast(msg, type) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = 'toast ' + type + ' show';
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => el.classList.remove('show'), type === 'err' ? 6000 : 3000);
}


let ME = { role: 'admin', auth: 'none', user: null };

async function loadWhoami() {
  try {
    const res = await fetch('/api/whoami');
    if (res.ok) ME = await res.json();
  } catch { }
  const cfg = document.getElementById('tab-config');
  if (cfg) cfg.style.display = ME.role === 'admin' || !ME.role || ME.role === 'none' ? '' : 'none';
  const box = document.getElementById('userbox');
  if (box && ME.auth === 'form' && ME.user) {
    box.hidden = false;
    document.getElementById('user-name').textContent = ME.user;
  }
}

async function logout() {
  await fetch('/api/logout', { method: 'POST' });
  location.href = '/login';
}

// Start: settings from this browser (taken over once from frte2tg, the app's old name), then the view of the address.
(() => {
  let fields = readPrefs().fields;
  if (!fields) {
    try {
      const old = JSON.parse(localStorage.getItem('frte2tg.prefs')) || {};
      if (old.fields || old.urls) writePrefs(old);
      fields = old.fields || (JSON.parse(localStorage.getItem('frte2tg.ui')) || {}).fields;
    } catch { }
    if (fields) writePrefs({ fields });
  }
  PREFS.forEach(id => { if (fields && id in fields) document.getElementById(id).value = fields[id]; });
  const start = location.pathname === '/' ? readPrefs().last || '/log' : location.pathname + location.search;
  history.replaceState({ n: navIndex }, '', start);
  loadLog();
  setRefresh();
  loadWhoami().then(render);
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => {});
})();

// Result of "apply" that reloaded the page for a new language.
try {
  const pending = sessionStorage.getItem('lookout.toast');
  if (pending) {
    sessionStorage.removeItem('lookout.toast');
    showToast(t('web.config.' + pending), pending === 'applied' ? 'ok' : 'err');
  }
} catch { }
