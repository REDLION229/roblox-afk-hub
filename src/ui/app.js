'use strict';
// Report script errors to the app (written to ui-errors.log) instead of failing silently.
window.addEventListener('error', (e) => {
  try { window.chrome.webview.postMessage({ cmd: 'jsError', message: `${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`, stack: e.error && e.error.stack }); } catch (_) {}
});
/* Roblox AFK Hub interface. All data comes from the C# side as "state" messages once a second;
   every action is sent back as { cmd, ... } via postMessage. */

// ---------------------------------------------------------------- icons
const ICONS = {
  home: '<path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V20h14V9.5"/><path d="M10 20v-6h4v6"/>',
  rocket: '<path d="M5 15c-1.5 1.5-2 5-2 5s3.5-.5 5-2"/><path d="M9 15l-3-3c1-4 4.5-8.5 12-9-.5 7.5-5 11-9 12z"/><circle cx="14.5" cy="9.5" r="1.5"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20c.6-3.6 3.3-5.5 6.5-5.5s5.9 1.9 6.5 5.5"/><path d="M16 4.6a3.5 3.5 0 0 1 0 6.8"/><path d="M18 14.8c2 .7 3.2 2.5 3.5 5.2"/>',
  chart: '<path d="M4 20V10"/><path d="M10 20V4"/><path d="M16 20v-7"/><path d="M21 20H3"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  layers: '<path d="M12 3 2 8l10 5 10-5-10-5z"/><path d="m2 13 10 5 10-5"/>',
  bolt: '<path d="M13 2 4 14h7l-1 8 9-12h-7l1-8z"/>',
  alert: '<path d="M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4"/><path d="M12 17h.01"/>',
  star: '<path d="m12 3 2.8 5.7 6.2.9-4.5 4.4 1 6.2L12 17.3 6.5 20.2l1-6.2L3 9.6l6.2-.9L12 3z"/>',
  play: '<path d="M7 4v16l13-8L7 4z"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  key: '<circle cx="7.5" cy="15.5" r="4.5"/><path d="m10.7 12.3 9.8-9.8"/><path d="m16 7 3 3"/><path d="m18.5 4.5 2 2"/>',
  refresh: '<path d="M21 12a9 9 0 0 1-15.5 6.2L3 16"/><path d="M3 12a9 9 0 0 1 15.5-6.2L21 8"/><path d="M21 3v5h-5"/><path d="M3 21v-5h5"/>',
  shield: '<path d="M12 3 4 6v6c0 5 3.4 8.4 8 9 4.6-.6 8-4 8-9V6l-8-3z"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z"/>',
  file: '<path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9l-6-6z"/><path d="M14 3v6h6"/>',
  moon: '<path d="M20.5 14.6A8.5 8.5 0 0 1 9.4 3.5a8.5 8.5 0 1 0 11.1 11.1z"/>',
  eye: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  pencil: '<path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5z"/>',
  x: '<path d="M18 6 6 18M6 6l12 12"/>',
  check: '<path d="M20 6 9 17l-5-5"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 16v-4"/><path d="M12 8h.01"/>',
  trash: '<path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/>',
  macro: '<path d="m9 9 5 12 1.8-5.2L21 14 9 9z"/><path d="m7.2 2.2.8 2.9"/><path d="m5.1 8-2.9-.8"/><path d="M14 4.1 12 6"/><path d="m6 12-1.9 2"/>',
  pause: '<rect x="6" y="5" width="4" height="14" rx="1"/><rect x="14" y="5" width="4" height="14" rx="1"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
  fit: '<path d="M3 8V5a2 2 0 0 1 2-2h3"/><path d="M16 3h3a2 2 0 0 1 2 2v3"/><path d="M21 16v3a2 2 0 0 1-2 2h-3"/><path d="M8 21H5a2 2 0 0 1-2-2v-3"/>',
  record: '<circle cx="12" cy="12" r="6" style="fill:currentColor"/>',
  chev: '<path d="m9 6 6 6-6 6"/>',
  grid: '<rect x="3" y="3" width="7.5" height="7.5" rx="1.5"/><rect x="13.5" y="3" width="7.5" height="7.5" rx="1.5"/><rect x="3" y="13.5" width="7.5" height="7.5" rx="1.5"/><rect x="13.5" y="13.5" width="7.5" height="7.5" rx="1.5"/>',
  palette: '<path d="M12 3a9 9 0 1 0 0 18c1.1 0 1.6-.8 1.6-1.5 0-.9-.7-1.3-.7-2.2 0-.8.7-1.4 1.5-1.4H17a4 4 0 0 0 4-4C21 6.6 17 3 12 3z"/><circle cx="7.5" cy="11.5" r="1"/><circle cx="10" cy="7.5" r="1"/><circle cx="15" cy="7.5" r="1"/>',
};
const icon = (name) => `<svg class="i" viewBox="0 0 24 24">${ICONS[name] || ''}</svg>`;
function hydrateIcons(root = document) {
  root.querySelectorAll('svg[data-icon]').forEach((el) => {
    el.setAttribute('viewBox', '0 0 24 24');
    el.innerHTML = ICONS[el.dataset.icon] || '';
    el.removeAttribute('data-icon');
  });
}

// ---------------------------------------------------------------- helpers
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const host = window.chrome && window.chrome.webview;
const send = (cmd, data = {}) => host && host.postMessage({ cmd, ...data });
const pad = (n) => String(n).padStart(2, '0');
const clock = (s) => `${Math.floor(s / 3600)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}`;
const mmss = (s) => `${Math.floor(s / 60)}:${pad(s % 60)}`;
function dur(s) {
  if (s >= 3600) return `${Math.floor(s / 3600)}h ${pad(Math.floor(s / 60) % 60)}m`;
  if (s >= 60) return `${Math.floor(s / 60)}m ${pad(s % 60)}s`;
  return `${s}s`;
}
const fmtInt = (n) => Number(n || 0).toLocaleString();
function setHtml(el, html) { if (el && el._html !== html) { el.innerHTML = html; el._html = html; } }
function setText(el, text) { if (el && el.textContent !== String(text)) el.textContent = text; }
const notFocused = (el) => document.activeElement !== el;
const initial = (name) => (String(name || '?').replace(/^[^A-Za-z0-9]+/, '')[0] || '?').toUpperCase();
function avatarHtml(url, name) { return url ? `<img src="${esc(url)}" alt="">` : esc(initial(name)); }

const DEMO = location.hash.startsWith('#demo');
const DEMO_THEME = DEMO ? location.hash.split(',')[1] : '';

// ---------------------------------------------------------------- state
let S = null;
let SESSIONS = [];
let LOGS = [];
let page = 'home';
const selected = new Set();
let selectedInit = false;
let linkInit = false;

const PAGES = {
  home: ['Home', 'Your AFK setup at a glance.'],
  launch: ['Launch', 'Open your accounts straight into a game — no browser needed.'],
  presets: ['Presets', 'Saved setups per game. One click and everything opens the way you left it.'],
  macros: ['Macros', 'Record or import macros and run them in any windows — they take turns.'],
  accounts: ['Accounts', 'Your saved Roblox logins, and how each account\'s window opens.'],
  stats: ['Stats', 'How your AFK sessions have gone.'],
  settings: ['Settings', 'What keeps you AFK. Everything saves automatically.'],
  customize: ['Customize', 'Looks and extras. None of this affects AFKing.'],
  misc: ['Misc', 'Credits, version and the activity log.'],
};

const THEMES = [
  { id: 'midnight', name: 'Midnight', bg: '#08090a', card: '#17181b', accent: '#e4f222', text: '#f7f8f8' },
  { id: 'glass', name: 'Glass', bg: '#0b1122', card: '#1c2642', accent: '#4c8dff', text: '#f4f6fb' },
  { id: 'graphite', name: 'Graphite', bg: '#16171a', card: '#26282d', accent: '#8b7cf6', text: '#eef0f4' },
  { id: 'ember', name: 'Ember', bg: '#120d0b', card: '#231a16', accent: '#ff7a45', text: '#fbf3ee' },
  { id: 'paper', name: 'Paper', bg: '#f4f5f7', card: '#ffffff', accent: '#2563eb', text: '#111318' },
  { id: 'avatar', name: 'Your avatar', bg: '#0b0c0f', card: '#1a1c22', accent: null, text: '#f7f8f8' },
];
const themeAccent = (t) => t.accent || (S && S.personal && S.personal.accent) || '#7c9cff';

// ---------------------------------------------------------------- navigation
function go(p) {
  if (!PAGES[p]) p = 'home';
  page = p;
  $$('.nav-btn').forEach((b) => b.classList.toggle('active', b.dataset.page === p));
  $$('.page').forEach((s) => s.classList.toggle('active', s.id === `page-${p}`));
  setText($('#page-title'), PAGES[p][0]);
  setText($('#page-sub'), PAGES[p][1]);
  $('#content').scrollTop = 0;
  render();
}
document.addEventListener('click', (e) => {
  const nav = e.target.closest('[data-page]');
  if (nav) return go(nav.dataset.page);
  const to = e.target.closest('[data-goto]');
  if (to) go(to.dataset.goto);
});

// ---------------------------------------------------------------- toasts & modals
function toast(text, kind = 'info') {
  const el = document.createElement('div');
  el.className = `toast ${kind}`;
  const ic = { good: 'check', bad: 'alert', warn: 'alert', info: 'info' }[kind] || 'info';
  el.innerHTML = `${icon(ic)}<span>${esc(text)}</span>`;
  $('#toasts').appendChild(el);
  setTimeout(() => { el.style.opacity = '0'; el.style.transition = 'opacity .3s'; }, 4200);
  setTimeout(() => el.remove(), 4600);
}

/** `extra` is HTML shown under the text (e.g. a fold-out how-to). */
function modal({ title, text = '', extra = '', input = null, ok = 'OK', danger = false }) {
  return new Promise((resolve) => {
    const back = document.createElement('div');
    back.className = 'modal-back';
    const field = !input ? ''
      : input.select
        ? `<select class="input">${input.select.map((o) => `<option value="${esc(o.value)}">${esc(o.label)}</option>`).join('')}</select>`
      : input.textarea
        ? `<textarea class="input" rows="5" placeholder="${esc(input.placeholder || '')}">${esc(input.value || '')}</textarea>`
        : `<input class="input" placeholder="${esc(input.placeholder || '')}" value="${esc(input.value || '')}">`;
    back.innerHTML = `<div class="modal"><h3>${esc(title)}</h3>${text ? `<p>${text}</p>` : ''}${extra}${field}
      <div class="actions"><button class="btn" data-r="cancel">Cancel</button>
      <button class="btn ${danger ? 'danger' : 'primary'}" data-r="ok">${esc(ok)}</button></div></div>`;
    $('#modal-root').appendChild(back);
    const fieldEl = $('.input', back);
    if (fieldEl) { fieldEl.focus(); if (fieldEl.select) fieldEl.select(); }
    const done = (v) => { back.remove(); resolve(v); };
    back.addEventListener('click', (e) => {
      if (e.target === back) return done(null);
      const r = e.target.closest('[data-r]');
      if (r) done(r.dataset.r === 'ok' ? (fieldEl ? fieldEl.value : true) : null);
    });
    back.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') done(null);
      if (e.key === 'Enter' && !(fieldEl && fieldEl.tagName === 'TEXTAREA')) done(fieldEl ? fieldEl.value : true);
    });
  });
}

// ---------------------------------------------------------------- charts
function dailyBuckets(days) {
  const out = [];
  const today = new Date(); today.setHours(0, 0, 0, 0);
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(today); d.setDate(d.getDate() - i);
    out.push({ date: d, seconds: 0, presses: 0 });
  }
  const all = S && S.session ? [S.session, ...SESSIONS] : SESSIONS;
  for (const s of all) {
    const d = new Date(s.start); d.setHours(0, 0, 0, 0);
    const b = out.find((x) => x.date.getTime() === d.getTime());
    if (b) { b.seconds += s.seconds; b.presses += s.presses; }
  }
  return out;
}

function niceMax(v) {
  if (v <= 0) return 1;
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * p >= v) return m * p;
  return 10 * p;
}

let gradId = 0;
function sparkline(svg, values) {
  if (!svg) return;
  const n = values.length, max = Math.max(...values, 0);
  const show = n >= 2 && values.filter((v) => v > 0).length >= 2;
  svg.closest('.stat')?.classList.toggle('has-spark', show);
  if (!show) { setHtml(svg, ''); return; }
  svg.setAttribute('viewBox', '0 0 100 40');
  const pts = values.map((v, i) => [(i / (n - 1)) * 100, 38 - (v / max) * 26]);
  // smooth curve through the points (Catmull-Rom → Bézier)
  let line = `M${pts[0][0]},${pts[0][1]}`;
  for (let i = 0; i < n - 1; i++) {
    const p0 = pts[i - 1] || pts[i], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2] || p2;
    const c1 = [p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6];
    const c2 = [p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6];
    line += ` C${c1[0].toFixed(2)},${c1[1].toFixed(2)} ${c2[0].toFixed(2)},${c2[1].toFixed(2)} ${p2[0].toFixed(2)},${p2[1].toFixed(2)}`;
  }
  const id = svg._gid || (svg._gid = `sg${++gradId}`);
  setHtml(svg, `<defs><linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" style="stop-color:var(--chart);stop-opacity:.35"/><stop offset="1" style="stop-color:var(--chart);stop-opacity:0"/></linearGradient></defs>
    <path d="${line} L100,40 L0,40 Z" fill="url(#${id})"/>
    <path d="${line}" fill="none" style="stroke:var(--chart)" stroke-width="1.5" vector-effect="non-scaling-stroke"/>`);
}

function axisChart(svg, data, { kind, format }) {
  if (!svg) return;
  const W = Math.max(320, svg.clientWidth || 600), H = 230, L = 40, R = 8, T = 10, B = 26;
  const max = niceMax(Math.max(...data.map((d) => d.value), 0));
  const iw = W - L - R, ih = H - T - B, step = iw / data.length;
  let out = `<defs><linearGradient id="areaFill" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" style="stop-color:var(--chart);stop-opacity:.32"/><stop offset="1" style="stop-color:var(--chart);stop-opacity:0"/></linearGradient></defs>`;
  for (let g = 0; g <= 4; g++) {
    const y = T + ih - (ih * g) / 4;
    out += `<line class="grid-line" x1="${L}" x2="${W - R}" y1="${y}" y2="${y}"/><text x="${L - 8}" y="${y + 4}" text-anchor="end">${format((max * g) / 4)}</text>`;
  }
  data.forEach((d, i) => {
    if (i % 2 === (data.length - 1) % 2) out += `<text x="${L + step * i + step / 2}" y="${H - 6}" text-anchor="middle">${d.label}</text>`;
  });
  if (kind === 'bar') {
    data.forEach((d, i) => {
      const h = (d.value / max) * ih, bw = Math.min(28, step * 0.62);
      const x = L + step * i + (step - bw) / 2, y = T + ih - h;
      out += `<rect class="bar-rect" x="${x}" y="${y}" width="${bw}" height="${Math.max(h, d.value > 0 ? 2 : 0)}" rx="4"><title>${d.title}</title></rect>`;
    });
  } else {
    const pts = data.map((d, i) => [L + step * i + step / 2, T + ih - (d.value / max) * ih]);
    const line = pts.map((p, i) => `${i ? 'L' : 'M'}${p[0]},${p[1]}`).join(' ');
    out += `<path class="area" d="${line} L${pts[pts.length - 1][0]},${T + ih} L${pts[0][0]},${T + ih} Z"/><path class="line" d="${line}"/>`;
    pts.forEach((p, i) => { if (data[i].value > 0) out += `<circle cx="${p[0]}" cy="${p[1]}" r="3.5" style="fill:var(--chart)"><title>${data[i].title}</title></circle>`; });
  }
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
  setHtml(svg, out);
}

// ---------------------------------------------------------------- render: shell
function applyTheme(theme, effectsOff) {
  const root = document.documentElement;
  const p = S && S.personal;
  if (p) {
    const acc = p.accent || '#7c9cff', ink = p.accentInk || '#0b0c0f';
    if (root.style.getPropertyValue('--avatar-accent') !== acc) {
      root.style.setProperty('--avatar-accent', acc);
      root.style.setProperty('--avatar-ink', ink);
      root.style.setProperty('--hero-accent', acc);
    }
  }
  if (root.dataset.theme !== theme) root.dataset.theme = theme;
  const fx = effectsOff ? 'off' : 'on';
  if (root.dataset.effects !== fx) root.dataset.effects = fx;
  $$('.theme-dot, .theme-card').forEach((d) => d.classList.toggle('active', d.dataset.t === theme));
}

function renderTop() {
  const m = $('#chip-multi');
  const multi = {
    off: ['', 'Multi-instance off'],
    ready: ['good', 'Multi-instance ready'],
    unlocked: ['good', `Multi-instance · ${S.multiCount} unlocked`],
    unlocking: ['warn', 'Unlocking new window…'],
    blocked: ['bad', 'Multi-instance blocked'],
  }[S.multi] || ['', S.multi];
  m.className = `chip ${multi[0]}`;
  setText($('.t', m), multi[1]);
  $('.dot', m).classList.toggle('pulse', S.multi === 'unlocking');

  const a = $('#chip-antiafk');
  a.className = `chip ${S.antiAfk ? 'good' : ''}`;
  setText($('.t', a), S.antiAfk ? 'Anti-AFK on' : 'Anti-AFK paused');

  const b = $('#afk-btn');
  if (S.afk && S.session) {
    b.className = 'btn afk-btn on';
    setHtml(b, `${icon('check')}<span>I'm back</span><span class="clock">${clock(S.session.seconds)}</span>`);
    b.title = 'End the AFK session and put your normal FPS back';
  } else {
    b.className = 'btn primary afk-btn';
    setHtml(b, `${icon('moon')}<span>Start AFK mode</span>`);
    b.title = 'Switch to your AFK FPS, turn on anti-AFK, and start timing the session';
  }
  // A new version on GitHub (hidden after "Later" until an even newer one comes out).
  const up = S.update || {};
  $('#chip-update').classList.toggle('hidden', !up.available || up.skipped);
  setText($('.t', $('#chip-update')), up.busy ? 'Updating…' : `Update to v${up.available || ''}`);
  // Version badge in the sidebar: always visible, says whether this is the newest version.
  const sv = $('#side-version');
  const st = up.busy ? ['', 'Checking…'] : up.available ? ['warn', 'Update available'] : up.upToDate ? ['good', 'Up to date'] : ['', 'Check for updates'];
  setText($('.v', sv), `v${S.version}`);
  $('.dot', sv).className = `dot ${st[0]}`;
  setText($('.s', sv), st[1]);
  sv.title = up.available ? `v${up.available} is out. Click to update.` : up.checkedAgo ? `Last checked ${up.checkedAgo}. Click to check again.` : 'Click to check for updates';
  // Shown while AFK mode is on, or whenever Roblox windows are open.
  $('#afk-close-btn').classList.toggle('hidden', !(S.afk && S.session) && !S.windows.length);
  setHtml($('#afk-close-btn'), `${icon('x')}${S.afk && S.session ? 'Back &amp; close all' : 'Close all Roblox'}`);
  const count = $('#nav-acct-count');
  setText(count, S.accounts.length || '');
  count.classList.toggle('hidden', !S.accounts.length);
  setText($('#admin-note'), S.admin ? 'Running as administrator' : '');
  // Macros can be hidden (Settings → Advanced); it stays visible while one is running or recording.
  $('.nav-btn[data-page="macros"]').classList.toggle('hidden', !S.cfg.showMacros && !S.macro.running && !S.macro.recording);
  $('#nav-macro-live').classList.toggle('hidden', !['Playing', 'Yielding', 'Yielded', 'TurnDone'].includes(S.macro.state));
}

// ---------------------------------------------------------------- render: home
function greeting() {
  const h = new Date().getHours();
  return h < 5 ? 'Up late,' : h < 12 ? 'Good morning,' : h < 17 ? 'Good afternoon,' : h < 22 ? 'Good evening,' : 'Up late,';
}

let tiltBound = false;
function renderHero() {
  const p = S.personal, hero = $('#hero');
  hero.classList.toggle('hidden', !p.name);
  if (!p.name) return;
  hero.classList.toggle('fx', !!(p.effects && !S.cfg.reduceEffects));
  setText($('#hero-greet'), greeting());
  setText($('#hero-name'), p.name);

  const sw = $('#hero-switch');
  sw.classList.toggle('hidden', S.accounts.length < 2);
  const swKey = S.accounts.map((a) => a.id + (a.display || a.name)).join('|');
  if (sw._key !== swKey) { sw._key = swKey; sw.innerHTML = S.accounts.map((a) => `<option value="${a.id}">${esc(a.display || a.name)}</option>`).join(''); }
  if (notFocused(sw)) sw.value = p.mainId || '';

  const wins = S.windows, inGame = wins.filter((w) => w.status === 'In game').length;
  const running = S.macro && S.macro.running;
  const watch = S.watch || [], down = watch.filter((w) => !w.ok).length;
  let sub;
  if (S.afk && S.session && down) sub = `AFK for ${clock(S.session.seconds)} — ${watch.length - down}/${watch.length} accounts in game, ${down === 1 ? 'one needs' : `${down} need`} attention.`;
  else if (S.afk && S.session) sub = `AFK for ${clock(S.session.seconds)} — ${wins.length ? `${wins.length} window${wins.length === 1 ? '' : 's'} going strong.` : 'no windows open yet.'}`;
  else if (wins.length) sub = `${wins.length} Roblox window${wins.length === 1 ? '' : 's'} open${inGame === wins.length ? ', all in game' : ''}.`;
  else sub = 'Ready when you are.';
  if (running) sub += ` ${running} macro window${running === 1 ? '' : 's'} running.`;
  setText($('#hero-sub'), sub);

  const chips = [];
  if (p.presence) chips.push(`<span class="chip ${p.presence.kind === 'good' ? 'good' : ''}">${p.presence.icon ? `<img src="${esc(p.presence.icon)}" style="width:18px;height:18px;border-radius:5px;margin-left:-4px">` : '<span class="dot"></span>'}${esc(p.presence.text)}</span>`);
  chips.push(`<span class="chip"><span class="dot ${S.accounts.length ? 'good' : ''}"></span>${S.accounts.length} account${S.accounts.length === 1 ? '' : 's'}</span>`);
  if (p.username && p.username !== p.name) chips.push(`<span class="chip">@${esc(p.username)}</span>`);
  setHtml($('#hero-chips'), chips.join(''));

  const rl = $('#hero-relaunch');
  const hasLast = !!(S.cfg.lastLink && S.cfg.lastAccounts.length);
  rl.classList.toggle('hidden', !hasLast);
  rl.disabled = S.launching;
  if (hasLast) {
    const fav = S.favorites.find((f) => f.link === S.cfg.lastLink);
    const placeMatch = /games\/(\d+)/.exec(S.cfg.lastLink);
    const recent = placeMatch ? S.recent.find((r) => r.placeId === placeMatch[1]) : null;
    const what = fav ? fav.name : recent && recent.game ? recent.game : 'last game';
    const iconUrl = (fav && fav.icon) || (recent && recent.icon);
    setHtml($('.rl-icon', rl), iconUrl ? `<img src="${esc(iconUrl)}" alt="" class="rl-img">` : icon('refresh'));
    setText($('.rl-text', rl), S.launching ? 'Launching…' : `Relaunch ${what} · ${S.cfg.lastAccounts.length} account${S.cfg.lastAccounts.length === 1 ? '' : 's'}`);
    rl.title = fav && fav.game ? `${fav.game} — launch the same accounts into it again` : 'Launch the same accounts into the same game again';
  }

  // Favorite presets: one click from Home.
  const favs = (S.presets || []).filter((x) => x.favorite).slice(0, S.maxFavoritePresets || 3);
  const hp = $('#hero-presets');
  hp.classList.toggle('hidden', !favs.length);
  setHtml(hp, favs.map((x) => `<button class="btn preset-chip" data-preset="${esc(x.id)}" ${S.launching ? 'disabled' : ''} title="Launch preset: ${esc(x.name)}${x.game ? ` (${esc(x.game)})` : ''}">${x.icon ? `<img src="${esc(x.icon)}" alt="">` : icon('grid')}<span>${esc(x.name)}</span></button>`).join(''));

  const show = p.showAvatar && p.full;
  $('#hero-avatar').classList.toggle('hidden', !p.showAvatar);
  $('.hero-main', hero).style.gridTemplateColumns = p.showAvatar ? '' : '1fr';
  const img = $('#hero-img');
  if (show && img.getAttribute('src') !== p.full) img.src = p.full;


  if (!tiltBound) {
    tiltBound = true;
    hero.addEventListener('mousemove', (e) => {
      if (!hero.classList.contains('fx')) return;
      const r = hero.getBoundingClientRect();
      const x = (e.clientX - r.left) / r.width - 0.5, y = (e.clientY - r.top) / r.height - 0.5;
      $('#hero-tilt').style.transform = `rotateY(${(x * 22).toFixed(1)}deg) rotateX(${(-y * 10).toFixed(1)}deg)`;
    });
    hero.addEventListener('mouseleave', () => { $('#hero-tilt').style.transform = ''; });
  }
}
$('#hero-switch').addEventListener('change', (e) => send('setConfig', { key: 'mainAccount', value: e.target.value }));
$('#hero-refresh').addEventListener('click', () => send('refreshAvatar'));
$('#hero-relaunch').addEventListener('click', () => send('quickRelaunch'));
$('#hero-presets').addEventListener('click', (e) => {
  const b = e.target.closest('[data-preset]');
  if (b) send('presetLaunch', { id: b.dataset.preset });
});

function renderHome() {
  const cfg = S.cfg;
  const rb = $('#resume-banner');
  rb.classList.toggle('hidden', S.resumeIn < 0);
  if (S.resumeIn >= 0) setText($('#resume-text'), `The PC restarted while you were AFK. Relaunching your last setup in ${S.resumeIn} s.`);
  renderHero();
  // first run: how will you use it? (only on a fresh install)
  const askMode = !cfg.mode && !cfg.hasLaunched && !S.accounts.length;
  $('#mode-card').classList.toggle('hidden', !askMode);
  // guide
  const showGuide = !askMode && !cfg.guideDismissed && !(S.accounts.length && cfg.hasLaunched);
  $('#guide-card').classList.toggle('hidden', !showGuide);
  if (showGuide) {
    const steps = [
      [S.accounts.length > 0, 'Add your accounts', 'Log in once per account — no more switching in the browser.', 'accounts'],
      [!!(cfg.lastLink || S.favorites.length), 'Choose your game', 'Paste a game or private server link, and save it.', 'launch'],
      [cfg.hasLaunched, 'Launch', 'Pick accounts and press Launch. Done.', 'launch'],
    ];
    setHtml($('#guide-steps'), steps.map(([done, t, d, to], i) =>
      `<div class="step ${done ? 'done' : ''}" data-goto="${to}"><span class="n">${done ? '✓' : i + 1}</span><div><b>${t}</b><span>${d}</span></div></div>`).join(''));
  }

  // stat cards
  const ses = S.session, wins = S.windows;
  if (ses) {
    const s = ses.seconds;
    setHtml($('#st-afk'), `${Math.floor(s / 3600)}:${pad(Math.floor(s / 60) % 60)}<small>:${pad(s % 60)}</small>`);
    const since = new Date(ses.start).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    setHtml($('#st-afk-foot'), `<span class="badge good"><span class="dot good"></span>Running</span><span>since ${since}</span>`);
  } else {
    setHtml($('#st-afk'), 'Off');
    setHtml($('#st-afk-foot'), '<span>Press <b>Start AFK mode</b> to begin</span>');
  }
  const days7 = dailyBuckets(7);
  sparkline($('#spark-afk'), days7.map((d) => d.seconds));
  sparkline($('#spark-press'), days7.map((d) => d.presses));

  setText($('#st-win'), wins.length);
  const inGame = wins.filter((w) => w.status === 'In game').length;
  setHtml($('#st-win-foot'), wins.length
    ? `<span class="badge ${inGame === wins.length ? 'good' : ''}">${inGame} in game</span>${S.backgroundRoblox ? `<span>${S.backgroundRoblox} in background</span>` : ''}`
    : '<span>None open right now</span>');

  const presses = ses ? ses.presses : wins.reduce((a, w) => a + w.presses, 0);
  setText($('#st-press'), fmtInt(presses));
  const nexts = wins.filter((w) => w.next != null).map((w) => w.next);
  setHtml($('#st-press-foot'), nexts.length ? `<span>Next one in <b class="mono">${mmss(Math.min(...nexts))}</b></span>`
    : `<span>${S.antiAfk ? 'Waiting for windows' : 'Anti-AFK is paused'}</span>`);

  const disc = ses ? ses.disconnects : 0;
  const missing = (S.watch || []).filter((w) => !w.ok && !w.hasWindow).length;
  const badNow = wins.filter((w) => w.statusKind === 'bad').length + missing;
  setText($('#st-disc'), disc);
  setHtml($('#st-disc-foot'), badNow ? `<span class="badge bad">${badNow} ${missing ? 'down' : 'disconnected'} now</span>`
    : wins.length ? '<span class="badge good">All connected</span>' : '<span>Nothing to watch yet</span>');

  renderWindows();
  renderFps();
}

// Windows whose details panel is open (by PID).
const openDetails = new Set();

function detailsHtml(w) {
  const d = w.details || {};
  const kv = (k, v) => `<div><span>${k}</span><b>${v}</b></div>`;
  const r = 24, c = 2 * Math.PI * r, score = d.health ?? 0;
  const copy = (v, label) => v ? `<button class="copy" data-copy="${esc(v)}" title="Copy ${label}">${esc(v.length > 20 ? v.slice(0, 8) + '…' : v)} ${icon('file')}</button>` : '—';
  return `
    <div class="wd-sec"><span class="label">Process</span><div class="kv">
      ${kv('CPU', d.cpu != null ? `${d.cpu.toFixed(1)}%` : '—')}
      ${kv('Memory', d.ramMb != null ? `${fmtInt(d.ramMb)} MB <small>(${d.ramPct}%)</small>` : '—')}
      ${kv('Threads', d.threads ?? '—')}
      ${kv('PID', w.pid)}
      ${kv('Running', dur(w.uptime))}
    </div></div>
    <div class="wd-sec"><span class="label">Game</span>
      <div class="wd-game">${d.gameIcon ? `<img src="${esc(d.gameIcon)}" alt="">` : `<span class="gi-empty">${icon('rocket')}</span>`}
        <b>${esc(d.game || (d.placeId ? 'Place ' + d.placeId : 'Not in a game yet'))}</b></div>
      <div class="kv">${kv('Place ID', copy(d.placeId, 'place ID'))}${kv('Server', copy(d.jobId, 'server ID'))}${kv('In this server', d.joined != null ? dur(d.joined) : '—')}</div>
    </div>
    <div class="wd-sec wd-health ${esc(d.healthKind || '')}"><span class="label">Stability</span>
      <svg viewBox="0 0 60 60" class="ring"><circle cx="30" cy="30" r="${r}" class="track"/><circle cx="30" cy="30" r="${r}" class="val"
        stroke-dasharray="${(c * score / 100).toFixed(1)} ${c.toFixed(1)}" transform="rotate(-90 30 30)"/><text x="30" y="34">${score}%</text></svg>
      <b>${esc(d.healthLabel || '')}</b>
    </div>`;
}

function renderWindows() {
  const list = $('#win-list');
  const wins = S.windows;
  $('#arrange-tile').classList.toggle('hidden', wins.length < 2);
  $('#arrange-stack').classList.toggle('hidden', wins.length < 2);
  // Accounts AFK mode expects that have no window right now (crashed, closed, rejoining).
  const gone = (S.watch || []).filter((w) => !w.ok && !w.hasWindow);
  $('#win-empty').classList.toggle('hidden', wins.length + gone.length > 0);
  list.classList.toggle('hidden', wins.length + gone.length === 0);
  const keep = new Set([...wins.map((w) => String(w.pid)), ...gone.map((w) => `acct-${w.id}`)]);
  $$('.win', list).forEach((el) => { if (!keep.has(el.dataset.pid)) el.remove(); });

  wins.forEach((w, idx) => {
    let el = $(`.win[data-pid="${w.pid}"]`, list);
    if (!el) {
      el = document.createElement('div');
      el.className = 'win';
      el.dataset.pid = w.pid;
      el.innerHTML = `
        <div class="who"><div class="avatar"></div><div style="min-width:0"><div class="name"></div><div class="meta"></div></div></div>
        <div class="badges"></div>
        <div class="timer"><div class="row1"><span class="lbl muted"></span><b></b></div><div class="bar"><i></i></div></div>
        <div class="win-actions">
          <label class="switch" title="Anti-AFK for this window"><input type="checkbox" data-act="toggle"><span></span></label>
          <button class="btn sm" data-act="press" title="Press the anti-AFK key in this window right now">${icon('bolt')}Press now</button>
          <button class="btn icon sm" data-act="show" title="Bring this window to the front">${icon('eye')}</button>
          <button class="btn icon sm" data-act="rename" title="Name this window">${icon('pencil')}</button>
          <button class="btn icon sm wd-toggle" data-act="details" title="Details: CPU, memory, game and how steady it's been">${icon('chev')}</button>
        </div>
        <div class="win-details hidden"></div>`;
      list.appendChild(el);
    }
    const open = openDetails.has(String(w.pid));
    el.classList.toggle('open', open);
    $('.win-details', el).classList.toggle('hidden', !open);
    if (open) setHtml($('.win-details', el), detailsHtml(w));
    if (list.children[idx] !== el) list.insertBefore(el, list.children[idx]);
    el.classList.toggle('is-bad', w.statusKind === 'bad');
    setHtml($('.avatar', el), avatarHtml(w.avatar, w.name));
    setText($('.name', el), w.name);
    const meta = $('.meta', el);
    setText(meta, `Open ${dur(w.uptime)} · ${w.presses} press${w.presses === 1 ? '' : 'es'}`);
    meta.title = w.lastPress ? `Last anti-AFK press ${w.lastPress}` : 'No anti-AFK press needed yet';
    setHtml($('.badges', el), [
      `<span class="badge ${w.statusKind}">${w.statusKind === 'good' ? '<span class="dot good"></span>' : ''}${esc(w.status)}</span>`,
      w.fps ? `<span class="badge accent" title="FPS this window opened with">${w.fps} FPS${w.low ? ' · low gfx' : ''}</span>` : '',
      `<span class="badge ${w.multiKind}" title="Multi-instance status">${esc(w.multi)}</span>`,
      w.macro ? `<span class="badge ${w.macro.active ? 'accent' : ''}" title="${w.macro.active ? 'Playing its macro now' : w.macro.running ? 'Waiting for its turn' : 'Macro assigned (not running)'}">${w.macro.active ? '▶ ' : w.macro.running ? '⏳ ' : ''}${esc(w.macro.name)}</span>` : '',
    ].join(''));
    const lbl = $('.timer .lbl', el), num = $('.timer b', el), bar = $('.bar', el), fill = $('.bar > i', el);
    if (w.next != null) {
      setText(lbl, 'Next anti-AFK press');
      setText(num, mmss(w.next));
      fill.style.width = `${Math.min(100, 100 * (1 - w.next / S.intervalSeconds)).toFixed(1)}%`;
      bar.classList.remove('warn');
    } else {
      setText(lbl, w.nextLabel || '');
      setText(num, '');
      fill.style.width = w.nextLabel && w.nextLabel.startsWith('Pressing') ? '100%' : '0%';
      bar.classList.toggle('warn', !!(w.nextLabel && w.nextLabel.startsWith('Waiting')));
    }
    const sw = $('[data-act="toggle"]', el);
    if (sw.checked !== w.antiAfkOn) sw.checked = w.antiAfkOn;
    $('[data-act="rename"]', el).classList.toggle('hidden', w.fromApp);
  });

  gone.forEach((w) => {
    const key = `acct-${w.id}`;
    let el = $(`.win[data-pid="${key}"]`, list);
    if (!el) {
      el = document.createElement('div');
      el.className = 'win is-bad';
      el.dataset.pid = key;
      el.innerHTML = `
        <div class="who"><div class="avatar"></div><div style="min-width:0"><div class="name"></div><div class="meta"></div></div></div>
        <div class="badges"></div>
        <div class="timer"></div>
        <div class="win-actions">
          <button class="btn sm" data-act="unwatch" title="Stop rejoining this account until you launch it again">${icon('x')}Stop watching</button>
        </div>`;
    }
    list.appendChild(el);
    setHtml($('.avatar', el), avatarHtml(null, w.name));
    setText($('.name', el), w.name);
    setText($('.meta', el), w.detail);
    setHtml($('.badges', el), `<span class="badge bad">${w.state === 'dropped' ? 'Dropped' : w.state === 'retrying' ? 'Retrying' : 'Rejoining'}</span>`);
  });
}

$('#win-list').addEventListener('click', async (e) => {
  const cp = e.target.closest('[data-copy]');
  if (cp) {
    try { await navigator.clipboard.writeText(cp.dataset.copy); toast('Copied.', 'good'); } catch (_) { toast('Couldn\'t copy.', 'warn'); }
    return;
  }
  const btn = e.target.closest('[data-act]');
  if (!btn || btn.dataset.act === 'toggle') return;
  if (btn.dataset.act === 'unwatch') return send('unwatch', { id: btn.closest('.win').dataset.pid.slice(5) });
  if (btn.dataset.act === 'details') {
    const id = btn.closest('.win').dataset.pid;
    if (openDetails.has(id)) openDetails.delete(id); else openDetails.add(id);
    return renderWindows();
  }
  const pid = Number(btn.closest('.win').dataset.pid);
  const act = btn.dataset.act;
  if (act === 'press') send('winPress', { pid });
  if (act === 'show') send('winShow', { pid });
  if (act === 'rename') {
    const w = S.windows.find((x) => x.pid === pid);
    const name = await modal({ title: 'Name this window', text: 'Which account is in this window? Only used for display.', input: { value: w && !w.name.startsWith('Roblox (') ? w.name : '', placeholder: 'e.g. Main account' }, ok: 'Save' });
    if (name !== null) send('winRename', { pid, name });
  }
});
$('#arrange-tile').addEventListener('click', () => send('arrangeWindows', { mode: 'tile' }));
$('#arrange-stack').addEventListener('click', () => send('arrangeWindows', { mode: 'stack' }));
$('#win-list').addEventListener('change', (e) => {
  if (e.target.dataset.act === 'toggle') send('winToggle', { pid: Number(e.target.closest('.win').dataset.pid) });
});

function renderFps() {
  const f = S.fps, cfg = S.cfg;
  setText($('#fps-now'), f.current ?? '—');
  const seg = $('#fps-seg');
  const key = cfg.fpsPresets.join(',');
  if (seg._key !== key) {
    seg._key = key;
    seg.innerHTML = cfg.fpsPresets.map((v) => `<button data-fps="${v}">${v}</button>`).join('');
  }
  $$('button', seg).forEach((b) => b.classList.toggle('on', Number(b.dataset.fps) === f.current));
  const lock = $('#fps-lock');
  if (lock.checked !== f.locked) lock.checked = f.locked;
  setText($('#fps-note'), !f.exists ? 'Roblox\'s settings file wasn\'t found — open Roblox once so it creates it.'
    : S.windows.length ? 'Windows that are already open keep their FPS until they rejoin.' : `Normal ${cfg.normalFps} · AFK ${cfg.afkFps}. Change these in Settings.`);
}
$('#fps-seg').addEventListener('click', (e) => { const b = e.target.closest('[data-fps]'); if (b) send('setFps', { value: Number(b.dataset.fps) }); });
$('#fps-set').addEventListener('click', () => { const v = Number($('#fps-custom').value); if (v) { send('setFps', { value: v }); $('#fps-custom').value = ''; } });
$('#fps-custom').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('#fps-set').click(); });
$('#fps-lock').addEventListener('change', (e) => send('setLock', { on: e.target.checked }));

// activity feed
function feedItem(l) {
  const el = document.createElement('div');
  el.className = `feed-item ${l.level}`;
  el.innerHTML = `<span class="dot ${l.level === 'info' ? '' : l.level}"></span><span class="msg">${esc(l.text)}</span><span class="t">${esc(l.time)}</span>`;
  return el;
}
function setLogs(logs) {
  LOGS = logs.slice(-150).reverse();
  const feed = $('#feed');
  feed.innerHTML = '';
  LOGS.forEach((l) => feed.appendChild(feedItem(l)));
}
function addLog(l) {
  LOGS.unshift(l);
  const feed = $('#feed');
  feed.insertBefore(feedItem(l), feed.firstChild);
  while (feed.children.length > 150) feed.lastChild.remove();
}

// ---------------------------------------------------------------- render: launch
let parseTimer = 0;
function requestParse() {
  clearTimeout(parseTimer);
  parseTimer = setTimeout(() => {
    const v = $('#link').value.trim();
    if (!v) { setHtml($('#parsed'), ''); return; }
    send('parseLink', { link: v });
  }, 220);
}
$('#link').addEventListener('input', requestParse);

function fpsLabel(a) { return a.fps > 0 ? `${a.fps} FPS` : `AFK FPS (${S.cfg.afkFps})`; }

function renderLaunch() {
  const cfg = S.cfg;
  if (!linkInit) { linkInit = true; $('#link').value = cfg.lastLink || ''; requestParse(); }
  if (!selectedInit && S.accounts.length) {
    selectedInit = true;
    (cfg.lastAccounts.length ? cfg.lastAccounts : S.accounts.map((a) => a.id)).forEach((id) => selected.add(id));
  }
  setHtml($('#favs'), S.favorites.map((f, i) =>
    `<span class="fav" data-i="${i}" title="${esc(f.game ? `${f.game} — ${f.link}` : f.link)}">${f.icon ? `<img class="gi" src="${esc(f.icon)}" alt="">` : icon('star')}${esc(f.name)}${f.game && f.game !== f.name ? `<span class="gn">${esc(f.game)}</span>` : ''}<button class="x" data-x="${i}" title="Remove from favorites">${icon('x')}</button></span>`).join(''));

  const list = $('#pick-list');
  $('#pick-empty').classList.toggle('hidden', S.accounts.length > 0);
  list.classList.toggle('hidden', !S.accounts.length);
  setHtml(list, S.accounts.map((a) => `
    <div class="pick ${selected.has(a.id) ? 'on' : ''}" data-id="${a.id}">
      <div class="avatar">${avatarHtml(a.avatar, a.display || a.name)}</div>
      <div class="info"><b>${esc(a.display || a.name)}</b>
        <div class="badges" style="margin-top:4px"><span class="badge accent">${fpsLabel(a)}</span>${a.low ? '<span class="badge">Low gfx</span>' : ''}${a.presence && a.presence.kind === 'good' ? `<span class="badge good" title="${esc(a.presence.text)}">Online</span>` : ''}</div></div>
      <span class="tick">${icon('check')}</span>
    </div>`).join(''));

  // recently played
  $('#recent-empty').classList.toggle('hidden', S.recent.length > 0);
  setHtml($('#recent-list'), S.recent.map((r) =>
    `<span class="fav" data-place="${r.placeId}" title="${esc(r.game || 'Place ' + r.placeId)} — joined ${r.joins} time${r.joins === 1 ? '' : 's'}">${r.icon ? `<img class="gi" src="${esc(r.icon)}" alt="">` : icon('clock')}${esc(r.game || 'Place ' + r.placeId)}<span class="ago">${esc(r.ago)}</span></span>`).join(''));

  // friends in game
  const fc = $('#friends-card');
  fc.classList.toggle('hidden', !S.cfg.friendsOn || !S.accounts.length);
  if (S.cfg.friendsOn) {
    setText($('#friends-sub'), !S.friendsLoaded ? 'Checking your main account\'s friends…'
      : S.friends.length ? `${S.friends.length} of ${S.friendCount} friends are in a game. "Join" sends the accounts you've ticked to their server.`
      : `None of your ${S.friendCount} friends are in a game right now.`);
    setHtml($('#friends-list'), S.friends.slice(0, 30).map((f) => `
      <div class="friend">
        <div class="avatar">${avatarHtml(f.headshot, f.display || f.name)}</div>
        <div class="info"><b>${esc(f.display || f.name || 'Friend')}${f.mine ? ' <span class="badge main" style="height:18px;font-size:11px">Your account</span>' : ''}</b>
          <span>${f.icon ? `<img src="${esc(f.icon)}" alt="">` : ''}${esc(f.game || 'In a game')}</span></div>
        <button class="btn primary sm" data-join="${f.id}" title="${f.canJoinServer ? 'Joins their exact server' : 'Follows them into their game (works if their privacy settings allow it)'}">${icon('play')}Join</button>
      </div>`).join(''));
  }

  const count = [...selected].filter((id) => S.accounts.some((a) => a.id === id)).length;
  setText($('#launch-count'), count ? `${count} account${count === 1 ? '' : 's'} selected` : 'Select at least one account');
  const btn = $('#launch-btn');
  btn.disabled = S.launching || !count;
  setText($('span', btn), S.launching ? 'Launching…' : count > 1 ? `Launch ${count} accounts` : 'Launch account');
  const prog = $('#launch-progress');
  prog.classList.toggle('hidden', !S.launching);
  setText($('span', prog), S.launchProgress || 'Starting…');
  syncCfgInputs($('#page-launch'));
}
$('#favs').addEventListener('click', (e) => {
  const x = e.target.closest('[data-x]');
  if (x) { e.stopPropagation(); return send('removeFavorite', { index: Number(x.dataset.x) }); }
  const f = e.target.closest('.fav');
  if (f) { $('#link').value = S.favorites[Number(f.dataset.i)].link; requestParse(); }
});
$('#recent-list').addEventListener('click', (e) => {
  const r = e.target.closest('[data-place]');
  if (r) { $('#link').value = `https://www.roblox.com/games/${r.dataset.place}/`; requestParse(); toast('Game filled in — pick accounts and launch.', 'info'); }
});
$('#friends-list').addEventListener('click', (e) => {
  const j = e.target.closest('[data-join]');
  if (!j) return;
  const ids = [...selected].filter((id) => S.accounts.some((a) => a.id === id));
  if (!ids.length) return toast('Tick the accounts that should join (step 2) first.', 'warn');
  send('friendJoin', { userId: j.dataset.join, accounts: ids });
});
$('#friends-refresh').addEventListener('click', () => { send('refreshFriends'); toast('Checking friends…', 'info'); });
$('#pick-list').addEventListener('click', (e) => {
  const p = e.target.closest('.pick');
  if (!p) return;
  const id = p.dataset.id;
  if (selected.has(id)) selected.delete(id); else selected.add(id);
  renderLaunch();
});
$('#fav-save').addEventListener('click', async () => {
  const link = $('#link').value.trim();
  if (!link) return toast('Paste a game link first.', 'warn');
  const parsedText = $('#parsed').dataset.ok === '1' ? $('#parsed').textContent.trim() : '';
  const name = await modal({ title: 'Save to favorites', text: 'Give this game or server a name you\'ll recognise.', input: { value: parsedText, placeholder: 'e.g. My private server' }, ok: 'Save' });
  if (name !== null) send('saveFavorite', { link, name });
});
$('#launch-btn').addEventListener('click', () => {
  const link = $('#link').value.trim();
  if (!link) return toast('Paste a game link first.', 'warn');
  send('launch', { link, accounts: [...selected] });
});
$('#preset-from-launch').addEventListener('click', async () => {
  const link = $('#link').value.trim();
  const ids = [...selected].filter((id) => S.accounts.some((a) => a.id === id));
  if (!link || !ids.length) return toast('Pick a game and at least one account first.', 'warn');
  const name = await modal({
    title: 'Save as preset',
    text: 'Saves this game and these accounts, with each account\'s FPS and macro as they are right now.',
    input: { value: suggestPresetName(link), placeholder: 'e.g. Overnight AFK' }, ok: 'Save preset',
  });
  if (name !== null) send('presetSave', { link, accounts: ids, name });
});

// ---------------------------------------------------------------- render: presets
/** The game's name for a link, if a favorite or a recent game knows it. */
function suggestPresetName(link) {
  const fav = S.favorites.find((f) => f.link === link);
  if (fav) return fav.game || fav.name;
  const m = /games\/(\d+)/.exec(link);
  const r = m && S.recent.find((x) => x.placeId === m[1]);
  return (r && r.game) || ($('#parsed').dataset.ok === '1' ? $('#parsed').textContent.trim() : '');
}

function renderPresets() {
  const list = S.presets || [];
  $('#preset-empty').classList.toggle('hidden', list.length > 0);
  const grid = $('#preset-grid');
  grid.classList.toggle('hidden', !list.length);
  setHtml(grid, list.map((p) => `
    <div class="preset" data-id="${esc(p.id)}">
      <div class="ph">
        <div class="pi">${p.icon ? `<img src="${esc(p.icon)}" alt="">` : icon('grid')}</div>
        <div class="pt"><b>${esc(p.name)}</b><span>${esc([p.game, p.lastUsed ? `used ${p.lastUsed}` : 'never used'].filter(Boolean).join(' · '))}</span></div>
        <button class="btn icon sm star ${p.favorite ? 'on' : ''}" data-p="fav" title="${p.favorite ? 'Unpin from Home' : 'Pin to Home'}">${icon('star')}</button>
      </div>
      <div class="pacc">${p.accounts.map((a) => `
        <div class="pa ${a.missing ? 'missing' : ''}">
          <div class="avatar sm">${avatarHtml(a.avatar, a.name)}</div>
          <b>${esc(a.name)}</b>
          <span class="badge accent">${a.fps > 0 ? `${a.fps} FPS` : 'AFK FPS'}${a.low ? ' · low gfx' : ''}</span>
          <span class="muted small pm">${a.macro ? `${p.afk && a.run ? '▶ ' : ''}${esc(a.macro)}` : 'Idle'}</span>
        </div>`).join('')}
      </div>
      <label class="loop pafk" title="On: AFK mode, anti-AFK, rejoining, alerts and the macros marked ▶. Off: just opens the windows so you can play."><label class="switch"><input type="checkbox" data-p="afk" ${p.afk ? 'checked' : ''}><span></span></label>${p.afk ? 'AFK mode, rejoins and macros' : 'Just open the windows'}</label>
      <div class="row">
        <button class="btn primary sm" data-p="launch" ${S.launching ? 'disabled' : ''}>${icon('play')}Launch</button>
        <button class="btn sm" data-p="update" title="Replace this preset with your current setup (your last launch, with today's FPS and macros)">${icon('refresh')}Update</button>
        <span style="flex:1"></span>
        <button class="btn icon sm" data-p="rename" title="Rename">${icon('pencil')}</button>
        <button class="btn icon sm danger" data-p="delete" title="Delete">${icon('trash')}</button>
      </div>
    </div>`).join(''));
}
$('#preset-grid').addEventListener('click', async (e) => {
  const b = e.target.closest('[data-p]');
  if (!b) return;
  const id = b.closest('.preset').dataset.id;
  const p = (S.presets || []).find((x) => x.id === id);
  if (!p) return;
  const act = b.dataset.p;
  if (act === 'launch') send('presetLaunch', { id });
  else if (act === 'fav') send('presetFavorite', { id });
  else if (act === 'afk') send('presetAfk', { id });
  else if (act === 'update') {
    if (await modal({ title: `Update "${p.name}"?`, text: 'It will use your last launch: that game, those accounts, and each one\'s current FPS and macro.', ok: 'Update' })) send('presetUpdate', { id });
  } else if (act === 'rename') {
    const name = await modal({ title: 'Rename preset', input: { value: p.name }, ok: 'Save' });
    if (name && name.trim()) send('presetRename', { id, name: name.trim() });
  } else if (act === 'delete') {
    if (await modal({ title: `Delete "${p.name}"?`, text: 'Your accounts and macros stay as they are.', ok: 'Delete', danger: true })) send('presetDelete', { id });
  }
});
$('#preset-save-current').addEventListener('click', async () => {
  if (!S.cfg.lastLink || !S.cfg.lastAccounts.length) return toast('Launch a game once first, then save it here.', 'warn');
  const name = await modal({
    title: 'Save current setup',
    text: 'Saves your last launch (the game and accounts) with each account\'s FPS and macro as they are right now.',
    input: { value: suggestPresetName(S.cfg.lastLink), placeholder: 'e.g. Overnight AFK' }, ok: 'Save preset',
  });
  if (name !== null) send('presetSave', { fromLast: true, name });
});

// ---------------------------------------------------------------- render: accounts
const FPS_CHOICES = [0, 1, 3, 5, 10, 15, 20, 30, 45, 60, 120];
function renderAccounts() {
  const grid = $('#acct-grid');
  $('#acct-empty').classList.toggle('hidden', S.accounts.length > 0);
  const keep = new Set(S.accounts.map((a) => a.id));
  $$('.acct', grid).forEach((el) => { if (!keep.has(el.dataset.id)) el.remove(); });
  S.accounts.forEach((a) => {
    let el = $(`.acct[data-id="${a.id}"]`, grid);
    if (!el) {
      el = document.createElement('div');
      el.className = 'card acct';
      el.dataset.id = a.id;
      el.innerHTML = `
        <div class="top"><div class="avatar lg"></div><div class="info"><b></b><span class="muted small"></span><div class="presence hidden"></div></div>
          <div class="stack" style="gap:6px;align-items:flex-end"><span class="badge st"></span><span class="badge main is-main hidden">${icon('star')}Main</span></div></div>
        <div class="opts">
          <div class="setting-line"><div class="txt"><b>Opens at</b><span>The FPS this account's window starts with.</span></div>
            <div class="ctl"><select class="input" data-f="fps"></select></div></div>
          <div class="setting-line"><div class="txt"><b>Low graphics</b><span>Roblox's lowest quality — saves the most power.</span></div>
            <div class="ctl"><label class="switch"><input type="checkbox" data-f="low"><span></span></label></div></div>
        </div>
        <div class="bottom"><span class="added"></span><div class="row" style="gap:4px"><button class="btn ghost sm mk-main" data-f="main" title="Greet me as this account on Home, and color the 'Your avatar' theme from it">${icon('star')}Make main</button><button class="btn ghost sm danger" data-f="remove">${icon('trash')}Remove</button></div></div>`;
      grid.appendChild(el);
    }
    setHtml($('.avatar', el), avatarHtml(a.avatar, a.display || a.name));
    setText($('.top b', el), a.display || a.name);
    setText($('.top .info span', el), `@${a.name} · ID ${a.id}`);
    const st = $('.st', el);
    st.className = `badge st ${a.statusKind}`;
    setText(st, a.status);
    const sel = $('[data-f="fps"]', el);
    const choices = FPS_CHOICES.includes(a.fps) ? FPS_CHOICES : [...FPS_CHOICES, a.fps].sort((x, y) => x - y);
    const opts = choices.map((v) => `<option value="${v}">${v ? `${v} FPS` : `AFK FPS (${S.cfg.afkFps})`}</option>`).join('');
    if (notFocused(sel)) { setHtml(sel, opts); sel.value = String(a.fps); }
    const low = $('[data-f="low"]', el);
    if (low.checked !== a.low) low.checked = a.low;
    setText($('.added', el), `Added ${a.added}`);
    const pres = $('.presence', el);
    pres.classList.toggle('hidden', !a.presence);
    if (a.presence) setHtml(pres, `${a.presence.icon ? `<img src="${esc(a.presence.icon)}" alt="">` : `<span class="dot ${a.presence.kind === 'good' ? 'good' : ''}"></span>`}${esc(a.presence.text)}`);
    $('.mk-main', el).classList.toggle('hidden', a.main || S.accounts.length < 2);
    $('.is-main', el).classList.toggle('hidden', !a.main || S.accounts.length < 2);
  });
}
$('#acct-grid').addEventListener('change', (e) => {
  const card = e.target.closest('.acct');
  if (!card) return;
  if (e.target.dataset.f === 'fps') send('setAccountFps', { id: card.dataset.id, fps: Number(e.target.value) });
  if (e.target.dataset.f === 'low') send('setAccountLow', { id: card.dataset.id, on: e.target.checked });
});
$('#acct-grid').addEventListener('click', async (e) => {
  const mk = e.target.closest('[data-f="main"]');
  if (mk) return send('setConfig', { key: 'mainAccount', value: mk.closest('.acct').dataset.id });
  const rm = e.target.closest('[data-f="remove"]');
  if (!rm) return;
  const id = rm.closest('.acct').dataset.id;
  const a = S.accounts.find((x) => x.id === id);
  const ok = await modal({ title: `Remove ${a ? a.display || a.name : 'account'}?`, text: 'Its saved login is deleted from this PC. You can add it again any time.', ok: 'Remove', danger: true });
  if (ok) send('removeAccount', { id });
});
$('#acct-add').addEventListener('click', () => send('addLogin'));
$('#acct-check').addEventListener('click', () => send('checkLogins'));
$('#acct-cookie').addEventListener('click', async () => {
  const cookie = await modal({
    title: 'Add account with cookie',
    text: 'Paste the account\'s <b>.ROBLOSECURITY</b> cookie. Only do this for your own accounts — it\'s full access to the account, so never paste it anywhere else. "Add account" is easier and you never see the cookie.',
    extra: `<details class="how-fold"><summary>How do I find my cookie?</summary><ol class="how">
      <li>Open a <b>private / incognito window</b> in Chrome, Edge or Brave (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>N</kbd>) and log in to <b>roblox.com</b> with that account.</li>
      <li>Press <kbd>F12</kbd> to open Developer Tools, then click the <b>Application</b> tab (if you can't see it, click <b>»</b>). In Firefox it's the <b>Storage</b> tab.</li>
      <li>On the left: <b>Cookies → https://www.roblox.com</b>.</li>
      <li>Find <b>.ROBLOSECURITY</b>, double-click its <b>Value</b>, press <kbd>Ctrl</kbd>+<kbd>A</kbd> then <kbd>Ctrl</kbd>+<kbd>C</kbd>, and paste it below.</li>
      <li><b>Close</b> the private window — don't press Log out, because logging out makes the cookie stop working.</li>
    </ol></details>`,
    input: { textarea: true, placeholder: '_|WARNING:-DO-NOT-SHARE-THIS…' }, ok: 'Add account',
  });
  if (cookie && cookie.trim()) send('addCookie', { cookie: cookie.trim() });
});

// ---------------------------------------------------------------- render: stats
const shortName = (a) => a.replace(/ \(@[^)]*\)$/, '');
function sessionAccounts(list, inGame, seconds) {
  // Newer sessions know how long each account was really in game; one that was out for a big
  // part of the session is shown in red, so a window that died overnight can't hide.
  const ig = inGame && Object.keys(inGame).length ? inGame : null;
  if (ig) {
    return list.map((a) => {
      const t = ig[a] || 0, short = seconds > 600 && t < seconds * 0.9;
      return `<span class="${short ? 'acct-short' : ''}" title="In game ${dur(t)} of ${dur(seconds)}">${esc(shortName(a))} <span class="faint">${dur(t)}</span></span>`;
    }).join(', ') || '—';
  }
  const unnamed = /^(Background )?Roblox \(PID \d+\)$/;
  const named = list.filter((a) => !unnamed.test(a)).map(shortName);
  const extra = list.length - named.length;
  return esc(named.join(', ') || (extra ? '' : '—')) + (extra ? `<span class="faint">${named.length ? ' + ' : ''}${extra} unnamed</span>` : '');
}

function renderStats() {
  if (!S) return;
  // Older versions kept sessions that were just a quick Start/Stop click; leave those out.
  const past = SESSIONS.filter((s) => s.seconds >= 60);
  const all = S.session ? [S.session, ...past] : past;
  const total = all.reduce((a, s) => a + s.seconds, 0);
  setText($('#tot-time'), total ? dur(total) : '0m');
  setText($('#tot-sessions'), fmtInt(all.length));
  const longest = all.reduce((a, s) => Math.max(a, s.seconds), 0);
  setText($('#tot-sessions-foot'), longest ? `Longest: ${dur(longest)}` : 'None yet');
  setText($('#tot-presses'), fmtInt(all.reduce((a, s) => a + s.presses, 0)));
  const disc = all.reduce((a, s) => a + s.disconnects, 0), rej = all.reduce((a, s) => a + s.rejoins, 0);
  setText($('#tot-disc'), fmtInt(disc));
  setText($('#tot-disc-foot'), rej ? `${rej} auto-rejoined` : disc ? 'None auto-rejoined' : 'Clean record');

  const days = dailyBuckets(14);
  const label = (d) => d.date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  axisChart($('#chart-hours'), days.map((d) => ({ value: d.seconds / 3600, label: label(d), title: `${label(d)}: ${dur(d.seconds)}` })),
    { kind: 'bar', format: (v) => (v >= 10 ? v.toFixed(0) : v.toFixed(1)) + 'h' });
  axisChart($('#chart-presses'), days.map((d) => ({ value: d.presses, label: label(d), title: `${label(d)}: ${d.presses} presses` })),
    { kind: 'line', format: (v) => (v >= 10 ? v.toFixed(0) : v.toFixed(1).replace(/\.0$/, '')) });

  $('#sess-empty').classList.toggle('hidden', past.length > 0);
  const body = $('#sess-body');
  const key = SESSIONS.length;
  if (body._key !== key) {
    body._key = key;
    body.innerHTML = past.map((s) => {
      const d = new Date(s.start);
      return `<tr><td>${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} · ${d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}</td>
        <td class="num">${dur(s.seconds)}</td><td>${sessionAccounts(s.accounts, s.inGame, s.seconds)}</td>
        <td class="num">${s.presses}</td><td class="num">${s.disconnects ? `<span class="badge bad">${s.disconnects}</span>` : '0'}</td><td class="num">${s.rejoins}</td></tr>`;
    }).join('');
  }
}
window.addEventListener('resize', () => { if (page === 'stats') renderStats(); });

// ---------------------------------------------------------------- render: macros
const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];
const TURNS = [1, 2, 3, 5, 10, 20];
/** Macros made for the game this window is in come first. */
function macroOptions(w) {
  const opt = (m) => `<option value="${m.id}">${esc(m.name)}</option>`;
  const here = w && w.placeId !== '0' ? S.macros.filter((m) => m.placeId === w.placeId) : [];
  const rest = S.macros.filter((m) => !here.includes(m));
  return '<option value="">Idle — anti-AFK only</option>' + (here.length
    ? `<optgroup label="For ${esc(here[0].game || 'this game')}">${here.map(opt).join('')}</optgroup>${rest.length ? `<optgroup label="Other macros">${rest.map(opt).join('')}</optgroup>` : ''}`
    : rest.map(opt).join(''));
}

function renderMacros() {
  const mac = S.macro;
  // hero
  const st = mac.state;
  const kind = { Playing: 'playing', Yielding: 'playing', TurnDone: 'playing', Yielded: 'waiting', PausedUser: 'paused', WaitingWindow: 'waiting', WaitingFocus: 'paused' }[st] || '';
  const ic = $('#macro-icon');
  ic.className = `hero-icon ${kind}`;
  setHtml(ic, icon({ playing: 'play', paused: 'pause', waiting: 'clock' }[kind] || 'macro'));
  const titles = {
    Off: 'No macro running', Playing: mac.running > 1 ? `${mac.running} windows taking turns` : 'Macro running', TurnDone: 'Switching windows', Yielding: 'Macro running', Yielded: 'Anti-AFK is checking the other windows',
    PausedUser: 'Paused — you\'re using the PC', WaitingWindow: 'Waiting for the window', WaitingFocus: 'Waiting for the window', Finished: 'Macro finished',
  };
  setText($('#macro-title'), titles[st] || st);
  let sub = mac.text;
  if ((st === 'Playing' || st === 'Yielding') && mac.running <= 1) sub += ` Loop ${mac.loops + 1} · running ${dur(mac.runSeconds)}.`;
  if (mac.problem && (st === 'WaitingFocus')) sub += ` ${mac.problem}`;
  setText($('#macro-sub'), sub);
  const actions = [];
  if (st === 'PausedUser') actions.push(`<button class="btn primary sm" data-mac="resume">${icon('play')}Resume</button>`);
  if (st !== 'Off') actions.push(`<button class="btn sm" data-mac="stop" title="Stops every window's macro. Or press F8 anywhere.">${icon('stop')}${mac.running > 1 ? 'Stop all' : 'Stop'} <kbd>F8</kbd></button>`);
  setHtml($('#macro-actions'), actions.join(''));
  $('#macro-explain').classList.toggle('hidden', st !== 'Off');

  // recording banner
  const rec = mac.recording;
  $('#record-banner').classList.toggle('hidden', !rec);
  if (rec) {
    setText($('#rec-title'), rec === 'countdown' ? `Get ready — recording in ${mac.recordWindow || 'the window'} starts in a moment…` : `Recording in ${mac.recordWindow}`);
    setText($('#rec-sub'), rec === 'countdown' ? 'Wait for the third beep, then do the moves you want looped.' : `${mac.recordSeconds} s · ${mac.recordEvents} events so far. Press F8 when you're done.`);
  }
  $('#macro-record').disabled = !!rec || !S.windows.length;
  $('#macro-record').title = S.windows.length ? 'Record your moves in a Roblox window' : 'Open a Roblox window first';

  // windows
  const list = $('#mwin-list');
  $('#mwin-empty').classList.toggle('hidden', S.windows.length > 0);
  const keep = new Set(S.windows.map((w) => String(w.pid)));
  $$('.mwin', list).forEach((el) => { if (!keep.has(el.dataset.pid)) el.remove(); });
  const libKey = S.macros.map((m) => m.id + m.name + m.placeId).join('|');
  S.windows.forEach((w, i) => {
    const optsKey = libKey + '@' + w.placeId;
    let el = $(`.mwin[data-pid="${w.pid}"]`, list);
    if (!el) {
      el = document.createElement('div');
      el.className = 'mwin';
      el.dataset.pid = w.pid;
      el.innerHTML = `
        <div class="who"><div class="avatar"></div><div style="min-width:0"><div class="name"></div><div class="meta"></div></div></div>
        <select class="input" data-m="macro" title="What this window does"></select>
        <select class="input" data-m="speed" title="Playback speed">${SPEEDS.map((s) => `<option value="${s}">${s}×</option>`).join('')}</select>
        <select class="input" data-m="turn" title="When several windows run macros: how many loops this one plays before the next window's turn">${TURNS.map((n) => `<option value="${n}">${n} loop${n === 1 ? '' : 's'}/turn</option>`).join('')}</select>
        <label class="loop" title="Play the macro over and over"><label class="switch"><input type="checkbox" data-m="loop"><span></span></label>Loop</label>
        <button class="btn sm" data-m="go"></button>
        <div class="turn hidden"></div>
        <div class="warnline hidden">${icon('alert')}<span></span></div>`;
      list.appendChild(el);
    }
    if (list.children[i] !== el) list.insertBefore(el, list.children[i]);
    setHtml($('.avatar', el), avatarHtml(w.avatar, w.name));
    setText($('.name', el), w.name);
    setText($('.meta', el), `${w.effectiveFps} FPS · ${w.status}`);
    const sel = $('[data-m="macro"]', el);
    if (sel._key !== optsKey && notFocused(sel)) { sel.innerHTML = macroOptions(w); sel._key = optsKey; }
    if (notFocused(sel)) sel.value = w.macro ? w.macro.id : '';
    const speed = $('[data-m="speed"]', el);
    if (notFocused(speed)) speed.value = String(w.macro ? w.macro.speed : 1);
    speed.disabled = !w.macro;
    const loop = $('[data-m="loop"]', el);
    loop.checked = w.macro ? w.macro.loop : true;
    loop.disabled = !w.macro;
    const turnSel = $('[data-m="turn"]', el);
    if (notFocused(turnSel)) turnSel.value = String(w.macro ? w.macro.loopsPerTurn : 1);
    turnSel.disabled = !w.macro;
    const running = !!(w.macro && w.macro.running);
    const active = !!(w.macro && w.macro.active);
    el.classList.toggle('running', running);
    el.classList.toggle('active', active);
    const go = $('[data-m="go"]', el);
    go.disabled = !w.macro || !!rec;
    go.className = `btn sm ${running ? 'danger' : w.macro ? 'primary' : ''}`;
    setHtml(go, running ? `${icon('stop')}Stop` : `${icon('play')}${mac.running ? 'Add' : 'Start'}`);
    go.title = !w.macro ? 'Pick a macro first' : running ? 'Stop this window\'s macro'
      : mac.running ? 'Add this window to the rotation — it plays when it\'s its turn' : 'Bring this window to the front and start the macro';
    const turn = $('.turn', el);
    turn.classList.toggle('hidden', !running);
    if (running) {
      const label = active ? `<span class="badge accent">▶ Playing now</span>`
        : w.pid === mac.nextPid ? '<span class="badge">Up next</span>' : '<span class="badge">Waiting its turn</span>';
      setHtml(turn, `${label}<span>${w.macro.totalLoops} loop${w.macro.totalLoops === 1 ? '' : 's'} played${mac.running > 1 ? ` · ${w.macro.loopsPerTurn} per turn` : ''}</span>`);
    }
    const warn = $('.warnline', el);
    const low = w.macro && w.effectiveFps < 20;
    warn.classList.toggle('hidden', !low);
    if (low) setText($('span', warn), `${w.effectiveFps} FPS is too low for a macro — inputs can get missed. Set this account to 30 FPS (Accounts → Opens at) and relaunch it.`);
  });

  // library
  const lib = $('#macro-lib');
  $('#macro-lib-empty').classList.toggle('hidden', S.macros.length > 0);
  setHtml(lib, S.macros.map((m) => `
    <div class="mrow" data-id="${m.id}">
      <div class="mi">${m.gameIcon ? `<img src="${esc(m.gameIcon)}" alt="">` : icon(m.source === 'tinytask' ? 'file' : 'macro')}</div>
      <div class="info"><b>${esc(m.name)}</b><span>${m.game ? `${esc(m.game)} · ` : ''}${m.seconds} s loop · ${m.actions} actions · added ${esc(m.created)}</span></div>
      <div class="badges">
        <button class="badge game-tag" data-lib="game" title="Which game this macro is for — it's listed first for windows in that game">${m.game ? esc(m.game) : 'No game'} ▾</button>
        <span class="badge">${m.source === 'tinytask' ? 'TinyTask import' : 'Recorded'}</span>
        ${m.coords === 'window' ? '<span class="badge good" title="Clicks are relative to the window, so it can move">Follows window</span>'
          : '<span class="badge warn" title="Clicks use exact screen positions — the window must be where it was when recorded">Fixed screen spot</span>'}
      </div>
      <div class="row">
        ${m.coords !== 'window' ? `<button class="btn sm" data-lib="fit" title="Make the clicks follow a window instead of fixed screen positions">${icon('fit')}Fit to window</button>` : ''}
        <button class="btn icon sm" data-lib="rename" title="Rename">${icon('pencil')}</button>
        <button class="btn icon sm danger" data-lib="delete" title="Delete">${icon('trash')}</button>
      </div>
    </div>`).join(''));
  syncCfgInputs($('#page-macros'));
}

function assignFrom(row) {
  send('macroAssign', {
    pid: Number(row.dataset.pid),
    macroId: $('[data-m="macro"]', row).value,
    speed: Number($('[data-m="speed"]', row).value),
    loop: $('[data-m="loop"]', row).checked,
    loopsPerTurn: Number($('[data-m="turn"]', row).value),
  });
}
$('#mwin-list').addEventListener('change', (e) => {
  const row = e.target.closest('.mwin');
  if (row && e.target.dataset.m) assignFrom(row);
});
$('#mwin-list').addEventListener('click', (e) => {
  const go = e.target.closest('[data-m="go"]');
  if (!go) return;
  const pid = Number(go.closest('.mwin').dataset.pid);
  const w = S.windows.find((x) => x.pid === pid);
  if (w && w.macro && w.macro.running) send('macroStop', { pid });
  else {
    send('macroStart', { pid });
    toast(S.macro.running ? `${w.name} joined the rotation — it plays when it's its turn.`
      : 'Starting — take your hand off the mouse. Moving it or typing pauses the macro; F8 stops it.', 'info');
  }
});
$('#macro-actions').addEventListener('click', (e) => {
  const b = e.target.closest('[data-mac]');
  if (b) send(b.dataset.mac === 'resume' ? 'macroResume' : 'macroStop');
});
$('#rec-stop').addEventListener('click', () => send('macroRecordStop'));
$('#macro-import').addEventListener('click', () => send('macroImport'));
const windowChoices = () => S.windows.map((w) => ({ value: String(w.pid), label: `${w.name} (${w.effectiveFps} FPS)` }));
$('#macro-record').addEventListener('click', async () => {
  if (!S.windows.length) return toast('Open a Roblox window first.', 'warn');
  const pid = await modal({
    title: 'Record a new macro',
    text: 'Pick the window to record in. When you press <b>Start recording</b>, it comes to the front and you\'ll hear three beeps — then do the moves you want looped. Press <kbd>F8</kbd> when you\'re done. Only keys and clicks inside that window are recorded.',
    input: { select: windowChoices() }, ok: 'Start recording',
  });
  if (pid) send('macroRecord', { pid: Number(pid) });
});
$('#macro-lib').addEventListener('click', async (e) => {
  const b = e.target.closest('[data-lib]');
  if (!b) return;
  const id = b.closest('.mrow').dataset.id;
  const m = S.macros.find((x) => x.id === id);
  if (!m) return;
  if (b.dataset.lib === 'rename') {
    const name = await modal({ title: 'Rename macro', input: { value: m.name }, ok: 'Save' });
    if (name && name.trim()) send('macroRename', { id, name: name.trim() });
  } else if (b.dataset.lib === 'game') {
    // Games your windows have joined, plus the one it's tied to now.
    const games = S.recent.filter((r) => r.game).map((r) => ({ value: r.placeId, label: r.game }));
    if (m.placeId !== '0' && !games.some((g) => g.value === m.placeId)) games.unshift({ value: m.placeId, label: m.game || `Place ${m.placeId}` });
    const placeId = await modal({
      title: `Which game is "${m.name}" for?`,
      text: 'It\'s listed first when you pick a macro for a window in that game.',
      input: { select: [...games, { value: '0', label: 'No particular game' }] }, ok: 'Save',
    });
    if (placeId !== null) send('macroSetGame', { id, placeId });
  } else if (b.dataset.lib === 'delete') {
    if (await modal({ title: `Delete "${m.name}"?`, text: 'Any window using it goes back to idle.', ok: 'Delete', danger: true })) send('macroDelete', { id });
  } else if (b.dataset.lib === 'fit') {
    if (!S.windows.length) return toast('Open the Roblox window it was recorded in first, in the same spot as when you recorded it.', 'warn');
    const pid = await modal({
      title: 'Fit to a window',
      text: 'TinyTask saved exact screen positions. Put the Roblox window <b>exactly where it was when you recorded</b>, pick it below, and the clicks will follow that window from then on — even if it moves.',
      input: { select: windowChoices() }, ok: 'Fit to this window',
    });
    if (pid) send('macroFit', { id, pid: Number(pid) });
  }
});

async function onRecorded(m) {
  go('macros');
  const name = await modal({
    title: 'Name your macro',
    text: `Recorded ${m.actions} actions in a ${m.seconds} s loop${m.game ? ` in ${esc(m.game)}` : ''}. Keep the name or type your own, then pick it for a window below and press Start.`,
    input: { value: m.name, placeholder: 'e.g. Move & attack loop' }, ok: 'Save',
  });
  if (name && name.trim() && name.trim() !== m.name) send('macroRename', { id: m.id, name: name.trim() });
}

function onImported(m) {
  go('macros');
  toast(m.coords === 'window' ? `Imported "${m.name}".`
    : `Imported "${m.name}". Its clicks use fixed screen spots — use "Fit to window" so they follow the Roblox window.`, 'good');
}

// ---------------------------------------------------------------- render: settings
function syncCfgInputs(root) {
  $$('[data-cfg]', root).forEach((el) => {
    if (!notFocused(el)) return;
    const v = S.cfg[el.dataset.cfg];
    if (el.type === 'checkbox') { if (el.checked !== !!v) el.checked = !!v; }
    else if (el.value !== String(v)) el.value = v;
  });
}
function renderCustomize() {
  const ma = $('#main-account');
  const maKey = S.accounts.map((a) => a.id + (a.display || a.name)).join('|');
  if (ma._key !== maKey && notFocused(ma)) {
    ma._key = maKey;
    ma.innerHTML = S.accounts.length ? S.accounts.map((a) => `<option value="${a.id}">${esc(a.display || a.name)}</option>`).join('') : '<option value="0">No accounts yet</option>';
  }
  syncCfgInputs($('#page-customize'));
  setText($('#discord-status'), S.cfg.liteMode && S.cfg.discordRpc ? 'Paused by Lite mode.' : S.discordStatus || 'Off');
}
function renderSettings() {
  const ks = $('#key-select');
  if (!ks.options.length) ks.innerHTML = S.keys.map((k) => `<option value="${k.id}">${esc(k.name)}</option>`).join('');
  syncCfgInputs($('#page-settings'));
  const auto = $('#autostart');
  if (auto.checked !== S.autostart) auto.checked = S.autostart;
  setText($('#multi-detail'), {
    off: 'Off — opening a second Roblox will close the first.',
    ready: 'On. No Roblox running right now.',
    unlocked: `On. ${S.multiCount} Roblox process${S.multiCount === 1 ? '' : 'es'} unlocked.`,
    unlocking: 'On. Unlocking a new window…',
    blocked: 'Windows blocked access to a Roblox window — try "Restart as admin".',
  }[S.multi] || '');
  $('#admin-line').classList.toggle('hidden', S.admin);
  setText($('#webhook-detail'), S.webhookSet ? 'Saved ✓ Paste a new link to replace it, or save it empty to remove it.' : 'Not set up yet.');
  setText($('#schedule-detail'), S.cfg.scheduleOn
    ? `On: ${S.cfg.scheduleStart} to ${S.cfg.scheduleStop} every day (the app has to be running).`
    : 'AFK mode on and off at set times, every day.');
  const mp = $('#make-portable');
  mp.disabled = S.portable;
  setHtml(mp, S.portable ? `${icon('check')}Portable` : `${icon('folder')}Make portable`);
  setText($('#portable-detail'), S.portable
    ? `On: data is in ${S.dataDir}. Copy it with the app. Logins only work on the PC they were added on.`
    : 'Keep all data in a folder next to the app.');
}

// Collapsible sections remember whether you left them open (per PC; falls back to the defaults).
$$('details.fold').forEach((d) => {
  try { const v = localStorage.getItem(`fold:${d.id}`); if (v !== null) d.open = v === '1'; } catch (_) {}
  d.addEventListener('toggle', () => { try { localStorage.setItem(`fold:${d.id}`, d.open ? '1' : '0'); } catch (_) {} });
});
document.addEventListener('change', (e) => {
  const el = e.target;
  if (!el.dataset || !el.dataset.cfg) return;
  const key = el.dataset.cfg;
  const value = el.type === 'checkbox' ? el.checked : el.type === 'number' ? Number(el.value) : el.value;
  if (key === 'reduceEffects' && S) applyTheme(S.cfg.theme, value || S.cfg.liteMode);
  if (key === 'liteMode' && S) { applyTheme(S.cfg.theme, value || S.cfg.reduceEffects); document.body.classList.toggle('lite-on', value); }
  send('setConfig', { key, value });
});
$('#autostart').addEventListener('change', (e) => send('setAutostart', { on: e.target.checked }));
$('#restart-admin').addEventListener('click', () => send('restartAdmin'));
$('#open-data').addEventListener('click', () => send('openDataFolder'));
$('#misc-open-data').addEventListener('click', () => send('openDataFolder'));

function renderMisc() {
  const up = S.update || {};
  setText($('#misc-version'), `Roblox AFK Hub v${S.version}`);
  if (up.status) setText($('#update-status'), up.status);
  const inst = $('#update-install');
  inst.classList.toggle('hidden', !up.available);
  inst.disabled = !!up.busy;
  setText(inst, up.busy ? 'Working…' : `Update to v${up.available || ''}`);
  $('#update-check').disabled = !!up.busy;
  syncCfgInputs($('#page-misc'));
}
async function askToUpdate() {
  const up = S.update || {};
  if (!up.available) return;
  const notes = (up.notes || '').trim();
  const r = await modal({
    title: `Update to v${up.available}?`,
    text: `You're on v${esc(S.version)}. The app downloads the new version, checks it's the official file, swaps it in and reopens. Your accounts, settings, macros and taskbar pin stay as they are.`
      + (S.afk ? '<br><br><b>Finish AFKing first</b> (press "I\'m back"): updating restarts the app.' : ''),
    extra: notes ? `<details class="how-fold"><summary>What's new</summary><pre class="notes">${esc(notes.slice(0, 2000))}</pre></details>` : '',
    ok: 'Update now',
  });
  if (r) send('updateInstall');
  else send('updateLater');
}
$('#chip-update').addEventListener('click', askToUpdate);
$('#side-version').addEventListener('click', () => ((S.update || {}).available ? askToUpdate() : send('updateCheck')));
$('#update-install').addEventListener('click', askToUpdate);
$('#update-check').addEventListener('click', () => send('updateCheck'));
$('#open-activity').addEventListener('click', () => send('openActivityLog'));
$('#open-settings-file').addEventListener('click', () => send('openSettingsFile'));
$('#resume-cancel').addEventListener('click', () => send('cancelResume'));
$('#webhook-save').addEventListener('click', () => {
  const inp = $('#webhook-input');
  send('setConfig', { key: 'webhookUrl', value: inp.value.trim() });
  inp.value = '';
});
$('#webhook-input').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('#webhook-save').click(); });
$('#webhook-test').addEventListener('click', () => send('testWebhook'));
$('#make-portable').addEventListener('click', async () => {
  const ok = await modal({
    title: 'Make Roblox AFK Hub portable?',
    text: 'Your settings, favorites, macros and stats get copied into an <b>AfkHub-data</b> folder next to AfkHub.exe, and the app restarts to use it. Then copy AfkHub.exe <b>and</b> that folder to a USB stick. Saved logins only work on the PC they were added on — on another PC, just add the accounts again.',
    ok: 'Make portable',
  });
  if (ok) send('makePortable');
});
$('#mode-card').addEventListener('click', (e) => {
  const b = e.target.closest('[data-mode]');
  if (b) send('chooseMode', { mode: b.dataset.mode });
});
$('#guide-dismiss').addEventListener('click', () => send('setConfig', { key: 'guideDismissed', value: true }));
$('#afk-btn').addEventListener('click', () => send('toggleAfk'));
$('#afk-close-btn').addEventListener('click', async () => {
  const n = S.windows.length;
  const ok = await modal({
    title: S.afk ? 'End AFK and close Roblox?' : 'Close all Roblox windows?',
    text: `${S.afk ? 'Ends the AFK session (like "I\'m back") and closes' : 'Closes'} ${n ? `all ${n} Roblox window${n === 1 ? '' : 's'}` : 'Roblox'}. Nothing gets rejoined.`,
    ok: 'Close all', danger: true,
  });
  if (ok) send('backAndClose');
});
$('#chip-antiafk').addEventListener('click', () => send('toggleAntiAfk'));

let pickerKey = '';
function drawThemePickers() {
  const key = themeAccent(THEMES[THEMES.length - 1]);
  if (key === pickerKey) return;
  pickerKey = key;
  setHtml($('#theme-dots'), THEMES.map((t) =>
    `<button class="theme-dot" data-t="${t.id}" title="${t.name}" style="background:linear-gradient(135deg, ${t.bg} 50%, ${themeAccent(t)} 50%)"></button>`).join(''));
  setHtml($('#theme-grid'), THEMES.map((t) => `
    <button class="theme-card" data-t="${t.id}" title="${t.id === 'avatar' ? 'Dark theme colored to match your main account\'s avatar' : t.name}">
      <div class="pv" style="background:${t.bg}">
        <i style="width:60%;background:${t.text};opacity:.85"></i>
        <i style="width:85%;background:${t.card}"></i>
        <i style="width:40%;background:${themeAccent(t)}"></i>
      </div>
      <span class="nm" style="background:${t.card};color:${t.text}">${t.name}</span>
    </button>`).join(''));
  if (S) applyTheme(S.cfg.theme, S.cfg.effectsOff);
}
function buildThemePickers() {
  drawThemePickers();
  document.addEventListener('click', (e) => {
    const t = e.target.closest('.theme-dot, .theme-card');
    if (!t) return;
    applyTheme(t.dataset.t, S && S.cfg.effectsOff);
    send('setConfig', { key: 'theme', value: t.dataset.t });
  });
}

// ---------------------------------------------------------------- demo data (for screenshots only)
function demoize(s) {
  const now = Date.now();
  s.session = { start: now - 5025000, seconds: 5025, presses: 9, disconnects: 0, rejoins: 0, accounts: ['Main', 'Alt'] };
  s.afk = true; s.multi = 'unlocked'; s.multiCount = 2;
  if (DEMO_THEME) s.cfg.theme = DEMO_THEME;
  s.windows = [
    { pid: 101, name: s.accounts[0] ? (s.accounts[0].display || s.accounts[0].name) : 'Main', avatar: s.accounts[0] && s.accounts[0].avatar, fromApp: true, status: 'In game', statusKind: 'good', multi: 'Unlocked', multiKind: 'good', fps: 30, low: false, antiAfkOn: true, next: 412, presses: 5, lastPress: '9m 12s ago', uptime: 5010 },
    { pid: 102, name: s.accounts[1] ? (s.accounts[1].display || s.accounts[1].name) : 'Alt', avatar: s.accounts[1] && s.accounts[1].avatar, fromApp: true, status: 'In game', statusKind: 'good', multi: 'Unlocked', multiKind: 'good', fps: 5, low: true, antiAfkOn: true, next: 77, presses: 4, lastPress: '13m 40s ago', uptime: 4960 },
  ];
  s.macros = [
    { id: 'm1', name: 'Farm loop', source: 'tinytask', coords: 'window', seconds: 13.3, actions: 112, created: 'Oct 29, 2026' },
    { id: 'm2', name: 'Spin + jump', source: 'recorded', coords: 'window', seconds: 6.2, actions: 18, created: 'Oct 29, 2026' },
  ];
  s.windows[0].macro = { id: 'm1', name: s.macros[0].name, speed: 1, loop: true, running: true, active: true, loopsPerTurn: 3, totalLoops: 41 };
  s.windows[1].macro = { id: 'm2', name: s.macros[1].name, speed: 1, loop: true, running: true, active: false, loopsPerTurn: 2, totalLoops: 27 };
  s.windows[0].effectiveFps = 30; s.windows[1].effectiveFps = 5;
  s.windows[0].next = null; s.windows[0].nextLabel = 'Kept active by its macro';
  s.macro = { state: 'Playing', running: 2, nowPid: 101, nextPid: 102, loops: 1, runSeconds: 2470, recording: null,
    text: `Taking turns: ${s.windows[0].name} is playing \u201c${s.macros[0].name}\u201d (loop 2 of 3), then ${s.windows[1].name}.` };
  s.windows[1].effectiveFps = 30;
  if (!SESSIONS.length) {
    SESSIONS = [3.2, 0, 5.5, 7.9, 2.1, 6.4, 8.8, 4.0, 0, 9.5, 6.1, 3.3, 7.2].map((h, i) => ({
      start: now - (i + 1) * 86400000, seconds: Math.round(h * 3600), presses: Math.round(h * 4), disconnects: i === 4 ? 1 : 0, rejoins: i === 4 ? 1 : 0, accounts: ['Main', 'Alt'],
    })).filter((x) => x.seconds);
  }
  return s;
}

// ---------------------------------------------------------------- main render + messages
function render() {
  if (!S) return;
  applyTheme(S.cfg.theme, S.cfg.effectsOff);
  document.body.classList.toggle('lite-on', !!S.cfg.liteMode);
  setText($('#lite-sub'), S.cfg.liteMode ? 'Extras paused' : 'Extras on');
  syncCfgInputs($('.side-foot'));
  drawThemePickers();
  renderTop();
  if (page === 'home') renderHome();
  else if (page === 'launch') renderLaunch();
  else if (page === 'presets') renderPresets();
  else if (page === 'macros') renderMacros();
  else if (page === 'accounts') renderAccounts();
  else if (page === 'stats') renderStats();
  else if (page === 'settings') renderSettings();
  else if (page === 'customize') renderCustomize();
  else if (page === 'misc') renderMisc();
}

if (host) {
  host.addEventListener('message', (e) => {
    const m = e.data;
    switch (m.type) {
      case 'init':
        setLogs(m.logs || []);
        if (m.page) go(m.page.toLowerCase());
        break;
      case 'state':
        S = DEMO ? demoize(m) : m;
        render();
        break;
      case 'stats':
        if (!DEMO || m.sessions.length) SESSIONS = m.sessions;
        if (page === 'stats' || page === 'home') render();
        break;
      case 'log': addLog(m.entry); break;
      case 'recorded': onRecorded(m.macro); break;
      case 'imported': onImported(m.macro); break;
      case 'toast': toast(m.text, m.kind); break;
      case 'parsed': {
        const p = $('#parsed');
        p.className = `parsed ${m.ok ? 'ok' : 'bad'}`;
        p.dataset.ok = m.ok ? '1' : '0';
        setHtml(p, `${icon(m.ok ? 'check' : 'info')}<span>${esc(m.text)}</span>`);
        break;
      }
    }
  });
}

hydrateIcons();
buildThemePickers();
go('home');
send('ready');
