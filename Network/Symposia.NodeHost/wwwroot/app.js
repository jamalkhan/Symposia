// Symposia Node Host UI (issue #133). Plain JS, no build step.
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const GB = 1024 ** 3;
let pollTimer = null;

async function api(method, path, body) {
  const res = await fetch(`/api${path}`, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'same-origin',
  });
  if (res.status === 401 && path !== '/login') { await route(); throw new Error('Not logged in'); }
  const text = await res.text();
  const data = text ? JSON.parse(text) : null;
  if (!res.ok) {
    const msg = data?.errors?.join(' ') || data?.error || (res.status === 429 ? 'Too many attempts — wait a minute.' : `Request failed (${res.status})`);
    throw new Error(msg);
  }
  return data;
}

function show(view) {
  $$('main > section').forEach((s) => (s.hidden = s.id !== `view-${view}`));
  $$('.tab').forEach((t) => t.classList.toggle('active', t.dataset.view === view));
  clearInterval(pollTimer);
  if (view === 'diagnostics') { refresh(); pollTimer = setInterval(refresh, 5000); }
  if (view === 'network') { refreshNetwork(); pollTimer = setInterval(refreshNetwork, 5000); }
  if (view === 'settings') loadSettings();
}

async function route() {
  let session;
  try { session = await (await fetch('/api/session')).json(); }
  catch { document.body.dataset.offline = '1'; setTimeout(route, 3000); return; }
  $('#nav').hidden = !session.authenticated;
  $('#setup-token').hidden = !session.setupTokenRequired;
  if (session.setupRequired) return show('setup');
  if (!session.authenticated) return show('login');
  show('diagnostics');
}

// ---------- auth ----------
$('#setup-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const f = e.target, err = $('[data-error]', f);
  if (f.password.value !== f.confirm.value) { err.textContent = 'Passwords do not match.'; return; }
  try { await api('POST', '/setup', { password: f.password.value, setupToken: f.setupToken.value || null }); route(); } catch (ex) { err.textContent = ex.message; }
});
$('#login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const f = e.target, err = $('[data-error]', f);
  try { await api('POST', '/login', { password: f.password.value }); f.reset(); route(); }
  catch (ex) { err.textContent = ex.message === 'Request failed (401)' ? 'Wrong password.' : ex.message; }
});
$('#logout').addEventListener('click', async () => { await api('POST', '/logout'); route(); });
$$('.tab').forEach((t) => t.addEventListener('click', () => show(t.dataset.view)));
document.addEventListener('click', (e) => { const g = e.target.closest('[data-goto]'); if (g) { e.preventDefault(); show(g.dataset.goto); } });

// ---------- diagnostics ----------
const dl = (pairs) => pairs.map(([k, v]) => `<dt>${esc(k)}</dt><dd class="mono">${v === undefined || v === null || v === '' ? '—' : esc(v)}</dd>`).join('');
const table = (head, rows) => `<tr>${head.map((h) => `<th>${esc(h)}</th>`).join('')}</tr>` + (rows.length ? rows.join('') : `<tr><td colspan="${head.length}" class="hint">none</td></tr>`);

// Environment comes from the chain ID: 31337 is Anvil; Base Sepolia and Base are fixed.
// Symposia L3 chain IDs get added here once #48 assigns them.
const ENVIRONMENTS = { 31337: 'Dev', 84532: 'Testnet', 8453: 'Mainnet' };
const shortAddress = (a) => (a && a.length > 10 ? `${a.slice(0, 5)}...${a.slice(-3)}` : a || '');
function setTitle(node, chainId) {
  const env = chainId ? ENVIRONMENTS[chainId] || `Chain ${chainId}` : '';
  const title = ['Symposia', shortAddress(node), env].filter(Boolean).join(' ');
  document.title = title;
  $('header h1').textContent = title;
}

async function refresh() {
  let s;
  try { s = await api('GET', '/status'); $('#stale').hidden = true; }
  catch (ex) { $('#stale').hidden = false; $('#stale').textContent = `Status unavailable (${ex.message}) — showing last known data.`; return; }
  $('#node-address').textContent = s.node;
  $('#unconfigured').hidden = s.mode !== 'Unconfigured';
  $('#components').innerHTML = table(['Component', 'Status', 'Detail'], s.components.map((c) =>
    `<tr><td>${esc(c.name)}</td><td class="status ${esc(c.status)}">${esc(c.status)}</td><td>${esc(c.reason)}</td></tr>`));
  const n = s.network || {};
  setTitle(s.node, n.chainId);
  $('#network').innerHTML = dl([['Mode', s.mode], ['Gateway', n.gatewayUrl], ['Chain ID', n.chainId], ['RPC', n.rpcUrl],
    ['Latest block', s.chain?.latestBlock], ['NodeRegistry', n.nodeRegistryAddress], ['EpochRootRegistry', n.epochRootRegistryAddress], ['Data dir', s.dataDir]]);
  if (s.chain?.error) $('#network').innerHTML += dl([['Chain', s.chain.error]]);
  const r = s.registry || {};
  $('#registration').innerHTML = r.error ? `<p class="error">${esc(r.error)}</p>` : dl([['Registered', s.registry ? (r.registered ? 'yes' : 'no') : ''],
    ['Endpoint on-chain', r.endpoint], ['Primary endpoint', s.primaryEndpoint], ['Nonce', r.nonce]]);
  $('#peers').innerHTML = table(['Endpoint', 'Address', 'Status', 'Reason', 'Last attempt'], s.peers.map((p) =>
    `<tr><td class="mono">${esc(p.endpoint)}</td><td class="mono">${esc(p.address || p.expectedAddress)}</td><td class="status ${esc(p.status)}">${esc(p.status)}</td><td>${p.reason === 'None' ? '' : esc(p.reason)}</td><td>${esc(new Date(p.lastAttempt).toLocaleTimeString())}</td></tr>`));
  $('#members').innerHTML = table(['Address', 'Endpoint'], (r.members || []).map((m) =>
    `<tr><td class="mono">${esc(m.node)}${m.self ? ' <b>(this node)</b>' : ''}</td><td class="mono">${esc(m.endpoint)}</td></tr>`));
  const logs = $('#logs'), atBottom = logs.scrollTop + logs.clientHeight >= logs.scrollHeight - 4;
  logs.textContent = s.logs.map((l) => `${new Date(l.time).toLocaleTimeString()} [${l.component}] ${l.level}: ${l.message}`).join('\n');
  if (atBottom) logs.scrollTop = logs.scrollHeight;
}

// ---------- network ----------
const fmtGb = (b) => `${(b / GB).toFixed(1)} GB`;
function light(h, describe) {
  const tip = h.detail || (h.level === 'Off' ? 'Not configured' : describe(h));
  return `<span class="light ${esc(h.level)}" title="${esc(tip)}"></span> <span class="hint">${esc(tip)}</span>`;
}
const storageTip = (h) => (h.freeBytes != null ? `${fmtGb(h.freeBytes)} free of ${fmtGb(h.capacityBytes)}` : h.level);
const computeTip = (h) => (h.cpuPercent != null ? `${h.cpuPercent}% CPU` : h.level);

async function refreshNetwork() {
  let s;
  try { s = await api('GET', '/network'); }
  catch (ex) { $('#network-error').hidden = false; $('#network-error').textContent = `Network unavailable (${ex.message}) — showing last known data.`; return; }
  $('#network-error').hidden = !s.error;
  $('#network-error').textContent = s.error || '';
  $('#network-nodes').innerHTML = table(['Public IP address', 'Node hash', 'Storage', 'Compute'], s.nodes.map((n) =>
    `<tr><td class="mono">${esc(n.host)}</td><td class="mono" title="${esc(n.endpoint)}">${esc(n.node)}${n.self ? ' <b>(this node)</b>' : ''}` +
    `${n.registered ? '' : `<br><span class="hint">${esc(n.notRegisteredReason)}</span>`}</td>` +
    `<td>${light(n.storage, storageTip)}</td><td>${light(n.compute, computeTip)}</td></tr>`));
}

// ---------- settings ----------
let current = null;
const get = (obj, path) => path.split('.').reduce((o, k) => o?.[k[0].toLowerCase() + k.slice(1)], obj);
const set = (obj, path, value) => {
  const keys = path.split('.').map((k) => k[0].toLowerCase() + k.slice(1));
  keys.slice(0, -1).reduce((o, k) => (o[k] ??= {}), obj)[keys.at(-1)] = value;
};

const LOG_LEVELS = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'];
$$('[data-loglevel]').forEach((s) => (s.innerHTML = LOG_LEVELS.map((l) => `<option>${l}</option>`).join('')));

async function loadSettings() {
  let data;
  try { data = await api('GET', '/settings'); }
  catch (ex) { $('#save-result').textContent = `Could not load settings: ${ex.message}`; return; }
  current = data.settings;
  const form = $('#settings-form');
  $('#presets').innerHTML = data.presets.map((p) => `<option value="${esc(p.name)}">${esc(p.name)}${p.deployed ? '' : ' (not yet deployed)'}</option>`).join('');
  const locked = new Set(data.lockedKeys.map((k) => k.toLowerCase().replaceAll(':', '.')));
  $$('[name]', form).forEach((el) => {
    const name = el.name;
    if (name === 'relayerKey') { el.value = ''; el.placeholder = data.relayerKeySet ? '•••••••• (set — enter a new key to replace)' : 'not set'; return; }
    let value = name === 'Storage.MaxCapacityGb' ? Math.round(get(current, 'Storage.MaxCapacityBytes') / GB) : get(current, name);
    if (el.type === 'radio') el.checked = el.value === value;
    else if (el.type === 'checkbox') el.checked = !!value;
    else el.value = Array.isArray(value) ? value.join(', ') : (value ?? '');
    el.dataset.original = el.type === 'checkbox' || el.type === 'radio' ? String(el.checked) : el.value;
    const key = (name === 'Storage.MaxCapacityGb' ? 'Storage.MaxCapacityBytes' : name).toLowerCase();
    el.disabled = locked.has(key);
    if (el.disabled) el.title = 'Set by environment variable';
  });
  $('#join-suggestion').hidden = !data.localGatewayFound;
  syncModes();
}

function syncModes() {
  const mode = $('[name="Network.Mode"]:checked')?.value;
  $$('[data-mode]').forEach((d) => (d.hidden = d.dataset.mode !== mode));
}
$$('[name="Network.Mode"]').forEach((r) => r.addEventListener('change', syncModes));
$('#use-join').addEventListener('click', () => {
  $('[name="Network.Mode"][value="Join"]').checked = true;
  $('[name="Network.GatewayUrl"]').value = 'http://127.0.0.1:8080';
  syncModes();
});

$('#settings-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.target, next = structuredClone(current), result = $('#save-result');
  let relayerKey = null;
  $$('[name]', form).forEach((el) => {
    if (el.disabled || el.hasAttribute('data-dup') && el.closest('[hidden]')) return;
    if (el.name === 'relayerKey') { if (el.value) relayerKey = el.value; return; }
    if (el.type === 'radio') { if (el.checked) set(next, el.name, el.value); return; }
    if (el.closest('[data-mode][hidden]')) return;
    let v = el.type === 'checkbox' ? el.checked : el.value;
    // Untouched fields keep their exact current value (no GB rounding, no blank-to-0).
    if (el.type !== 'checkbox' && el.value === el.dataset.original) return;
    if (el.type === 'number') { if (v === '') return; v = Number(v); }
    if (el.name === 'Discovery.SeedPeers') v = v.split(',').map((x) => x.trim()).filter(Boolean);
    if (el.name === 'Storage.MaxCapacityGb') { set(next, 'Storage.MaxCapacityBytes', v * GB); return; }
    set(next, el.name, v);
  });
  result.textContent = 'Applying…';
  try {
    await api('PUT', '/settings', { settings: next, relayerKey });
    result.textContent = 'Saved. Components restarted as needed.';
    await loadSettings();
  } catch (ex) { result.textContent = ex.message; }
});

route();
