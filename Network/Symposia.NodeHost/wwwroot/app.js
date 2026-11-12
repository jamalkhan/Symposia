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

// Views that live behind the login get a #hash so refresh and back/forward keep the page.
const NAV_VIEWS = ['diagnostics', 'logs', 'dashboard', 'network', 'services', 'ledger', 'settings'];
const hashView = () => (NAV_VIEWS.includes(location.hash.slice(1)) ? location.hash.slice(1) : null);

function show(view) {
  if (NAV_VIEWS.includes(view) && location.hash !== `#${view}`) history[hashView() ? 'pushState' : 'replaceState'](null, '', `#${view}`);
  $$('main > section').forEach((s) => (s.hidden = s.id !== `view-${view}`));
  $$('.tab').forEach((t) => t.classList.toggle('active', t.dataset.view === view));
  clearInterval(pollTimer);
  if (view === 'diagnostics') { refresh(); pollTimer = setInterval(refresh, 5000); }
  if (view === 'logs') { refreshLogs(); pollTimer = setInterval(refreshLogs, 5000); }
  if (view === 'dashboard') { refreshDashboard(); pollTimer = setInterval(refreshDashboard, 60000); }
  if (view === 'network') { refreshNetwork(); pollTimer = setInterval(refreshNetwork, 5000); }
  if (view === 'settings') loadSettings();
  if (view === 'services') { loadSettings(); loadServices(); }
  if (view === 'ledger') { refreshLedger(); pollTimer = setInterval(refreshLedger, 15000); }
}

async function route() {
  let session;
  try { session = await (await fetch('/api/session')).json(); }
  catch { document.body.dataset.offline = '1'; setTimeout(route, 3000); return; }
  $('#nav').hidden = !session.authenticated;
  $('#setup-token').hidden = !session.setupTokenRequired;
  if (session.setupRequired) return show('setup');
  if (!session.authenticated) return show('login');
  show(hashView() || 'diagnostics');
}
window.addEventListener('popstate', () => { if (!$('#nav').hidden) show(hashView() || 'diagnostics'); });

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

// Parts of /api/status every polled tab renders (header, alert banner), so one poll per tab keeps them current.
function applyStatus(s) {
  $('#node-address').textContent = s.node;
  renderAlerts(s.activeAlerts || []);
}

// ---------- operator alerts (#196) ----------
function renderAlerts(alerts) {
  const banner = $('#alerts-banner');
  banner.hidden = alerts.length === 0;
  banner.innerHTML = alerts.map((a) => `<div class="alert ${a.severity === 'Error' ? 'bad' : 'warn'}"><b>${esc(a.title)}</b> ${esc(a.detail)}</div>`).join('');
}

// Tabs that don't poll /api/status still keep the banner current.
setInterval(async () => {
  const view = hashView();
  if ($('#nav').hidden || view === 'diagnostics' || view === 'logs') return;
  try { renderAlerts((await api('GET', '/status')).activeAlerts || []); } catch { /* next tick */ }
}, 15000);

$('#alerts-test').addEventListener('click', async () => {
  const out = $('#alerts-test-result'), form = $('#settings-form');
  const alerts = {};
  $$('[name^="Alerts."]', form).forEach((el) => (alerts[el.name.slice(7)] = el.type === 'number' ? Number(el.value) : el.value));
  out.textContent = 'Sending…';
  try {
    const r = await api('POST', '/alerts/test', { alerts, smtpPassword: $('[name="smtpPassword"]', form).value || null });
    out.textContent = r.sent ? `Sent to ${alerts.Email}.` : `Failed: ${r.error}`;
  } catch (ex) { out.textContent = ex.message; }
});

async function refresh() {
  let s;
  try { s = await api('GET', '/status'); $('#stale').hidden = true; }
  catch (ex) { $('#stale').hidden = false; $('#stale').textContent = `Status unavailable (${ex.message}) — showing last known data.`; return; }
  applyStatus(s);
  $('#unconfigured').hidden = s.mode !== 'Unconfigured';
  $('#components').innerHTML = table(['Component', 'Status', 'Detail'], s.components.map((c) =>
    `<tr><td>${esc(c.name)}</td><td class="status ${esc(c.status)}">${esc(c.status)}</td><td>${esc(c.reason)}</td></tr>`));
  const n = s.network || {};
  setTitle(s.node, n.chainId);
  const RPC_SOURCES = { Direct: '', Gateway: ' (via gateway)', Advertised: ' (advertised by gateway)' };
  const rpc = n.rpcUrl ? n.rpcUrl + (RPC_SOURCES[n.rpcSource] || '') : (s.mode === 'Join' ? 'none — gateway advertises no RPC' : '');
  $('#network').innerHTML = dl([['Mode', s.mode], ['Gateway', n.gatewayUrl], ['Chain ID', n.chainId], ['RPC', rpc],
    ['Rewards contracts', (n.rewardsContracts || []).join(', ') || 'none known'],
    ['Latest block', s.chain?.latestBlock], ['NodeRegistry', n.nodeRegistryAddress], ['EpochRootRegistry', n.epochRootRegistryAddress], ['Data dir', s.dataDir]]);
  if (s.chain?.error) $('#network').innerHTML += dl([['Chain', s.chain.error]]);
  const r = s.registry || {};
  $('#registration').innerHTML = r.error ? `<p class="error">${esc(r.error)}</p>` : dl([['Registered', s.registry ? (r.registered ? 'yes' : 'no') : ''],
    ['Endpoint on-chain', r.endpoint], ['Primary endpoint', s.primaryEndpoint], ['Nonce', r.nonce]]);
  // #228: a loopback primary is only reachable on this machine; peers and the Verifier can't use it.
  if (s.mode !== 'LocalDev' && /^https?:\/\/(127\.|localhost)/.test(s.primaryEndpoint || ''))
    $('#registration').innerHTML += `<p class="error">The primary endpoint is ${esc(s.primaryEndpoint)}, which other nodes can't reach. Set an advertised endpoint for the role that serves it.</p>`;
  $('#peers').innerHTML = table(['Endpoint', 'Address', 'Status', 'Reason', 'Last attempt'], s.peers.map((p) =>
    `<tr><td class="mono">${esc(p.endpoint)}</td><td class="mono">${esc(p.address || p.expectedAddress)}</td><td class="status ${esc(p.status)}">${esc(p.status)}</td><td>${p.reason === 'None' ? '' : esc(p.reason)}</td><td>${esc(new Date(p.lastAttempt).toLocaleTimeString())}</td></tr>`));
  $('#members').innerHTML = table(['Address', 'Endpoint'], (r.members || []).map((m) =>
    `<tr><td class="mono">${esc(m.node)}${m.self ? ' <b>(this node)</b>' : ''}</td><td class="mono">${esc(m.endpoint)}</td></tr>`));
  // Rewards epoch and heartbeats (issue #181 FR5).
  const rw = s.rewards, t = s.telemetry, when = (v) => (v ? new Date(v).toLocaleString() : '');
  const beat = (b) => (b ? `epoch ${b.epoch} slot ${b.slot} at ${when(b.at)}${b.reason ? ` — ${b.reason}` : ''}` : 'none yet');
  $('#rewards-card').hidden = !rw && !t;
  $('#rewards').innerHTML = rw?.error ? `<p class="error">${esc(rw.error)}</p>` : dl([
    ['Epoch', rw ? `${rw.epoch} (${Math.round(rw.epochSeconds / 60)} min)` : ''], ['Epoch ends', when(rw?.epochEndsAt)],
    ['Sealable from', when(rw?.sealableFrom)], ['Telemetry committee', rw ? (rw.committeeMember ? `this node (${rw.threshold} of ${rw.committeeSize})` : `not a member (${rw.committeeSize} members)`) : ''],
    ['Heartbeats', !t ? 'off' : !t.configured ? 'telemetry not configured' : t.scheduleProblem ? `held: ${t.scheduleProblem}` : !t.scheduleSynced ? 'holding until the chain schedule syncs' : 'sending'],
    ['Last accepted', t?.configured ? beat(t.lastAccepted) : ''], ['Last rejected', t?.configured ? beat(t.lastRejected) : ''],
    // Gossip reach (issue #207): accepted heartbeats only count once they leave this node.
    ['Gossip', !t?.gossip ? '' : t.gossip.warning ? `⚠ ${t.gossip.warning}`
      : `${t.gossip.recognizedPeers} peers, last push ${t.gossip.lastPushSucceeded ? when(t.gossip.lastPushSucceeded) : 'none yet'}, ${t.gossip.failedPushes} failed`]]);
  $('#rewards-stale').hidden = !s.rewardsStale;
  if (s.rewardsStale) $('[data-reason]', $('#rewards-stale')).textContent = s.rewardsStale.reason;
  const c = s.storageCapacity, gb = (b) => `${(b / GB).toFixed(1)} GB`;
  $('#capacity-card').hidden = !c;
  if (c) {
    $('#capacity').innerHTML = dl([['Allocated', gb(c.allocated)], ['Used', gb(c.used)], ['Unused allocated', gb(c.unused)],
      ['Free on disk', `${gb(c.free)} (${c.volumeRoot}, ${gb(c.reserve)} kept in reserve)`],
      ['Disk space', c.diskLow === 'Ok' ? 'OK' : `${c.diskLow}: warning below ${gb(c.warnBelow)}, error below ${gb(c.errorBelow)}`],
      ['Mode', c.mode === 'Writable' ? 'Accepting writes' : c.mode === 'ReadOnly' ? `Read-only until ${gb(c.exitAbove)} free` : 'Critical: below reserve, reporting unhealthy'],
      ['Reported', `${gb(c.available)} available of ${gb(c.total)}`]]);
  }
  const g = s.blobGateway;
  $('#blobgateway-card').hidden = !g || !g.configured;
  if (g && g.configured) {
    const url = g.port ? `http://${location.hostname}:${g.port}` : '';
    $('#blobgateway').innerHTML = dl([['Status', g.level === 'Ok' ? 'Healthy' : g.level], ['Detail', g.detail], ['S3 endpoint (LAN)', url],
      ['Storage nodes', g.storageNodes != null ? `${g.healthyStorageNodes} reachable of ${g.storageNodes}` : ''],
      ['Waiting for another copy', g.underReplicated]]);
  }
  refreshVerifier();
}

// Verifier role (#225 FR7): mode and why, targets with their last probe, each target's latest report.
const OUTCOMES = ['OK', 'Wrong answer', 'Server error', 'Other status', 'Timeout', 'Connect failed', 'Rate limited', 'Undiscoverable'];
const REPORT_STATES = ['Measured', 'Observed only', 'Signed', 'Submitted', 'Terminal'];
const REPORT_FLAGS = [[1, 'bytes capped'], [2, 'bytes missing'], [4, 'bytes signature invalid'], [8, 'low coverage']];
async function refreshVerifier() {
  let v;
  try { v = await api('GET', '/verifier'); } catch { return; }
  const snap = v.snapshot;
  $('#verifier-card').hidden = v.status === 'Disabled' && !snap;
  if (!snap) { $('#verifier').innerHTML = dl([['Status', v.status], ['Detail', v.reason]]); $('#verifier-targets').innerHTML = ''; $('#verifier-reports').innerHTML = ''; return; }
  const MODES = { Attesting: 'Attesting (signs reports)', ObserveOnly: 'Observe-only (measures, never signs)', Unknown: 'Unknown (signs nothing)' };
  $('#verifier').innerHTML = dl([['Mode', MODES[snap.mode] || snap.mode], ['Why', snap.reason], ['Epoch', snap.epoch],
    ['Discarded minutes this epoch', snap.discardedMinutes]]);
  $('#verifier-targets').innerHTML = table(['Gateway', 'URL', 'Last probe', 'Latency'], snap.targets.map((t) =>
    `<tr><td class="mono">${esc(t.node)}</td><td class="mono">${esc(t.url || t.problem)}</td><td>${t.lastOutcome == null ? 'not yet' : esc(OUTCOMES[t.lastOutcome] || t.lastOutcome)}</td><td>${t.lastLatencyMs == null ? '' : `${t.lastLatencyMs} ms`}</td></tr>`));
  $('#verifier-reports').innerHTML = table(['Gateway', 'Epoch', 'Uptime / success / p95 / bytes', 'State', 'Notes'], snap.reports.map((r) =>
    `<tr><td class="mono">${esc(r.target)}</td><td>${r.epoch}</td><td>${(r.values[0] / 100).toFixed(2)}% / ${(r.values[1] / 100).toFixed(2)}% / ${r.values[2]} ms / ${bytes(r.values[3])}</td>
      <td>${esc(REPORT_STATES[r.state] || r.state)}${r.txHash ? ` <span class="mono">${esc(r.txHash.slice(0, 10))}…</span>` : ''}</td>
      <td>${esc([...REPORT_FLAGS.filter(([f]) => r.flags & f).map(([, n]) => n), r.lastError].filter(Boolean).join('; '))}</td></tr>`));
}

// ---------- network dashboard (#198) ----------
const bytes = (b) => {
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let v = b, u = 0;
  while (Math.abs(v) >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return u === 0 ? `${b} B` : `${v.toFixed(1)} ${units[u]}`;
};
async function refreshDashboard() {
  let d;
  try { d = await api('GET', '/dashboard/storage'); $('#dash-error').hidden = true; }
  catch (ex) { $('#dash-error').hidden = false; $('#dash-error').textContent = `Dashboard unavailable (${ex.message}).`; return; }
  const p = d.population;
  $('#dash-asof').textContent = `as of ${new Date(d.asOf).toLocaleTimeString()}`;
  $('#dash-error').hidden = !d.error;
  if (d.error) $('#dash-error').textContent = d.error;
  $('#dash-storage').innerHTML = dl([
    ['Total storage used', bytes(d.usedBytes)],
    ['Total storage available', bytes(d.availableBytes)],
    ['Available nodes', `${p.available} available · ${p.readOnly} read-only · ${p.unhealthy} unhealthy/unreachable of ${p.registered} registered`],
    ['Average uptime (30 days)', d.meanUptimePercent == null ? 'no telemetry yet' : `${d.meanUptimePercent.toFixed(1)}%`]]);
  $('#dash-versions').innerHTML = table(['Version', 'Nodes', 'Share of network'],
    d.versions.map((v) => `<tr><td>${esc(v.version)}</td><td class="num">${v.count}</td><td class="num">${v.percent.toFixed(1)}%</td></tr>`));
  const notes = [];
  if (d.fromCheckpoint) notes.push(`${d.fromCheckpoint} unreachable node(s) counted from their last on-chain checkpoint.`);
  if (d.usedUnknown) notes.push(`Used storage excludes ${d.usedUnknown} node(s) that don't report it.`);
  if (d.uptimeNodesWithoutData) notes.push(`Uptime averages ${d.uptimeNodes} node(s); ${d.uptimeNodesWithoutData} have no checkpoints in the window.`);
  $('#dash-notes').textContent = notes.join(' ');
}

// ---------- logs (#195) ----------
async function refreshLogs() {
  let s;
  try { s = await api('GET', '/status'); } catch { return; }
  applyStatus(s);
  const logs = $('#logs'), first = !logs.textContent, atBottom = logs.scrollTop + logs.clientHeight >= logs.scrollHeight - 4;
  logs.textContent = s.logs.map((l) => `${new Date(l.time).toLocaleTimeString()} [${l.component}] ${l.level}: ${l.message}`).join('\n');
  if (first || atBottom) logs.scrollTop = logs.scrollHeight;
}

$('#reset-rewards').addEventListener('click', async () => {
  const status = $('[data-status]', $('#rewards-stale'));
  if (!confirm('Redeploy the rewards contracts on this local chain?\n\nThe earlier ledger and stakes on this chain are abandoned; node registrations are kept. Joined hosts must restart.')) return;
  status.textContent = 'Redeploying…';
  try { await api('POST', '/chain/reset-rewards'); status.textContent = 'Done. Restart any hosts that joined this network so they pick up the new contracts.'; refresh(); }
  catch (ex) { status.textContent = ex.message; }
});

// ---------- network ----------
const fmtGb = (b) => `${(b / GB).toFixed(1)} GB`;
function light(h, describe) {
  const tip = h.detail || (h.level === 'Off' ? 'Not configured' : describe(h));
  return `<span class="light ${esc(h.level)}" title="${esc(tip)}"></span> <span class="hint">${esc(tip)}</span>`;
}
const storageTip = (h) => (h.freeBytes != null ? `${fmtGb(h.freeBytes)} free of ${fmtGb(h.capacityBytes)}` : h.level);
const gatewayTip = (h) => (h.healthyStorageNodes != null ? `${h.healthyStorageNodes} storage node(s) reachable` : h.level);
const computeTip = (h) => (h.cpuPercent != null ? `${h.cpuPercent}% CPU` : h.level);

async function refreshNetwork() {
  let s;
  try { s = await api('GET', '/network'); }
  catch (ex) { $('#network-error').hidden = false; $('#network-error').textContent = `Network unavailable (${ex.message}) — showing last known data.`; return; }
  $('#network-error').hidden = !s.error;
  $('#network-error').textContent = s.error || '';
  $('#network-nodes').innerHTML = table(['Public IP address', 'Node hash', 'Protocol', 'Storage', 'Compute', 'Gateway'], s.nodes.map((n) =>
    `<tr><td class="mono">${esc(n.host)}</td><td class="mono" title="${esc(n.endpoint)}">${esc(n.node)}${n.self ? ' <b>(this node)</b>' : ''}` +
    `${n.registered ? '' : `<br><span class="hint">${esc(n.notRegisteredReason)}</span>`}` +
    `${(n.warnings || []).map((w) => `<br><span class="warn">${esc(w)}</span>`).join('')}</td>` +
    `<td>${protocolCell(n.services)}</td>` +
    `<td>${light(n.storage, storageTip)}</td><td>${light(n.compute, computeTip)}</td>` +
    `<td>${light(n.gateway || { level: 'Off', detail: 'Not reported' }, gatewayTip)}</td></tr>`));
  renderProtocol(s.protocol);
}

// Protocol version and features from the node's services document (#145).
function protocolCell(svc) {
  if (!svc) return '<span class="hint">—</span>';
  const features = svc.features || [];
  return `v${esc(svc.protocolVersion ?? 0)}<br><span class="hint mono">${features.length ? features.map(esc).join('<br>') : 'no features listed'}</span>`;
}

// The on-chain activation schedule with storage-node readiness (#145).
function renderProtocol(p) {
  const el = $('#network-protocol');
  if (!p || !p.configured) { el.innerHTML = '<p class="hint">This network has no protocol activation contract; nothing is required.</p>'; return; }
  const when = (t) => new Date(t * 1000).toLocaleString();
  const min = p.minProtocolVersion || {};
  const minText = `Minimum protocol version: v${esc(min.effective ?? 0)}` + (min.pending ? ` (v${esc(min.pending)} from ${esc(when(min.pendingAt))})` : '');
  el.innerHTML = `<p>${minText}</p>` + (p.activations.length
    ? table(['Feature', 'Required from', 'State', 'Storage nodes ready'], p.activations.map((a) =>
      `<tr><td class="mono">${esc(a.name)}</td><td>${esc(when(a.activatesAt))}</td><td>${a.inEffect ? 'Active' : 'Scheduled'}</td>` +
      `<td>${a.ready === a.total ? '' : '<span class="warn">'}${esc(a.ready)} of ${esc(a.total)}${a.ready === a.total ? '' : '</span>'}</td></tr>`))
    : '<p class="hint">No activations scheduled.</p>');
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

let lanAddress = null;
document.addEventListener('click', (e) => {
  const b = e.target.closest('[data-use-lan]');
  if (!b || !lanAddress) return;
  const service = b.dataset.useLan, port = $(`[name="${service}.Port"]`).value;
  $(`[name="${service}.AdvertisedEndpoint"]`).value = `http://${lanAddress}:${port}`;
});
// Keys are generated in the browser and only sent when the operator saves.
const randomString = (alphabet, n) => [...crypto.getRandomValues(new Uint8Array(n))].map((b) => alphabet[b % alphabet.length]).join('');
$('#generate-keys').addEventListener('click', () => {
  const id = 'SYM' + randomString('ABCDEFGHIJKLMNOPQRSTUVWXYZ234567', 17);
  const secret = randomString('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789', 40);
  $('[name="BlobGateway.AccessKeyId"]').value = id;
  const field = $('[name="blobGatewaySecret"]');
  field.type = 'text'; field.value = secret;
  $('#generated-hint').textContent = 'Copy the secret now; after saving it is never shown again.';
});

// Settings are split across the Settings and Node Services tabs; both forms edit the same document.
const settingsForms = () => $$('[data-settings-form]');
const settingsFields = () => settingsForms().flatMap((f) => $$('[name]', f));

async function loadSettings() {
  let data;
  try { data = await api('GET', '/settings'); }
  catch (ex) { $$('[data-save-result]').forEach((r) => (r.textContent = `Could not load settings: ${ex.message}`)); return; }
  current = data.settings;
  $('#presets').innerHTML = data.presets.map((p) => `<option value="${esc(p.name)}">${esc(p.name)}${p.deployed ? '' : ' (not yet deployed)'}</option>`).join('');
  const locked = new Set(data.lockedKeys.map((k) => k.toLowerCase().replaceAll(':', '.')));
  settingsFields().forEach((el) => {
    const name = el.name;
    if (name === 'blobGatewaySecret') { el.value = ''; el.placeholder = data.blobGatewaySecretSet ? '•••••••• (set — enter a new secret to replace)' : 'not set'; return; }
    if (name === 'smtpPassword') { el.value = ''; el.placeholder = data.smtpPasswordSet ? '•••••••• (set — enter a new password to replace)' : 'not set'; return; }
    if (name === 'clearSmtpPassword') { el.checked = false; el.disabled = !data.smtpPasswordSet; return; }
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
  lanAddress = data.lanAddress;
  $$('[data-lan]').forEach((p) => (p.hidden = !lanAddress));
  $$('[data-use-lan]').forEach((b) => (b.textContent = `Use ${lanAddress}`));
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

settingsForms().forEach((f) => f.addEventListener('submit', async (e) => {
  e.preventDefault();
  const form = e.target, next = structuredClone(current), result = $('[data-save-result]', form);
  let relayerKey = null, blobGatewaySecret = null, smtpPassword = null;
  $$('[name]', form).forEach((el) => {
    if (el.disabled || el.hasAttribute('data-dup') && el.closest('[hidden]')) return;
    if (el.name === 'relayerKey') { if (el.value) relayerKey = el.value; return; }
    if (el.name === 'blobGatewaySecret') { if (el.value) blobGatewaySecret = el.value; return; }
    if (el.name === 'smtpPassword') { if (el.value) smtpPassword = el.value; return; }
    if (el.name === 'clearSmtpPassword') { if (el.checked && smtpPassword === null) smtpPassword = ''; return; }
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
    await api('PUT', '/settings', { settings: next, relayerKey, blobGatewaySecret, smtpPassword });
    result.textContent = 'Saved. Components restarted as needed.';
    await loadSettings();
  } catch (ex) { result.textContent = ex.message; }
}));

// ---------- node services ----------
// Public earnings breakdown per Node Service (#159), from /api/node-services.
const pct = (bps) => `${(bps / 100).toFixed(bps % 100 ? 1 : 0)}%`;
function earnings(svc) {
  const factors = svc.factors.length
    ? `<table class="factors">${svc.factors.map((f) => `<tr><td>${esc(f.name)}${f.lowerIsBetter ? ' <span class="hint">(lower is better)</span>' : ''}</td><td class="num">${pct(f.weightBps)}</td></tr>`).join('')}</table>`
    : '<p class="hint">No scoring factors defined yet.</p>';
  return `<p><span class="badge ${svc.earns ? 'earns' : 'no-earn'}">${svc.earns ? 'Earns SYM' : 'Does not earn'}</span> <span class="badge">${esc(svc.status)}</span></p>
    <p>${esc(svc.description)}</p>
    ${dl([['Funding', svc.funding], ['Pool share', svc.poolShare], ['Minimum stake', svc.minimumStake]])}
    <h4>Score</h4>${factors}
    <h4>Penalties</h4><ul>${svc.penalties.map((p) => `<li>${esc(p)}</li>`).join('')}</ul>
    ${svc.note ? `<p class="hint">${esc(svc.note)}</p>` : ''}`;
}

async function loadServices() {
  let list;
  try { list = await api('GET', '/node-services'); }
  catch (ex) { $('#service-catalog').innerHTML = `<p class="error">${esc(ex.message)}</p>`; return; }
  const inline = new Set();
  $$('[data-earnings]').forEach((el) => {
    const svc = list.find((x) => x.key === el.dataset.earnings);
    if (svc) { el.innerHTML = earnings(svc); inline.add(svc.key); }
  });
  $('#service-catalog').innerHTML = list.filter((x) => !inline.has(x.key))
    .map((x) => `<div class="card"><h2>${esc(x.name)}</h2>${earnings(x)}</div>`).join('');
}

route();

// ---------- ledger ----------
// Stake, reward and penalty transactions per Node Service (#161), from /api/ledger.
const signed = (a) => (String(a).startsWith('-') ? `<span class="neg">${esc(a)}</span>` : a === '0' ? '0' : `<span class="pos">+${esc(a)}</span>`);
function ledgerQuery() {
  const q = new URLSearchParams();
  if ($('#ledger-category').value) q.set('category', $('#ledger-category').value);
  if ($('#ledger-service').value) q.set('service', $('#ledger-service').value);
  return q.toString();
}

async function refreshLedger() {
  let s;
  try { s = await api('GET', `/ledger?${ledgerQuery()}`); }
  catch (ex) { $('#ledger-unavailable').hidden = false; $('#ledger-unavailable').textContent = `Ledger unavailable (${ex.message}).`; return; }
  $('#ledger-unavailable').hidden = s.available;
  $('#ledger-unavailable').textContent = s.available ? '' : `Ledger unavailable: ${s.reason}`;
  $('#ledger-csv').href = `/api/ledger.csv?${ledgerQuery()}`;
  $('#ledger-totals').innerHTML = table(['Node Service', 'Stake (SYM)', 'Earned (SYM)', 'Slashed (SYM)', 'Reserve held (SYM)'], s.totals.map((t) =>
    `<tr><td>${esc(t.service)}</td><td class="num">${esc(t.stake)}</td><td class="num">${esc(t.earned)}</td><td class="num">${esc(t.slashed)}</td><td class="num">${esc(t.reserveHeld)}</td></tr>`));
  const services = $('#ledger-service'), chosen = services.value;
  services.innerHTML = '<option value="">All</option>' + s.totals.map((t) => `<option${t.service === chosen ? ' selected' : ''}>${esc(t.service)}</option>`).join('');
  $('#ledger-entries').innerHTML = table(['Time', 'Node Service', 'Category', 'Entry', 'Amount (SYM)', 'Epoch', 'Detail', 'Transaction'], s.entries.map((e) =>
    `<tr><td>${esc(new Date(e.timestamp * 1000).toLocaleString())}</td><td>${esc(e.service)}</td><td>${esc(e.category)}</td><td>${esc(e.type)}</td>` +
    `<td class="num">${signed(e.amount)}</td><td>${e.epoch ?? ''}</td><td>${esc(e.detail)}</td>` +
    `<td class="mono" title="${esc(e.txHash)}">${esc(shortAddress(e.txHash))} <span class="hint">#${esc(e.block)}</span></td></tr>`));
}
['#ledger-category', '#ledger-service'].forEach((sel) => $(sel).addEventListener('change', refreshLedger));
