// NovaShield HUD website. No tracking, no third-party scripts.
'use strict';

// ---- links: fill these in when the accounts exist; until then the buttons say "Coming soon" ----
const LINKS = {
  repo: 'muchkurisrivardhan/vantage',
  sponsors: 'https://github.com/sponsors/muchkurisrivardhan',
  coffee: 'https://buymeacoffee.com/srivardhanmuchuri',
};

// ---- download buttons: live once a GitHub release with the installers exists ----
fetch(`https://api.github.com/repos/${LINKS.repo}/releases/latest`)
  .then(r => (r.ok ? r.json() : null))
  .then(rel => {
    if (!rel) return;
    for (const a of document.querySelectorAll('.dl-link')) {
      const asset = (rel.assets || []).find(x => x.name === a.dataset.asset);
      if (!asset) continue;
      a.href = asset.browser_download_url;
      a.textContent = `Download ${rel.tag_name}`;
      a.classList.remove('btn-disabled');
      a.removeAttribute('aria-disabled');
    }
    const note = document.getElementById('dl-note');
    note.textContent = '';
    note.append(`${rel.tag_name}, released ${new Date(rel.published_at).toLocaleDateString()}. Checksums and release notes are on `,
      Object.assign(document.createElement('a'), { href: rel.html_url, textContent: 'GitHub' }), '.');
  })
  .catch(() => {});

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];

// ---- GitHub star count (shown only once there are stars) ----
fetch(`https://api.github.com/repos/${LINKS.repo}`)
  .then(r => (r.ok ? r.json() : null))
  .then(d => {
    if (!d || !d.stargazers_count) return;
    const n = d.stargazers_count;
    const el = $('#stars');
    el.textContent = n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1) + 'k' : String(n);
    el.hidden = false;
  })
  .catch(() => {});

// ---- the launcher clock ----
const clock = $('#clock');
const tick = () => { clock.textContent = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); };
tick(); setInterval(tick, 15000);

// ---- try it: a mock of Beacon's results, all in the browser ----
const APPS = ['Visual Studio Code', 'Spotify', 'Microsoft Edge', 'File Explorer', 'Terminal', 'Notepad', 'Settings', 'Photos', 'Paint', 'Task Manager'];
const DEFAULTS = [
  { g: 'D', t: 'Open Dotline', s: 'Life, goals, focus timer and the wallpaper map', src: 'SUITE', run: 'Opened Dotline' },
  { g: '25', t: 'Start a 25 minute focus session', s: 'The countdown shows in this bar', src: 'FOCUS', run: 'Focus: 25:00 left' },
  { g: 'L', t: 'Lock screen: apply the HUD', s: 'Your current picture is saved first', src: 'LOCK', run: 'Lock screen set (demo)' },
  { g: 'S', t: 'Settings', s: 'Style, accent colour, widgets and lock screen', src: 'BEACON', run: 'Opened Settings (demo)' },
];

const fuzzy = (q, s) => {
  q = q.toLowerCase(); s = s.toLowerCase();
  if (s.includes(q)) return 100 - s.indexOf(q);
  let i = 0; for (const c of s) if (c === q[i]) i++;
  return i === q.length ? 40 : 0;
};

function understand(q) {
  const rows = [];
  const lower = q.toLowerCase().trim();
  if (!lower) return DEFAULTS;

  if (lower.startsWith('?')) {
    const ask = q.slice(1).trim() || 'anything';
    rows.push({ g: '?', t: `Ask Claude: ${ask}`, s: 'Uses your own API key. Destructive steps ask first', src: 'CLAUDE', run: 'Claude would answer here (demo)' });
    return rows;
  }

  // Arithmetic: digits and operators only, so evaluating it can't run anything else.
  if (/^[\d\s+\-*/().%]+$/.test(lower) && /\d/.test(lower) && /[+\-*/%]/.test(lower)) {
    try {
      const v = Function(`"use strict"; return (${lower})`)();
      if (Number.isFinite(v)) rows.push({ g: '=', t: `= ${+v.toFixed(10)}`, s: 'Enter copies the answer', src: 'CALC', run: `Copied ${+v.toFixed(10)}` });
    } catch { /* half-typed sum */ }
  }

  // Plain English: a sentence becomes steps.
  const steps = [];
  const vol = lower.match(/volume (?:to |at )?(\d{1,3})/);
  if (vol) steps.push(`Set volume ${Math.min(100, +vol[1])}%`);
  if (/\bmute\b/.test(lower)) steps.push('Mute');
  if (/\bdim\b/.test(lower)) steps.push('Dim screen 30%');
  if (/\bbright(en)?\b/.test(lower)) steps.push('Brightness 80%');
  const timer = lower.match(/(\d+)\s*(m|min|minute|minutes)\b(?!.*focus)/);
  if (timer && /timer|remind/.test(lower)) steps.push(`Timer ${timer[1]} min`);
  if (steps.length) rows.push({ g: '»', t: steps.join(' · '), s: `${steps.length} step${steps.length > 1 ? 's' : ''}, understood on your PC`, src: 'SENTENCE', run: `Done: ${steps.join(', ')} (demo)` });

  const focus = lower.match(/^focus\s*(\d+)?/);
  if (focus) { const m = focus[1] || 25; rows.push({ g: m, t: `Start a ${m} minute focus session`, s: 'Dotline counts it down; this bar shows it', src: 'FOCUS', run: `Focus: ${m}:00 left` }); }

  for (const d of DEFAULTS) if (fuzzy(lower, d.t) > 60) rows.push(d);

  APPS.map(a => ({ a, score: fuzzy(lower, a) }))
    .filter(x => x.score > 0).sort((x, y) => y.score - x.score).slice(0, 4)
    .forEach(({ a }) => rows.push({ g: a[0], t: a, s: 'App', src: 'APP', run: `Opened ${a} (demo)` }));

  if (!rows.length) rows.push({ g: '⌕', t: `Search the web for "${q.trim()}"`, s: 'Or start with ? to ask Claude', src: 'WEB', run: 'Would open your browser (demo)' });
  return rows.slice(0, 5);
}

const input = $('#q'), list = $('#results'), status = $('#status');
let rows = [], sel = 0;

function render() {
  rows = understand(input.value);
  sel = Math.min(sel, rows.length - 1);
  list.replaceChildren(...rows.map((r, i) => {
    const li = document.createElement('li');
    li.setAttribute('role', 'option');
    li.setAttribute('aria-selected', String(i === sel));
    const g = Object.assign(document.createElement('span'), { className: 'glyph', textContent: r.g });
    const body = document.createElement('span');
    body.append(Object.assign(document.createElement('span'), { className: 't', textContent: r.t }),
                Object.assign(document.createElement('span'), { className: 's', textContent: r.s }));
    const src = Object.assign(document.createElement('span'), { className: 'src', textContent: r.src });
    li.append(g, body, src);
    li.addEventListener('mouseenter', () => { sel = i; mark(); });
    li.addEventListener('click', () => { sel = i; run(); });
    return li;
  }));
}
const mark = () => $$('li', list).forEach((li, i) => li.setAttribute('aria-selected', String(i === sel)));
function run() { if (rows[sel]) { status.textContent = rows[sel].run; clearTimeout(run.t); run.t = setTimeout(() => (status.textContent = ''), 3500); } }

input.addEventListener('input', () => { sel = 0; render(); });
input.addEventListener('keydown', e => {
  if (e.key === 'ArrowDown') { sel = (sel + 1) % rows.length; mark(); e.preventDefault(); }
  else if (e.key === 'ArrowUp') { sel = (sel - 1 + rows.length) % rows.length; mark(); e.preventDefault(); }
  else if (e.key === 'Enter') { run(); e.preventDefault(); }
  else if (e.key === 'Escape') { input.value = ''; sel = 0; render(); }
});
$$('.chip[data-try]').forEach(b => b.addEventListener('click', () => { input.value = b.dataset.try; sel = 0; render(); input.focus(); }));
render();

// ---- launcher style chips: re-skin the mock, and show the matching lock screen theme ----
const STYLE_TO_THEME = { tactical: 'amber', hud: 'cyan', crt: 'phosphor', cyberpunk: 'neon', mono: 'mono' };
const tryit = $('#tryit');
$$('.styles button').forEach(b => b.addEventListener('click', () => {
  tryit.dataset.style = b.dataset.style;
  $$('.styles button').forEach(x => x.setAttribute('aria-checked', String(x === b)));
  setTheme(STYLE_TO_THEME[b.dataset.style]);
}));

// ---- lock screen theme tabs ----
const lockImg = $('#lockimg'), emblem = $('#emblem');
function setTheme(theme) {
  const tab = $(`.themes button[data-theme="${theme}"]`);
  if (!tab) return;
  $$('.themes button').forEach(x => x.setAttribute('aria-selected', String(x === tab)));
  lockImg.src = `img/lock-${theme}.jpg`;
  lockImg.alt = `Lock screen HUD in the ${tab.textContent.toLowerCase()} theme`;
  emblem.src = `img/emblem-${theme}.png`;
}
$$('.themes button').forEach(b => b.addEventListener('click', () => setTheme(b.dataset.theme)));
['amber', 'cyan', 'phosphor', 'neon', 'redalert', 'mono'].forEach(t => { new Image().src = `img/lock-${t}.jpg`; });

// ---- sponsor tabs and links ----
$$('.sp-tabs button').forEach(b => b.addEventListener('click', () => {
  $$('.sp-tabs button').forEach(x => x.setAttribute('aria-selected', String(x === b)));
  $$('.sp-panel').forEach(p => { p.hidden = p.dataset.panel !== b.dataset.tab; });
}));

let onceAmount = 10;
$$('.amt').forEach(b => b.addEventListener('click', () => {
  onceAmount = +b.dataset.amt;
  $$('.amt').forEach(x => x.setAttribute('aria-pressed', String(x === b)));
}));
$('.amt[data-amt="10"]').setAttribute('aria-pressed', 'true');

function hrefFor(kind, a) {
  if (kind === 'coffee') return LINKS.coffee;
  if (!LINKS.sponsors) return null;
  if (kind === 'sponsors-once') return `${LINKS.sponsors}?frequency=one-time&amount=${onceAmount}`;
  // monthly: fixed tiers carry data-amt, the custom tier reads the input
  const amt = Math.max(1, Math.round(+(a && a.dataset.amt || $('#monthly-amount').value) || 5));
  return `${LINKS.sponsors}?frequency=recurring&amount=${amt}`;
}
let anyMissing = false;
$$('.sponsor-link').forEach(a => {
  const ready = Boolean(hrefFor(a.dataset.kind, a));
  if (!ready) {
    anyMissing = true;
    a.textContent = 'Coming soon';
    a.classList.add('btn-disabled');
    a.removeAttribute('href');
    a.setAttribute('aria-disabled', 'true');
    return;
  }
  a.target = '_blank'; a.rel = 'noopener';
  a.addEventListener('click', () => { a.href = hrefFor(a.dataset.kind, a); });
  a.href = hrefFor(a.dataset.kind, a);
});
$('#sp-soon').hidden = !anyMissing;
