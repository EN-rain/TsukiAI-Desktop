const $ = id => document.getElementById(id);
const INVITE_URL = 'https://discord.com/oauth2/authorize?client_id=1472450578905829508&permissions=70645806382080&integration_type=0&scope=bot';
const BOT_CONFIG_KEYS = [
  'guild_id', 'voice_channel_id', 'text_channel_id',
  'text_reply_mode', 'text_voice_mode', 'text_voice_keywords',
];
const DEFAULT_TEXT_VOICE_KEYWORDS = 'voice,say it,say this,say that,speak,talk,sing,read it';
const AWARENESS_KEYS = ['voice_engagement_mode', 'chat_engagement_mode',
  'engagement_aliases', 'engagement_followup_ms', 'classifier_model'];
let awarenessSaved = null;
let awarenessDirty = false;
function awarenessValues() {
  return Object.fromEntries(AWARENESS_KEYS.map(key => [key, $(key).value.trim()]));
}
function renderAwarenessSettings(bot) {
  if (!awarenessDirty || !awarenessSaved) {
    for (const key of AWARENESS_KEYS) {
      $(key).value = String(bot[key] ?? ({ voice_engagement_mode: 'context_aware',
        chat_engagement_mode: 'context_aware', engagement_aliases: 'tsuki,tsukihime',
        engagement_followup_ms: '10000', classifier_model: '' })[key]);
    }
    awarenessSaved = awarenessValues();
    awarenessDirty = false;
  }
  renderClassifierAvailability();
  renderAwarenessDirty();
}
function renderClassifierAvailability() {
  const configured = Boolean($('classifier_model').value.trim());
  for (const item of document.querySelectorAll('#engagement-settings option[value="proactive"]')) {
    item.disabled = !configured;
  }
  $('engagement-classifier-status').textContent = configured
    ? 'Classifier ID selected. Groq key access and model availability are verified only by runtime requests; ambiguous/errors abstain.'
    : 'Classifier off. Direct address and same-speaker follow-ups still work; proactive mode is unavailable.';
}
function renderAwarenessDirty() {
  renderClassifierAvailability();
  const values = awarenessValues();
  awarenessDirty = AWARENESS_KEYS.some(key => values[key] !== awarenessSaved?.[key]);
  $('engagement-save-note').textContent = awarenessDirty
    ? 'Unsaved changes · applying reconnects the Discord bridge.'
    : 'Saved settings · no changes pending.';
}
let personalityLoaded = false;
let personalityLoading = false;
let personalityEpoch = 0;
let personalitySaved = '';
let personalityOverrideSaved = '';

function resetPersonalitySession() {
  personalityEpoch += 1;
  personalityLoaded = false;
  personalityLoading = false;
  personalitySaved = '';
  personalityOverrideSaved = '';
  for (const id of ['personality-editor', 'personality-override-editor']) {
    $(id).value = '';
    $(id).disabled = true;
  }
  $('personality-source').textContent = 'assets/Modelfile';
  $('personality-advanced').open = false;
  renderPersonalityDirty();
  renderPersonalityOverrideDirty();
}

function renderPersonalityDirty() {
  const editor = $('personality-editor');
  const dirty = personalityLoaded && editor.value.trim() !== personalitySaved;
  $('personality-counter').textContent = editor.value.length + ' / 4000';
  $('personality-status').textContent = !personalityLoaded ? 'Loading existing Modelfile…'
    : dirty ? 'Unsaved Modelfile changes. Save to apply them to the API.'
      : 'Saved on VPS.';
  $('personality-save').disabled = !dirty || !editor.value.trim();
  $('personality-reset').disabled = !dirty;
  $('personality-reload').disabled = !personalityLoaded;
}

function renderPersonalityOverrideDirty() {
  const editor = $('personality-override-editor');
  const dirty = personalityLoaded && editor.value.trim() !== personalityOverrideSaved;
  $('personality-override-counter').textContent = editor.value.length + ' / 4000';
  $('personality-override-tag').textContent = personalityOverrideSaved ? 'ACTIVE' : 'OFF';
  $('personality-override-status').textContent = !personalityLoaded
    ? 'Loading optional override…'
    : dirty ? 'Unsaved override changes.'
      : personalityOverrideSaved
        ? 'Additional override saved and appended after the Modelfile.'
        : 'No additional override. Only the existing Modelfile personality is used.';
  $('personality-override-save').disabled = !dirty;
  $('personality-override-clear').disabled = !personalityLoaded || !editor.value;
}

async function loadPersonality() {
  if (personalityLoading) return;
  personalityLoading = true;
  const epoch = personalityEpoch;
  try {
    const saved = await request('/api/personality');
    if (epoch !== personalityEpoch) return;
    if (!personalityLoaded || $('personality-editor').value.trim() === personalitySaved) {
      personalitySaved = saved.base_personality || '';
      $('personality-editor').value = personalitySaved;
    }
    if (!personalityLoaded ||
        $('personality-override-editor').value.trim() === personalityOverrideSaved) {
      personalityOverrideSaved = saved.personality_injection || '';
      $('personality-override-editor').value = personalityOverrideSaved;
    }
    personalityLoaded = true;
    $('personality-source').textContent = saved.base_source || 'assets/Modelfile · SYSTEM';
    $('personality-editor').disabled = false;
    $('personality-override-editor').disabled = false;
    renderPersonalityDirty();
    renderPersonalityOverrideDirty();
  } catch (error) {
    if (epoch === personalityEpoch)
      $('personality-status').textContent = 'Could not read existing Modelfile: ' + error.message;
  } finally {
    if (epoch === personalityEpoch) personalityLoading = false;
  }
}
const RADIO_SETTINGS = new Set(['text_reply_mode', 'text_voice_mode']);
function settingValue(key) {
  return RADIO_SETTINGS.has(key)
    ? (document.querySelector('input[name="' + key + '"]:checked')?.value || '')
    : $(key).value;
}
function setSettingValue(key, value) {
  if (!RADIO_SETTINGS.has(key)) {
    $(key).value = value;
    return;
  }
  const radio = [...document.querySelectorAll('input[name="' + key + '"]')]
    .find(item => item.value === value);
  if (radio) radio.checked = true;
}
let csrf = '';
let busy = false;
let savedSettings = {};
let selectedProvider = 'off';
let providerChanging = false;
let inferenceCatalog = [];
let inferenceSaved = [];
let inferenceDraft = [];
let inferenceLoading = false;
let inferenceEditing = false;
let inferenceLoaded = false;
let groqUpdating = false;
let literouterUpdating = false;
const inferenceModels = { groq: [], literouter: [] };
const inferenceModelSaved = { groq: '', literouter: '' };
const inferenceModelBusy = { groq: false, literouter: false };
let shownAvatarKey = '';
let joinedGuilds = [];
let guildsLoaded = false;
let guildsLoading = false;
let selectedGuildId = '';
let currentBotSettings = {};
let guildSelectionSerial = 0;
let notificationTimer = null;
let notificationGeneration = 0;
let latestSnapshot = null;
let voiceHealthPolling = false;
let lastVoiceHealthCheck = 0;
let selectedVoiceOnline = null;
let liveVoiceSession = null;
let voiceSessionSerial = 0;
let voiceOperationBusy = false;

const DASHBOARD_TABS = {
  overview: ['CONTROL ROOM / OVERVIEW', 'Overview', 'Live status and direct actions.'],
  'discord-voice': ['CONTROL ROOM / DISCORD & VOICE', 'Discord & Voice', 'Voice routing, the bot, and selected channels.'],
  ai: ['CONTROL ROOM / AI', 'AI & Personality', 'Existing persona, inference route, and conversation awareness.'],
  vps: ['CONTROL ROOM / VPS', 'VPS Overview', 'Container and host status.'],
  account: ['CONTROL ROOM / ACCOUNT', 'Account', 'Bot avatar and private dashboard access.'],
};
const DISCORD_VOICE_ANCHORS = new Set(['voice', 'discord', 'discord-settings']);
const AI_ANCHORS = new Set(['llm', 'context-awareness', 'personality']);
function showTab() {
  const anchor = location.hash.slice(1);
  const tab = DISCORD_VOICE_ANCHORS.has(anchor) ? 'discord-voice'
    : AI_ANCHORS.has(anchor) ? 'ai'
      : anchor in DASHBOARD_TABS ? anchor : 'overview';
  const changed = $('dashboard').dataset.activeTab !== tab;
  $('dashboard').dataset.activeTab = tab;
  for (const section of document.querySelectorAll('#dashboard [data-view]')) {
    section.hidden = section.dataset.view !== tab;
  }
  for (const link of document.querySelectorAll('.side-nav [data-tab]')) {
    if (link.dataset.tab === tab) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  const [eyebrow, title, description] = DASHBOARD_TABS[tab];
  $('page-eyebrow').textContent = eyebrow;
  $('page-title').textContent = title;
  $('page-description').textContent = description;
  document.body.classList.remove('sidebar-open');
  $('menu-toggle').setAttribute('aria-expanded', 'false');
  $('menu-toggle').setAttribute('aria-label', 'Open navigation');
  if (!$('dashboard').hidden) {
    if (DISCORD_VOICE_ANCHORS.has(anchor) || AI_ANCHORS.has(anchor)) {
      const destination = $(anchor);
      // The target was hidden until this route became active. Scroll only
      // after the unified page has been revealed; preserve old deep links.
      requestAnimationFrame(() => destination?.scrollIntoView({ block: 'start' }));
    } else if (changed) window.scrollTo(0, 0);
  }
}
window.addEventListener('hashchange', showTab);
document.querySelectorAll('.side-nav a, .side-brand, .action-links a').forEach(link => {
  link.addEventListener('click', () => {
    if (link.hash === location.hash) showTab();
  });
});
$('header-account').addEventListener('click', () => { location.hash = '#account'; });

// The mockup's search is real navigation, not a decorative input or an
// unsupported server-wide search. Match existing pages and their actual controls.
const PAGE_SEARCH = [
  { tab: 'overview', label: 'Overview', terms: 'status health snapshot quick actions' },
  { tab: 'discord-voice', label: 'Discord & Voice', terms: 'voice route tts molab colab notebook connection audio bridge bot reconnect invite servers channels guild text reply keywords custom tts speak' },
  { tab: 'ai', label: 'AI & Personality', terms: 'personality modelfile persona system prompt injection llm provider classifier context awareness followup aliases' },
  { tab: 'vps', label: 'VPS Overview', terms: 'containers docker uptime cpu ram disk restart' },
  { tab: 'account', label: 'Account', terms: 'password security avatar session sign out' },
];
const pageSearch = $('page-search');
const searchResults = $('search-results');
function closePageSearch() {
  searchResults.hidden = true;
  searchResults.replaceChildren();
  pageSearch.setAttribute('aria-expanded', 'false');
}
function renderPageSearch() {
  const query = pageSearch.value.toLocaleLowerCase().trim();
  searchResults.replaceChildren();
  if (!query || $('dashboard').hidden) {
    closePageSearch();
    return;
  }
  const matches = PAGE_SEARCH.filter(item =>
    (item.label + ' ' + item.terms).toLocaleLowerCase().includes(query));
  for (const item of matches) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'search-result';
    button.setAttribute('role', 'option');
    button.textContent = item.label;
    button.addEventListener('click', () => {
      closePageSearch();
      pageSearch.value = '';
      location.hash = '#' + item.tab;
      showTab();
    });
    searchResults.append(button);
  }
  if (!matches.length) {
    const empty = document.createElement('p');
    empty.textContent = 'No matching page or control.';
    searchResults.append(empty);
  }
  searchResults.hidden = false;
  pageSearch.setAttribute('aria-expanded', 'true');
}
pageSearch.addEventListener('input', renderPageSearch);
pageSearch.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    closePageSearch();
    pageSearch.blur();
  } else if (event.key === 'Enter' && !searchResults.hidden) {
    event.preventDefault();
    searchResults.querySelector('button')?.click();
  }
});
document.addEventListener('keydown', event => {
  if ((event.key === '/' && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName))
      || ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k')) {
    if ($('dashboard').hidden) return;
    event.preventDefault();
    pageSearch.focus();
  }
});
document.addEventListener('click', event => {
  if (!event.target.closest('.jump-search')) closePageSearch();
});
window.addEventListener('hashchange', closePageSearch);
$('session-refresh').addEventListener('click', () => $('refresh').click());
$('session-logout').addEventListener('click', () => $('logout').click());
$('menu-toggle').addEventListener('click', () => {
  const open = document.body.classList.toggle('sidebar-open');
  $('menu-toggle').setAttribute('aria-expanded', String(open));
  $('menu-toggle').setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && document.body.classList.contains('sidebar-open')) {
    document.body.classList.remove('sidebar-open');
    $('menu-toggle').setAttribute('aria-expanded', 'false');
    $('menu-toggle').focus();
  }
});
showTab();

function renderOverviewGuilds() {
  $('guilds-summary').textContent = guildsLoaded ? String(joinedGuilds.length) + ' joined' : 'Checking';
  $('guilds-detail').textContent = guildsLoaded
    ? 'Bot membership reported by Discord' : 'Waiting for Discord server list';
  const guild = joinedGuilds.find(item => item.id === currentBotSettings.guild_id);
  $('overview-guild').textContent = guild?.name || currentBotSettings.guild_id || 'Not configured';
}

function renderOverviewSession() {
  const bot = currentBotSettings;
  const connected = liveVoiceSession?.connected === true &&
    liveVoiceSession.guild_id === bot.guild_id;
  setTone($('overview-health-voice'), liveVoiceSession === null
    ? 'Not verified' : connected ? 'Connected' : 'Not connected',
    liveVoiceSession === null ? 'muted' : connected ? 'good' : 'bad');
  const providerName = selectedProvider === 'off' ? 'Off' :
    selectedProvider === 'molab' ? 'Molab' : 'Colab';
  $('overview-voice-state').textContent = selectedProvider === 'off'
    ? 'Voice off' : providerName + (selectedVoiceOnline === true ? ' ready' :
      selectedVoiceOnline === false ? ' unavailable' : ' checking');
  $('overview-voice-note').textContent = connected
    ? 'Discord voice session connected. TTS availability is checked separately.'
    : 'Discord voice session not confirmed. Check the selected server.';
}

function renderOverview(data) {
  const services = Array.isArray(data.services) ? data.services : [];
  const running = services.filter(item => String(item?.state || '').toLowerCase() === 'running' ||
    String(item?.state || '').toLowerCase().startsWith('up')).length;
  const bridge = services.find(item => item.service === 'bridge');
  setTone($('overview-bridge-state'), bridge?.state || 'Not reported',
    !bridge || bridge.state === 'unknown' || bridge.state === 'unavailable' ? 'muted' :
      String(bridge.state).toLowerCase() === 'running' ||
        String(bridge.state).toLowerCase().startsWith('up') ? 'good' : 'bad');
  $('overview-bridge-note').textContent = 'Docker container status only. A running container does not prove Discord gateway or voice connectivity.';
  setTone($('overview-health-services'), services.length
    ? running + ' / ' + services.length + ' running' : 'Not reported',
    !services.length ? 'muted' : running === services.length ? 'good' : 'warning');
  $('header-health').lastChild.textContent = services.length
    ? running + ' / ' + services.length + ' containers running' : 'Container state unavailable';
  $('header-health').dataset.state = services.length && running === services.length
    ? 'ready' : 'unknown';
  const bot = data.bot || {};
  const channel = $('voice_channel_id');
  const selected = bot.guild_id && bot.guild_id === selectedGuildId && channel.value === bot.voice_channel_id
    ? channel.selectedOptions[0]?.textContent : '';
  $('overview-channel').textContent = selected || bot.voice_channel_id || 'Not configured';
  const providerName = selectedProvider === 'molab' ? 'Molab' :
    selectedProvider === 'colab' ? 'Colab' : 'Off';
  setTone($('overview-health-provider'), selectedProvider === 'off'
    ? 'Off (selected)' : providerName + (selectedVoiceOnline === true ? ' · ready' :
      selectedVoiceOnline === false ? ' · unavailable' : ' · checking'),
    selectedProvider === 'off' ? 'muted' : selectedVoiceOnline === true ? 'good' :
      selectedVoiceOnline === false ? 'bad' : 'warning');
  renderOverviewGuilds();
  renderOverviewSession();
}

function clearNotification() {
  if (notificationTimer !== null) clearTimeout(notificationTimer);
  notificationTimer = null;
  const toast = $('status-message');
  toast.hidden = true;
  $('toast-text').textContent = '';
}

function message(value, failed = false, pending = false) {
  if (notificationTimer !== null) clearTimeout(notificationTimer);
  notificationTimer = null;
  const token = ++notificationGeneration;
  const toast = $('status-message');
  if (!value) {
    clearNotification();
    return;
  }
  $('toast-text').textContent = value;
  toast.className = failed ? 'status-message error' : 'status-message';
  toast.hidden = false;
  if (!pending) {
    notificationTimer = setTimeout(() => {
      if (notificationGeneration === token) clearNotification();
    }, failed ? 9000 : 5000);
  }
}
$('dismiss-toast').addEventListener('click', () => {
  ++notificationGeneration;
  clearNotification();
});

function fact(label, value) {
  const item = document.createElement('div');
  item.className = 'fact';
  const title = document.createElement('span');
  title.className = 'label';
  title.textContent = label;
  const info = document.createElement('span');
  info.className = 'value';
  info.textContent = value;
  item.append(title, info);
  return item;
}

function bytes(value) {
  if (!Number.isFinite(value)) return 'Unavailable';
  let size = value;
  for (const unit of ['B', 'KiB', 'MiB', 'GiB', 'TiB']) {
    if (size < 1024) return size.toFixed(size >= 100 ? 0 : 1) + ' ' + unit;
    size /= 1024;
  }
  return size.toFixed(1) + ' PiB';
}

async function request(path, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), path === '/api/status' ? 35000 : 25000);
  try {
    const response = await fetch(path, {
      credentials: 'same-origin', cache: 'no-store', ...options,
      signal: controller.signal,
    });
    const type = response.headers.get('content-type') || '';
    const body = type.includes('application/json') ? await response.json() : {};
    if (!response.ok) {
      const detail = body.error || (response.status >= 500
        ? 'VPS connection failed (HTTP ' + response.status + '). Check controls service.'
        : 'Request failed (HTTP ' + response.status + ')');
      throw new Error(detail);
    }
    return body;
  } catch (error) {
    if (error.name === 'AbortError') throw new Error('VPS request timed out. Check the service status.');
    if (error instanceof TypeError) throw new Error('Cannot connect to controls. Check VPS and network.');
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

async function saveBotConfig(values, endpoint = '/api/bot-config') {
  const submitted = await request(endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
    body: JSON.stringify(values),
  });
  if (!submitted.job_id) return submitted; // Supports older servers during rollout.
  let failures = 0;
  for (let poll = 0; poll < 160; poll += 1) {
    await new Promise(resolve => setTimeout(resolve, 800));
    let job;
    try {
      job = await request('/api/config-jobs/' + encodeURIComponent(submitted.job_id));
      failures = 0;
    } catch (error) {
      if (error.message === 'Sign in required') throw error;
      if (++failures > 4) throw new Error('Lost connection while saving. Refresh status before retrying.');
      continue; // Retry reads, never resend a configuration-changing POST.
    }
    if (job.state === 'done') return job;
    if (job.state === 'failed') throw new Error(job.error || 'Settings could not be applied.');
  }
  throw new Error('Settings are taking longer than expected. Refresh status before retrying.');
}

function providerTone(entry) {
  if (!entry?.selected) return { text: 'Standby', cls: '' };
  if (entry.online === true) return { text: 'Ready', cls: 'ready' };
  if (entry.online === false) return { text: 'Unavailable', cls: 'offline' };
  return { text: 'Checking', cls: 'pending' };
}

function setTone(element, text, tone) {
  element.textContent = text;
  element.className = tone ? 'tone-' + tone : '';
}

function renderSnapshot(data) {
  const services = Array.isArray(data.services) ? data.services : [];
  const isRunning = entry => String(entry?.state || '').toLowerCase() === 'running' ||
    String(entry?.state || '').toLowerCase().startsWith('up');
  const bridge = services.find(entry => entry.service === 'bridge');
  const bridgeRunning = isRunning(bridge);
  const bridgeUnknown = !bridge || bridge.state === 'unknown' || bridge.state === 'unavailable';
  const running = services.filter(isRunning).length;
  const expected = services.length;
  const provider = data.selected_provider || 'off';
  const chosen = provider === 'molab' ? data.providers?.Molab :
    provider === 'colab' ? data.providers?.Colab : null;
  const ready = chosen?.online === true;
  const unreachable = chosen?.online === false;
  const providerName = provider === 'molab' ? 'Molab' : provider === 'colab' ? 'Colab' : 'Off';
  setTone($('bridge-summary'), bridgeUnknown ? 'Unknown' : bridgeRunning ? 'Running' : 'Stopped',
    bridgeUnknown ? 'muted' : bridgeRunning ? 'good' : 'bad');
  $('bridge-detail').textContent = bridgeRunning
    ? 'Docker container running · gateway unverified'
    : bridgeUnknown ? 'Docker state could not be read' : 'Bridge needs attention';
  setTone($('voice-summary'), provider === 'off' ? 'Off · intentional' :
    ready ? providerName + ' · ready' : unreachable ? providerName + ' · offline' :
      providerName + ' · checking', provider === 'off' ? 'muted' :
        ready ? 'good' : unreachable ? 'bad' : 'warning');
  $('voice-detail').textContent = provider === 'off' ? 'Voice disabled; text still works' :
    ready ? 'Selected notebook responded to health check' :
      unreachable ? 'No fallback · start the selected notebook' :
        'Awaiting selected notebook health';
  setTone($('services-summary'), expected ? running + ' / ' + expected + ' running' :
    'No report', !expected ? 'muted' : running === expected ? 'good' : 'warning');
  $('services-detail').textContent = 'Latest Docker container state';

  const alert = $('attention');
  const title = $('attention-title');
  const copy = $('attention-copy');
  const link = $('attention-link');
  const setAlert = (tone, marker, heading, detail, label, target) => {
    alert.dataset.tone = tone;
    $('attention-mark').textContent = marker;
    title.textContent = heading;
    copy.textContent = detail;
    link.firstChild.textContent = label + ' ';
    link.href = target;
  };
  if (bridgeUnknown) {
    setAlert('warn', '!', 'Bridge state could not be verified',
      'The Docker status command did not return a usable bridge state. Refresh or inspect VPS services.',
      'View containers', '#vps');
  } else if (!bridgeRunning) {
    setAlert('bad', '!', 'Discord bridge needs attention',
      'The bridge container is not reported as running. Check the bridge controls before expecting speech.',
      'Manage bridge', '#discord');
  } else if (unreachable) {
    setAlert('warn', '!', providerName + ' is unavailable',
      'Start the selected standalone notebook and its tunnel. The other provider will not be used.',
      'View voice route', '#voice');
  } else if (expected && running < expected) {
    setAlert('warn', '!', 'One or more VPS services need attention',
      'Check the container list below. Voice availability is a separate health check.',
      'View containers', '#vps');
  } else if (provider === 'off') {
    setAlert('neutral', 'i', 'Voice is intentionally off',
      'Text chat remains available; enable exactly one notebook above to restore spoken replies.',
      'Choose a provider', '#voice');
  } else if (!ready) {
    setAlert('neutral', 'i', 'Voice health is not confirmed',
      'Only the selected notebook is checked. Retry the health check or inspect its session.',
      'View voice route', '#voice');
  } else {
    setAlert('good', '✓', 'Checked services are available',
      'Docker reports the containers running and the selected notebook passed health. Discord voice connection is not independently verified.',
      'View containers', '#vps');
  }
  const badge = $('bridge-badge');
  badge.textContent = bridgeUnknown ? 'State unknown' : bridgeRunning ? 'Container running' : 'Container stopped';
  badge.className = 'status-badge' + (bridgeUnknown ? '' : bridgeRunning ? ' ready' : ' offline');
}

function renderServices(data) {
  const stats = data.docker_stats || {};
  $('services').replaceChildren(...data.services.map(entry => {
    const row = document.createElement('div');
    const state = String(entry.state || 'unavailable').toLowerCase();
    const running = state === 'running' || state.startsWith('up');
    row.className = 'service-row ' + (running ? 'running' :
      (state === 'unknown' || state === 'unavailable' ? 'unknown' : 'stopped'));
    const name = document.createElement('span');
    name.className = 'service-name';
    name.textContent = entry.service.toUpperCase();
    const status = document.createElement('span');
    status.className = 'service-state';
    const dot = document.createElement('i');
    dot.className = 'state-dot';
    const stateText = document.createElement('span');
    stateText.textContent = running ? 'Running' : (state === 'unknown' || state === 'unavailable' ? 'Status unknown' : entry.state || 'Status unknown');
    status.append(dot, stateText);
    const usage = document.createElement('span');
    usage.className = 'service-stat';
    const detail = stats['tsukiai-' + entry.service + '-1'];
    usage.textContent = detail ? detail.cpu + ' CPU · ' + detail.memory : (entry.status || 'No runtime data');
    row.append(name, status, usage);
    return row;
  }));
}

function renderSystem(system) {
  const values = [
    ['Uptime', (system.uptime_seconds / 3600).toFixed(1) + ' h'],
    ['Load 1 / 5 / 15', system.load.map(v => v.toFixed(2)).join(' / ')],
    ['RAM used', bytes(system.memory_total - system.memory_available) + ' / ' + bytes(system.memory_total)],
    ['Disk used', bytes(system.disk_used) + ' / ' + bytes(system.disk_total)],
  ];
  $('system').replaceChildren(...values.map(([label, value]) => {
    const item = document.createElement('div');
    item.className = 'system-stat';
    const l = document.createElement('span');
    l.textContent = label;
    const v = document.createElement('strong');
    v.textContent = value;
    item.append(l, v);
    return item;
  }));
}

function renderProviders(data) {
  if (providerChanging) return;
  selectedProvider = data.selected_provider || 'off';
  const current = selectedProvider === 'molab' ? data.providers?.Molab :
    selectedProvider === 'colab' ? data.providers?.Colab : null;
  selectedVoiceOnline = current?.online ?? null;
  const indicator = $('voice-health-indicator');
  const healthText = $('voice-health-text');
  indicator.textContent = selectedProvider === 'off' ? '—' :
    selectedVoiceOnline === true ? '●' : selectedVoiceOnline === false ? '○' : '◌';
  indicator.className = 'voice-health-indicator ' + (selectedProvider === 'off' ? 'disabled' :
    selectedVoiceOnline === true ? 'ready' :
      selectedVoiceOnline === false ? 'disconnected' : 'pending');
  $('provider-feedback').parentElement.dataset.state = selectedProvider === 'off' ? 'off' :
    selectedVoiceOnline === true ? 'ready' :
      selectedVoiceOnline === false ? 'offline' : 'pending';
  healthText.textContent = selectedProvider === 'off'
    ? 'Voice disabled · connection polling paused'
    : selectedVoiceOnline === true
      ? selectedProvider + ' connected · checking every 30 seconds'
      : selectedVoiceOnline === false
        ? selectedProvider + ' unreachable · retrying every 10 seconds · no fallback'
        : 'Checking selected ' + selectedProvider + ' connection…';
  const radio = document.querySelector(`input[name="tts_provider"][value="${selectedProvider}"]`);
  if (radio) radio.checked = true;
  if (!$('guild-config').hidden) renderLiveVoiceControls();
  for (const [name, id] of [['Molab', 'molab-status'], ['Colab', 'colab-status']]) {
    const tone = providerTone(data.providers?.[name]);
    $(id).textContent = tone.text;
    $(id).className = tone.cls;
  }
}

function renderExclusiveInferenceToggles() {
  const conflicting = ['groq', 'literouter'].every(name => inferenceSaved.includes(name));
  for (const provider of ['groq', 'literouter']) {
    const enabled = inferenceSaved.includes(provider);
    const toggle = $(provider + '-provider-toggle');
    toggle.textContent = enabled ? 'Enabled' : 'Disabled';
    toggle.setAttribute('aria-pressed', String(enabled));
    toggle.setAttribute('aria-label',
      (enabled ? 'Disable ' : 'Enable ') + (provider === 'groq' ? 'Groq' : 'LiteRouter'));
    toggle.disabled = !inferenceLoaded || inferenceEditing;
    $(provider + '-provider-state').textContent = conflicting
      ? 'Both configured · select one' : enabled ? 'Active' : 'Inactive';
  }
}

async function toggleExclusiveInferenceProvider(provider) {
  if (!inferenceLoaded || inferenceEditing) return;
  if (inferenceDraft.join(',') !== inferenceSaved.join(',')) {
    message('Save or discard unsaved advanced provider changes first.', true);
    return;
  }
  const enabled = inferenceSaved.includes(provider);
  const next = enabled
    ? inferenceSaved.filter(name => name !== provider)
    : [...inferenceSaved.filter(name => name !== 'groq' && name !== 'literouter'), provider];
  if (!next.length) {
    message('At least one API inference provider must remain enabled. Enable the other provider to switch.', true);
    return;
  }
  if (!enabled && provider === 'literouter') {
    const details = inferenceCatalog.find(item => item.id === 'literouter');
    if (!details?.credential_configured || !details?.selected_model) {
      $('literouter-provider-details').open = true;
      message('Add a LiteRouter API key and select a listed LiteRouter model before enabling it.', true);
      return;
    }
  }
  inferenceEditing = true;
  renderInferenceProviders();
  try {
    await saveBotConfig({ providers: next }, '/api/inference-providers');
    inferenceSaved = [...next];
    inferenceDraft = [...next];
    message((provider === 'groq' ? 'Groq' : 'LiteRouter') +
      (enabled ? ' disabled.' : ' enabled; the other provider is disabled.'));
  } catch (error) {
    message(error.message, true);
  } finally {
    inferenceEditing = false;
    renderInferenceProviders();
    await loadInferenceProviders(true);
  }
}

function renderInferenceProviders() {
  const list = $('inference-provider-list');
  list.replaceChildren();
  const selected = new Set(inferenceDraft);
  const select = $('inference-add-provider');
  const previous = select.value;
  select.replaceChildren(new Option('Choose an API provider…', ''));
  for (const item of inferenceCatalog) {
    if (!selected.has(item.id)) select.add(new Option(item.id.toUpperCase(), item.id));
  }
  if ([...select.options].some(option => option.value === previous)) select.value = previous;
  for (const [index, name] of inferenceDraft.entries()) {
    const entry = inferenceCatalog.find(item => item.id === name) || { id: name };
    const row = document.createElement('div');
    row.className = 'inference-provider-row';
    row.setAttribute('role', 'listitem');
    const copy = document.createElement('span');
    const title = document.createElement('strong');
    title.textContent = (index + 1) + '. ' + name.toUpperCase();
    const detail = document.createElement('small');
    detail.textContent = (entry.model || 'Model unverified') + ' · ' +
      (entry.credential_configured
        ? 'Key ' + (entry.credential_masked || 'configured')
        : 'Key not configured');
    copy.append(title, detail);
    const actions = document.createElement('span');
    actions.className = 'inference-row-actions';
    for (const [label, delta] of [['↑', -1], ['↓', 1]]) {
      const button = document.createElement('button');
      button.className = 'quiet'; button.type = 'button'; button.textContent = label;
      button.setAttribute('aria-label', (delta < 0 ? 'Move ' : 'Move ') + name +
        (delta < 0 ? ' up' : ' down'));
      button.disabled = index + delta < 0 || index + delta >= inferenceDraft.length || inferenceEditing;
      button.addEventListener('click', () => {
        const updated = [...inferenceDraft];
        [updated[index], updated[index + delta]] = [updated[index + delta], updated[index]];
        inferenceDraft = updated; renderInferenceProviders();
      });
      actions.append(button);
    }
    const remove = document.createElement('button');
    remove.className = 'quiet'; remove.type = 'button'; remove.textContent = 'Remove';
    remove.disabled = inferenceDraft.length <= 1 || inferenceEditing;
    remove.setAttribute('aria-label', 'Remove ' + name);
    remove.addEventListener('click', () => {
      inferenceDraft = inferenceDraft.filter(item => item !== name);
      renderInferenceProviders();
    });
    actions.append(remove);
    if (name !== 'groq' && name !== 'literouter') {
      const setKey = document.createElement('button');
      setKey.className = 'quiet'; setKey.type = 'button';
      setKey.textContent = 'Set key';
      setKey.disabled = inferenceEditing;
      setKey.setAttribute('aria-label', 'Set API key for ' + name);
      setKey.addEventListener('click', () => {
        const prior = row.querySelector('form');
        if (prior) { prior.remove(); return; }
        const form = document.createElement('form');
        form.className = 'groq-add-form inference-key-form';
        const field = document.createElement('input');
        field.type = 'password'; field.autocomplete = 'new-password';
        field.maxLength = 512; field.required = true;
        field.setAttribute('aria-label', 'API key for ' + name);
        field.placeholder = name.toUpperCase() + ' API key';
        const apply = document.createElement('button');
        apply.className = 'quiet strong'; apply.type = 'submit';
        apply.textContent = 'Save key';
        form.append(field, apply);
        form.addEventListener('submit', async event => {
          event.preventDefault();
          if (!field.value.trim() || inferenceEditing) return;
          inferenceEditing = true;
          apply.disabled = true;
          try {
            await saveBotConfig({ provider: name, key: field.value.trim() },
              '/api/inference-key');
            field.value = '';
            message(name.toUpperCase() + ' key saved privately.');
          } catch (error) {
            message(error.message, true);
          } finally {
            field.value = '';
            inferenceEditing = false;
            void loadInferenceProviders(true);
          }
        });
        row.append(form);
        field.focus();
      });
      actions.append(setKey);
      if (entry.credential_configured) {
        const revoke = document.createElement('button');
        revoke.className = 'quiet'; revoke.type = 'button';
        revoke.textContent = 'Clear key';
        revoke.setAttribute('aria-label', 'Clear API key for ' + name);
        revoke.addEventListener('click', async () => {
          if (inferenceEditing || !confirm('Clear the saved ' + name + ' key?')) return;
          inferenceEditing = true;
          try {
            await saveBotConfig({ provider: name, action: 'remove' },
              '/api/inference-key');
            message(name.toUpperCase() + ' API key cleared.');
          } catch (error) {
            message(error.message, true);
          } finally {
            inferenceEditing = false;
            void loadInferenceProviders(true);
          }
        });
        actions.append(revoke);
      }
    }
    row.append(copy, actions);
    list.append(row);
  }
  if (!inferenceDraft.length) list.textContent = 'No configured API inference providers.';
  const dirty = inferenceDraft.join(',') !== inferenceSaved.join(',');
  $('inference-save').disabled = !dirty || !inferenceDraft.length || inferenceEditing;
  $('inference-add').disabled = !select.value || inferenceEditing;
  $('inference-status').textContent = inferenceEditing ? 'Applying provider settings…'
    : dirty ? 'Unsaved provider order · save to apply to C# inference.'
      : inferenceSaved.length ? 'Configured provider order · inference availability unverified.'
        : 'Provider settings unavailable.';
  renderExclusiveInferenceToggles();
}

function renderInferenceModel(provider) {
  const saved = inferenceModelSaved[provider];
  const models = inferenceModels[provider];
  const select = $(provider + '-model-select');
  const selected = select.value;
  const busy = inferenceModelBusy[provider];
  const active = inferenceSaved.includes(provider);
  $(provider + '-model-current').textContent = saved
    ? (active ? 'Current model: ' : 'Saved model (inactive): ') + saved
    : (provider === 'literouter'
        ? 'Not configured · choose and save a free model'
        : 'Not configured · fetch the Groq model catalog');
  // Never convert an unverified saved ID into a catalog option. Only model
  // IDs returned by the selected key's read-only GET /models are selectable.
  select.replaceChildren(new Option(
    models.length ? 'Choose an available model…' : 'Fetch available models first', ''));
  models.forEach(model => select.add(new Option(model, model)));
  select.value = models.includes(selected) ? selected :
    models.includes(saved) ? saved : '';
  select.disabled = busy || !models.length;
  $(provider + '-model-load').disabled = busy;
  $(provider + '-model-save').disabled = busy || !select.value ||
    !models.includes(select.value) || select.value === saved;
}

async function loadInferenceModels(provider) {
  if (!Object.hasOwn(inferenceModels, provider) ||
      inferenceModelBusy[provider]) return;
  inferenceModelBusy[provider] = true;
  renderInferenceModel(provider);
  const feedback = $(provider + '-model-status');
  feedback.textContent = 'Reading the provider model catalog · no prompt sent…';
  try {
    const state = await request('/api/inference-models?provider=' +
      encodeURIComponent(provider));
    const ids = Array.isArray(state.models) ? state.models : [];
    inferenceModels[provider] = provider === 'literouter'
      ? ids.filter(id => typeof id === 'string' && id.toLowerCase().endsWith(':free'))
      : ids;
    feedback.textContent = inferenceModels[provider].length +
      (provider === 'literouter' ? ' free model IDs · ' : ' available model IDs · ') +
      'read-only listing; no model generation or test prompt.';
    if (inferenceModelSaved[provider] &&
        !inferenceModels[provider].includes(inferenceModelSaved[provider])) {
      feedback.textContent += ' Saved model not found in this listing.';
    }
  } catch (error) {
    inferenceModels[provider] = [];
    feedback.textContent = 'Cannot verify available models: ' + error.message;
  } finally {
    inferenceModelBusy[provider] = false;
    renderInferenceModel(provider);
  }
}

async function saveInferenceModel(provider) {
  if (!Object.hasOwn(inferenceModels, provider) ||
      inferenceModelBusy[provider]) return;
  const model = $(provider + '-model-select').value;
  if (!inferenceModels[provider].includes(model) ||
      (provider === 'literouter' && !model.toLowerCase().endsWith(':free')) ||
      model === inferenceModelSaved[provider]) return;
  inferenceModelBusy[provider] = true;
  renderInferenceModel(provider);
  $(provider + '-model-status').textContent = 'Saving listed model · reconnecting API…';
  try {
    await saveBotConfig({ provider, model }, '/api/inference-model');
    inferenceModelSaved[provider] = model;
    $(provider + '-model-status').textContent = 'Saved: ' + model +
      ' · runtime inference not verified (no test prompt sent).';
    message(provider.toUpperCase() + ' inference model updated.');
  } catch (error) {
    $(provider + '-model-status').textContent = 'Model not applied: ' + error.message;
    message(error.message, true);
  } finally {
    inferenceModelBusy[provider] = false;
    renderInferenceModel(provider);
    void loadInferenceProviders(true);
  }
}

async function loadInferenceProviders(force = false) {
  if (inferenceLoading || inferenceEditing || $('dashboard').hidden) return;
  if (!force && inferenceCatalog.length) return;
  inferenceLoading = true;
  try {
    const state = await request('/api/inference-providers');
    inferenceCatalog = Array.isArray(state.providers) ? state.providers : [];
    inferenceSaved = Array.isArray(state.selected) ? state.selected : [];
    inferenceLoaded = true;
    inferenceDraft = [...inferenceSaved];
    for (const provider of Object.keys(inferenceModelSaved)) {
      inferenceModelSaved[provider] =
        inferenceCatalog.find(item => item.id === provider)?.selected_model || '';
      renderInferenceModel(provider);
    }
    if (state.legacy_override && state.legacy_override !== 'api') {
      $('inference-status').textContent =
        'An old notebook LLM override is configured. Saving API providers restores C# inference.';
    }
    renderInferenceProviders();
  } catch (error) {
    inferenceLoaded = false;
    renderExclusiveInferenceToggles();
    $('inference-status').textContent = 'Cannot read inference providers: ' + error.message;
  } finally {
    inferenceLoading = false;
  }
}

async function loadGroqKeys() {
  if ($('dashboard').hidden || groqUpdating) return;
  try {
    const state = await request('/api/groq-keys');
    const keys = Array.isArray(state.keys) ? state.keys : [];
    $('groq-total-calls').textContent = Number.isSafeInteger(state.calls_today)
      ? state.calls_today.toLocaleString() : 'Unavailable';
    $('groq-calls-coverage').textContent = state.coverage === 'complete'
      ? state.date + ' · ' + state.timezone + ' · recorded API attempts'
      : 'Daily call history is not yet verifiable · ' +
        (Number.isSafeInteger(state.recorded_calls_today)
          ? state.recorded_calls_today.toLocaleString() + ' observed attempts · '
          : '') + 'no estimated totals';
    const list = $('groq-key-list');
    list.replaceChildren();
    for (const [index, key] of keys.entries()) {
      const row = document.createElement('div');
      row.className = 'groq-key-row';
      row.setAttribute('role', 'listitem');
      const label = document.createElement('span');
      const masked = document.createElement('strong');
      masked.textContent = key.masked;
      const usage = document.createElement('small');
      usage.textContent = 'API calls today: ' +
        (Number.isSafeInteger(key.calls_today) ? key.calls_today : 'Unavailable') +
        (!Number.isSafeInteger(key.calls_today) &&
         Number.isSafeInteger(key.recorded_calls_today)
          ? ' · ' + key.recorded_calls_today + ' observed' : '');
      label.append(masked, usage);
      const remove = document.createElement('button');
      remove.type = 'button'; remove.className = 'quiet'; remove.textContent = 'Remove';
      remove.disabled = groqUpdating;
      remove.setAttribute('aria-label', 'Remove masked key ' + key.masked);
      remove.addEventListener('click', () => void mutateGroqKey({ action: 'remove', id: key.id }));
      row.append(label, remove);
      list.append(row);
    }
    if (!keys.length) list.textContent = 'No Groq keys configured.';
    $('groq-key-status').textContent = state.total_keys + ' configured Groq API key(s).';
  } catch (error) {
    $('groq-key-status').textContent = 'Groq inventory unavailable: ' + error.message;
    $('groq-total-calls').textContent = 'Unavailable';
    $('groq-calls-coverage').textContent = 'Cannot verify usage';
  }
}

async function mutateGroqKey(payload) {
  if (groqUpdating) return;
  groqUpdating = true;
  const add = $('groq-add-form').querySelector('button[type=submit]');
  add.disabled = true;
  $('groq-key-status').textContent = 'Applying Groq keys…';
  try {
    await saveBotConfig(payload, '/api/groq-keys');
    $('groq-new-key').value = '';
    message('Groq keys updated. API and bridge reconnected.');
  } catch (error) {
    message(error.message, true);
    $('groq-key-status').textContent = 'Key update failed. Refresh and inspect the current pool.';
  } finally {
    groqUpdating = false;
    add.disabled = false;
    await loadGroqKeys();
    void loadInferenceProviders(true);
  }
}

function toggleLiteRouterKeyForm(open) {
  const form = $('literouter-key-form');
  const button = $('literouter-key-toggle');
  form.hidden = !open;
  button.setAttribute('aria-expanded', String(open));
  button.setAttribute('aria-label', open ? 'Close LiteRouter key form' : 'Add LiteRouter API key');
  button.textContent = open ? '− Cancel' : '+ Add key';
  if (open) $('literouter-model-key').focus();
  else $('literouter-model-key').value = '';
}

async function loadLiteRouterKeys() {
  if ($('dashboard').hidden || literouterUpdating) return;
  const list = $('literouter-key-list');
  const status = $('literouter-key-preview');
  try {
    const state = await request('/api/literouter-keys');
    list.replaceChildren();
    $('literouter-model-key').disabled = !state.managed;
    $('literouter-key-toggle').disabled = !state.managed;
    $('literouter-key-form').querySelector('button[type=submit]').disabled = !state.managed;
    if (!state.managed) {
      toggleLiteRouterKeyForm(false);
      status.textContent = state.status || 'External LiteRouter key file is managed separately.';
      list.textContent = 'External key pool; dashboard cannot safely change its keys.';
      return;
    }
    const keys = Array.isArray(state.keys) ? state.keys : [];
    for (const key of keys) {
      const row = document.createElement('div');
      row.className = 'groq-key-row';
      row.setAttribute('role', 'listitem');
      const masked = document.createElement('strong');
      masked.textContent = key.masked;
      const remove = document.createElement('button');
      remove.type = 'button'; remove.className = 'quiet'; remove.textContent = 'Remove';
      remove.disabled = literouterUpdating;
      remove.setAttribute('aria-label', 'Remove LiteRouter key ' + key.masked);
      remove.addEventListener('click', () =>
        void mutateLiteRouterKey({ action: 'remove', id: key.id }));
      row.append(masked, remove);
      list.append(row);
    }
    if (!keys.length) list.textContent = 'No LiteRouter API keys configured.';
    status.textContent = state.total_keys + ' LiteRouter key(s) configured. Each key is masked.';
  } catch (error) {
    list.replaceChildren();
    status.textContent = 'LiteRouter key inventory unavailable: ' + error.message;
  }
}

async function mutateLiteRouterKey(payload) {
  if (literouterUpdating) return;
  literouterUpdating = true;
  const form = $('literouter-key-form');
  const add = form.querySelector('button[type=submit]');
  add.disabled = true;
  $('literouter-key-preview').textContent = 'Applying LiteRouter key change…';
  try {
    await saveBotConfig(payload, '/api/literouter-keys');
    if (payload.action === 'add') toggleLiteRouterKeyForm(false);
    message('LiteRouter key pool updated. The API has reconnected.');
  } catch (error) {
    message(error.message, true);
    $('literouter-key-preview').textContent = 'Key update failed: ' + error.message;
  } finally {
    literouterUpdating = false;
    add.disabled = false;
    await loadLiteRouterKeys();
    if (payload.action === 'add') {
      // Confirm the saved key can read this account's catalog without sending
      // a prompt. This does not choose a model or enable the provider.
      await loadInferenceModels('literouter');
    }
    void loadInferenceProviders(true);
  }
}

// Session-scoped, source-backed voice controls and metrics. Never invent a
// running average when its denominator is zero or the provider is offline.
let focusSnapshotCache = null;
let focusRequestSerial = 0;
function renderFocusSnapshot(state) {
  focusSnapshotCache = state;
  const focused = Array.isArray(state.user_ids) ? state.user_ids : [];
  const members = Array.isArray(state.members) ? state.members : [];
  $('voice-focus-status').textContent = focused.length
    ? 'Listening only to ' + focused.length + ' selected user(s)'
    : state.channel_id ? 'Listening to everyone in the connected voice channel'
      : 'No manual focus; Tsuki will listen to everyone when connected';
  const list = $('voice-focus-list');
  list.replaceChildren();
  if (!focused.length) {
    const empty = document.createElement('span');
    empty.className = 'section-hint';
    empty.textContent = 'No manual focus. Everyone can be heard.';
    list.append(empty);
  }
  for (const id of focused) {
    const name = members.find(member => member.id === id)?.name || id;
    const entry = document.createElement('div');
    entry.className = 'voice-focus-entry';
    const text = document.createElement('span');
    text.textContent = name + ' · ' + id;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'quiet';
    button.textContent = 'Unfocus';
    button.setAttribute('aria-label', 'Stop focusing on ' + name);
    button.addEventListener('click', () => void updateVoiceFocus('remove', id));
    entry.append(text, button);
    list.append(entry);
  }
  const select = $('voice-focus-user');
  const previous = select.value;
  select.replaceChildren();
  const placeholder = new Option('Choose a listener…', '');
  select.add(placeholder);
  for (const member of members) {
    select.add(new Option(member.name + ' (' + member.id + ')', member.id));
  }
  if (members.some(member => member.id === previous)) select.value = previous;
  $('focus-clear').disabled = focused.length === 0;
}
async function refreshVoiceFocus() {
  const serial = ++focusRequestSerial;
  try {
    const state = await request('/api/voice-focus');
    if (serial !== focusRequestSerial || $('dashboard').hidden) return;
    renderFocusSnapshot(state);
  } catch (error) {
    if (serial !== focusRequestSerial) return;
    $('voice-focus-status').textContent = 'Focus unavailable: ' + error.message;
  }
}
async function updateVoiceFocus(action, id = '') {
  const guild = currentBotSettings.guild_id;
  if (!guild || selectedGuildId !== guild) {
    message('Select the active Discord server before changing focus.', true);
    return;
  }
  const payload = { guild_id: guild, action };
  if (action !== 'clear') payload.user_id = id;
  try {
    const state = await request('/api/voice-focus', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify(payload),
    });
    renderFocusSnapshot(state);
    $('voice-focus-id').value = '';
    message(action === 'add' ? 'Voice focus updated.' :
      action === 'clear' ? 'Tsuki now listens to everyone.' : 'Voice focus removed.');
  } catch (error) {
    message(error.message, true);
    await refreshVoiceFocus();
  }
}
$('focus-refresh').addEventListener('click', () => void refreshVoiceFocus());
$('voice-focus-disclosure').addEventListener('toggle', event => {
  if (event.currentTarget.open) void refreshVoiceFocus();
});
$('focus-add').addEventListener('click', () => {
  const id = $('voice-focus-id').value.trim() || $('voice-focus-user').value;
  if (!/^[0-9]{17,20}$/.test(id)) {
    message('Choose a listener or enter their 17–20 digit Discord user ID.', true);
    return;
  }
  void updateVoiceFocus('add', id);
});
$('focus-clear').addEventListener('click', () => {
  if (!confirm('Remove all focus restrictions and listen to everyone in voice?')) return;
  void updateVoiceFocus('clear');
});
$('reload-analytics').addEventListener('click', () => void refreshVoiceAnalytics());
function addMetric(parent, label, value) {
  const item = document.createElement('div');
  item.className = 'metric-fact';
  const title = document.createElement('span');
  title.textContent = label;
  const number = document.createElement('strong');
  number.textContent = value;
  item.append(title, number);
  parent.append(item);
}
async function refreshVoiceAnalytics() {
  const results = $('voice-analytics-results');
  results.textContent = 'Reading observed metrics…';
  let data;
  try {
    data = await request('/api/voice-analytics');
  } catch (error) {
    results.textContent = 'Bridge analytics unavailable: ' + error.message;
    return;
  }
  results.replaceChildren();
  const stages = data.stages || {};
  const average = (stage) => {
    const item = stages[stage];
    return item?.timed_samples > 0 && Number.isFinite(item.avg_ms)
      ? item.avg_ms + ' ms (' + item.timed_samples + ' measured)' : 'No timed samples';
  };
  addMetric(results, 'STT average', average('stt'));
  addMetric(results, 'LLM average', average('llm'));
  addMetric(results, 'TTS generation average', average('tts'));
  addMetric(results, 'TTS attempts / failures',
    (stages.tts?.attempts ?? 0) + ' / ' + (stages.tts?.failures ?? 0));
  addMetric(results, 'Completed VC replies', String(data.completed_replies ?? 0));
  addMetric(results, 'Busy / cooldown drops',
    (data.drops?.busy ?? 0) + ' / ' + (data.drops?.cooldown ?? 0));
  addMetric(results, 'Synthesized audio',
    (data.tts?.generated_audio_seconds ?? 0) + ' seconds');
  addMetric(results, 'Audio cache', data.tts?.audio_cache || 'Not reported');
  const engagement = $('engagement-metrics');
  engagement.replaceChildren();
  if (data.engagement) {
    const types = ['voice', 'chat'];
    for (const type of types) {
      const counts = data.engagement[type] || {};
      addMetric(engagement, type + ' direct / follow-up / proactive',
        [counts.direct ?? 0, counts.follow_up ?? 0, counts.proactive ?? 0].join(' / '));
      addMetric(engagement, type + ' background / uncertain / other-person / noise',
        [counts.background ?? 0, counts.uncertain ?? 0,
          counts.addressed_other ?? 0, counts.noise ?? 0].join(' / '));
    }
    const classifier = data.engagement.classifier || {};
    addMetric(engagement, 'Classifier attempts / failures',
      (classifier.attempts ?? 0) + ' / ' + (classifier.failures ?? 0));
    addMetric(engagement, 'Classifier average / measured',
      classifier.timed_samples > 0 ?
        (classifier.duration_ms_total / classifier.timed_samples).toFixed(1) +
          ' ms / ' + classifier.timed_samples : 'Unavailable · no samples');
    addMetric(engagement, 'Measured window', data.engagement.since || 'Unavailable');
  } else {
    engagement.textContent = 'Unavailable · running bridge has not reported engagement counters.';
  }
  let source = 'Bridge samples since ' + (data.since || 'unknown') +
    '; reset at bridge restart. STT/LLM timing is not provided by every route.';
  if (selectedProvider === 'molab') {
    try {
      const molab = await request('/api/molab-metrics');
      addMetric(results, 'Molab average generation / request',
        molab.successes && Number.isFinite(molab.avg_generation_ms)
          ? molab.avg_generation_ms + ' ms (' + molab.successes + ' samples)' : 'No samples');
      addMetric(results, 'Molab real-time factor',
        molab.successes && Number.isFinite(molab.real_time_factor)
          ? String(molab.real_time_factor) : 'No samples');
      addMetric(results, 'Molab queue average',
        molab.successes && Number.isFinite(molab.avg_queue_ms)
          ? molab.avg_queue_ms + ' ms' : 'No samples');
      addMetric(results, 'True GPU batch calls',
        Number.isInteger(molab.gpu_batch_calls)
          ? String(molab.gpu_batch_calls) : 'Not reported by running notebook');
      addMetric(results, 'Generated utterances',
        Number.isInteger(molab.synthesized_utterances)
          ? String(molab.synthesized_utterances) : 'Not reported by running notebook');
      source += ' Molab generation measured since notebook start; ' +
        'backend: ' + (molab.attention_backend || 'not reported') + '.';
    } catch {
      addMetric(results, 'Molab notebook', 'Metrics unavailable until updated notebook is running');
    }
  }
  $('voice-analytics-source').textContent = source +
    ' Full-ICL reference prompt is precomputed; phrase audio is not cached.';
}

function makeChannelOptions(element, items, current, kind) {
  const placeholder = document.createElement('option');
  placeholder.value = '';
  placeholder.textContent = kind === 'text'
    ? 'None · use voice-channel chat'
    : 'Choose a voice channel';
  const options = [placeholder];
  for (const item of items) {
    const option = document.createElement('option');
    option.value = item.id;
    option.textContent = '# ' + item.name;
    options.push(option);
  }
  if (current && !items.some(item => item.id === current)) {
    const unavailable = document.createElement('option');
    unavailable.value = current;
    unavailable.textContent = 'Configured channel no longer visible · ' + current;
    unavailable.disabled = true;
    options.push(unavailable);
  }
  element.replaceChildren(...options);
  element.value = current || '';
}

function renderLiveVoiceControls() {
  const active = Boolean(selectedGuildId &&
    selectedGuildId === currentBotSettings.guild_id);
  const connected = active && liveVoiceSession?.connected &&
    liveVoiceSession.guild_id === selectedGuildId;
  const status = $('live-voice-status');
  if (!active) {
    status.textContent = 'Live voice controls apply only to the active server. Save this server first.';
  } else if (liveVoiceSession === null) {
    status.textContent = 'Live voice status unverified. Check connection to enable actions.';
  } else if (connected) {
    const named = [...$('voice_channel_id').options].find(option =>
      option.value === liveVoiceSession.channel_id);
    status.textContent = 'Connected to ' + (named?.textContent ||
      'channel ' + liveVoiceSession.channel_id) + ' · live session, not the saved default.';
  } else {
    status.textContent = 'Tsuki is not connected to voice. Use /tjoin in Discord to rejoin.';
  }
  status.dataset.state = connected ? 'connected' : 'inactive';
  $('leave-voice').disabled = !connected || voiceOperationBusy;
  $('custom-tts-form').querySelector('button[type=submit]').disabled =
    !connected || voiceOperationBusy || selectedProvider === 'off';
}

async function refreshVoiceSession() {
  const serial = ++voiceSessionSerial;
  if (!selectedGuildId || $('dashboard').hidden) return;
  try {
    const state = await request('/api/voice-session');
    if (serial !== voiceSessionSerial) return;
    liveVoiceSession = state;
    renderLiveVoiceControls();
    renderOverviewSession();
  } catch (error) {
    if (serial !== voiceSessionSerial) return;
    liveVoiceSession = null;
    renderLiveVoiceControls();
    renderOverviewSession();
    if (selectedGuildId === currentBotSettings.guild_id) {
      $('live-voice-status').textContent = error.message;
    }
  }
}

function renderGuildButtons() {
  const list = $('guild-list');
  const buttons = joinedGuilds.map(guild => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'guild-choice' + (guild.id === selectedGuildId ? ' selected' : '');
    button.setAttribute('aria-pressed', guild.id === selectedGuildId ? 'true' : 'false');
    const emblem = document.createElement('span');
    emblem.className = 'guild-emblem';
    emblem.setAttribute('aria-hidden', 'true');
    emblem.textContent = guild.name.slice(0, 1).toUpperCase() || '?';
    const name = document.createElement('span');
    name.className = 'guild-choice-name';
    name.textContent = guild.name;
    const badge = document.createElement('small');
    badge.textContent = guild.id === currentBotSettings.guild_id ? 'ACTIVE' : 'JOINED';
    button.append(emblem, name, badge);
    button.addEventListener('click', () => void selectGuild(guild.id));
    return button;
  });
  list.replaceChildren(...buttons);
  if (!buttons.length) {
    const note = document.createElement('p');
    note.className = 'section-hint';
    note.textContent = 'Discord reports no joined servers for this bot.';
    list.append(note);
  }
}

async function selectGuild(id) {
  const guild = joinedGuilds.find(item => item.id === id);
  if (!guild) return;
  const serial = ++guildSelectionSerial;
  selectedGuildId = id;
  liveVoiceSession = null;
  ++voiceSessionSerial;
  renderGuildButtons();
  $('guild-config').hidden = false;
  $('guild-config-name').textContent = guild.name;
  $('guild-config-hint').textContent = 'Loading channels from Discord…';
  $('guild-config-state').textContent = 'CHECKING';
  $('guild-config-state').dataset.state = 'checking';
  $('bot-settings').querySelector('button[type=submit]').disabled = true;
  try {
    const channels = await request('/api/guilds/' + id + '/channels');
    if (serial !== guildSelectionSerial) return;
    const active = currentBotSettings.guild_id === id;
    $('guild_id').value = id;
    const configured = active ? currentBotSettings : {};
    makeChannelOptions($('voice_channel_id'), channels.voice || [],
      configured.voice_channel_id, 'voice');
    makeChannelOptions($('text_channel_id'), channels.text || [],
      configured.text_channel_id, 'text');
    setSettingValue('text_reply_mode', configured.text_reply_mode || 'mention');
    setSettingValue('text_voice_mode', configured.text_voice_mode === 'off' ? 'off' : 'keywords');
    $('text_voice_keywords').value = configured.text_voice_keywords || DEFAULT_TEXT_VOICE_KEYWORDS;
    updateVoiceKeywordsVisibility();
    savedSettings = Object.fromEntries(BOT_CONFIG_KEYS.map(key => [key, settingValue(key)]));
    updateChannelScopeNote();
    $('guild-config-state').textContent = active ? 'ACTIVE SERVER' : 'JOINED · INACTIVE';
    $('guild-config-state').dataset.state = active ? 'ready' : 'neutral';
    $('guild-config-hint').textContent = active ?
      'Current VPS configuration for this server.' :
      'The bot has joined this server but this VPS is not listening here. Choose its channels and save to switch the active server.';
    $('guild-save-note').textContent = active ?
      'Saving settings reconnects the bot.' :
      'Saving switches the bot to this server; the previous server stops receiving replies.';
    $('bot-settings').querySelector('button[type=submit]').disabled = false;
    renderLiveVoiceControls();
    if (latestSnapshot) renderOverview(latestSnapshot);
    void refreshVoiceSession();
  } catch (error) {
    if (serial !== guildSelectionSerial) return;
    $('guild-config-state').textContent = 'CHANNELS UNAVAILABLE';
    $('guild-config-state').dataset.state = 'offline';
    $('guild-config-hint').textContent = error.message;
    message('Discord channels: ' + error.message, true);
  }
}

async function refreshSelectedChannels() {
  const id = selectedGuildId;
  if (!id || $('guild-config').hidden) return;
  const serial = guildSelectionSerial;
  const form = $('bot-settings');
  const submit = form.querySelector('button[type=submit]');
  submit.disabled = true;
  $('guild-config-hint').textContent = 'Refreshing voice and text channels from Discord…';
  try {
    const channels = await request('/api/guilds/' + id + '/channels?refresh=1');
    if (serial !== guildSelectionSerial || selectedGuildId !== id) return;
    const draft = Object.fromEntries(BOT_CONFIG_KEYS.map(key => [key, settingValue(key)]));
    makeChannelOptions($('voice_channel_id'), channels.voice || [],
      draft.voice_channel_id, 'voice');
    makeChannelOptions($('text_channel_id'), channels.text || [],
      draft.text_channel_id, 'text');
    // A discovery action must not reset any uncommitted configuration changes.
    showBotSettingsDirtyState();
    $('guild-config-hint').textContent = channels.stale
      ? 'Discord rate-limited the refresh. Showing the previous channel list; try again shortly.'
      : 'Channel list updated · ' + (channels.voice || []).length
        + ' voice and ' + (channels.text || []).length + ' text channels found.';
    message(channels.stale ? 'Discord rate-limited the refresh; showing cached channels.'
      : 'Voice and text channels refreshed. Unsaved settings preserved.',
      Boolean(channels.stale));
    if (latestSnapshot) renderOverview(latestSnapshot);
  } catch (error) {
    if (serial !== guildSelectionSerial || selectedGuildId !== id) return;
    $('guild-config-hint').textContent = 'Channel refresh failed. Existing selections were kept.';
    message('Discord channels: ' + error.message, true);
  } finally {
    if (serial === guildSelectionSerial && selectedGuildId === id) {
      submit.disabled = false;
    }
  }
}

async function loadGuilds(force = false) {
  if (guildsLoading) return;
  guildsLoading = true;
  $('reload-guilds').disabled = true;
  try {
    const data = await request('/api/guilds' + (force ? '?refresh=1' : ''));
    joinedGuilds = Array.isArray(data.guilds) ? data.guilds : [];
    guildsLoaded = true;
    renderGuildButtons();
    renderOverviewGuilds();
    if (!joinedGuilds.length) $('guild-config').hidden = true;
    else if (!selectedGuildId || !joinedGuilds.some(item => item.id === selectedGuildId)) {
      await selectGuild(joinedGuilds.some(item => item.id === currentBotSettings.guild_id) ?
        currentBotSettings.guild_id : joinedGuilds[0].id);
    } else if (force) {
      await refreshSelectedChannels();
    }
  } catch (error) {
    guildsLoaded = false;
    renderOverviewGuilds();
    const note = document.createElement('p');
    note.className = 'section-hint tone-warning';
    note.textContent = 'Cannot read joined servers: ' + error.message;
    $('guild-list').replaceChildren(note);
  } finally {
    guildsLoading = false;
    $('reload-guilds').disabled = false;
  }
}

async function refresh(force = false) {
  if (busy || (providerChanging && !force)) return;
  busy = true;
  try {
    const data = await request('/api/status');
    csrf = data.csrf;
    $('login').hidden = true;
    $('dashboard').hidden = false;
    $('updated').textContent = data.updated_at;

    const bot = data.bot || {};
    $('bot').replaceChildren(
      fact('Guild', bot.guild_id || 'Not configured'),
      fact('Voice channel', bot.voice_channel_id || 'Not configured'),
      fact('Text channel', bot.text_channel_id || 'Voice-channel chat only'),
    );
    currentBotSettings = bot;
    renderAwarenessSettings(bot);
    if (!personalityLoaded) void loadPersonality();
    latestSnapshot = data;
    lastVoiceHealthCheck = Date.now();
    renderProviders(data);
    void loadInferenceProviders();
    void loadGroqKeys();
    void loadLiteRouterKeys();
    $('voice-health-checked').textContent = 'Checked ' + (data.updated_at || 'just now');
    renderSnapshot(data);
    renderServices(data);
    renderSystem(data.system);
    renderOverview(data);
    showTab();
    if (!guildsLoaded) void loadGuilds();
    else renderGuildButtons();
    if (!$('guild-config').hidden) {
      updateChannelScopeNote();
      renderLiveVoiceControls();
      void refreshVoiceSession();
    }
    $('avatar-name').textContent = data.avatar?.username || 'Tsukihime';
    $('avatar-editor-name').textContent = data.avatar?.username || 'Tsukihime';
    const avatarKey = [data.avatar?.id || '', data.avatar?.avatar_hash || 'default'].join('/');
    if (shownAvatarKey !== avatarKey) {
      shownAvatarKey = avatarKey;
      const avatarUrl = '/api/avatar-image?v=' + encodeURIComponent(avatarKey);
      $('avatar-image').src = avatarUrl;
      $('avatar-editor-image').src = avatarUrl;
    }
  } catch (error) {
    if (error.message === 'Sign in required') {
      resetPersonalitySession();
      $('login').hidden = false;
      $('dashboard').hidden = true;
    } else {
      message(error.message, true);
    }
  } finally {
    busy = false;
  }
}

$('login-form').addEventListener('submit', async event => {
  event.preventDefault();
  $('login-error').textContent = '';
  try {
    await request('/api/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password: $('password').value,
        remember: $('remember-device').checked }),
    });
    $('password').value = '';
    resetPersonalitySession();
    guildsLoaded = false;
    selectedGuildId = '';
    await refresh();
  } catch (error) {
    $('login-error').textContent = error.message;
  }
});

$('refresh').addEventListener('click', () => void refresh());
$('reload-guilds').addEventListener('click', () => void loadGuilds(true));
$('copy-invite').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(INVITE_URL);
    message('Bot invite link copied to clipboard.');
    $('copy-invite').title = 'Invite link copied';
  } catch {
    // Clipboard API may be denied by browser policy; never claim success.
    const field = document.createElement('textarea');
    field.value = INVITE_URL;
    field.setAttribute('aria-label', 'Bot invite URL');
    field.style.position = 'fixed';
    field.style.opacity = '0';
    document.body.append(field);
    field.select();
    const copied = document.execCommand('copy');
    field.remove();
    message(copied ? 'Bot invite link copied to clipboard.' :
      'Clipboard access is blocked. Allow clipboard permission and try again.', !copied);
  }
});

$('logout').addEventListener('click', async () => {
  try {
    await request('/api/logout', { method: 'POST', headers: { 'X-CSRF-Token': csrf } });
  } catch (error) {
    message('Could not revoke this browser sign-in: ' + error.message, true);
    return;
  }
  csrf = '';
  resetPersonalitySession();
  guildsLoaded = false;
  selectedGuildId = '';
  liveVoiceSession = null;
  ++voiceSessionSerial;
  joinedGuilds = [];
  $('dashboard').hidden = true;
  $('login').hidden = false;
});

$('provider-form').addEventListener('change', async event => {
  const target = event.target;
  if (!target.matches('input[name="tts_provider"]') || providerChanging) return;
  const provider = target.value;
  if (provider === selectedProvider) return;
  const previous = selectedProvider;
  const controls = [...document.querySelectorAll('input[name="tts_provider"]')];
  providerChanging = true;
  controls.forEach(input => { input.disabled = true; });
  $('provider-feedback').textContent = 'Switching to ' + provider + ' · reconnecting Tsuki…';
  $('provider-feedback').classList.add('pending');
  $('provider-feedback').parentElement.dataset.state = 'pending';
  $('provider-form').setAttribute('aria-busy', 'true');
  try {
    await saveBotConfig({ tts_provider: provider });
    selectedProvider = provider;
    renderLiveVoiceControls();
    $('provider-feedback').textContent = provider === 'off'
      ? 'Voice disabled. Text replies remain available.'
      : provider + ' selected. No automatic fallback.';
    message('Voice route changed to ' + provider + '.');
  } catch (error) {
    const original = controls.find(input => input.value === previous);
    if (original) original.checked = true;
    $('provider-feedback').textContent = 'Could not switch provider. Previous setting restored.';
    $('provider-feedback').parentElement.dataset.state = 'offline';
    message(error.message, true);
  } finally {
    providerChanging = false;
    controls.forEach(input => { input.disabled = false; });
    $('provider-feedback').classList.remove('pending');
    $('provider-form').removeAttribute('aria-busy');
    await refresh(true);
  }
});

$('inference-add-provider').addEventListener('change', renderInferenceProviders);
for (const provider of ['groq', 'literouter']) {
  $(provider + '-provider-toggle').addEventListener('click', () =>
    void toggleExclusiveInferenceProvider(provider));
}
for (const provider of ['groq', 'literouter']) {
  $(provider + '-model-load').addEventListener('click', () =>
    void loadInferenceModels(provider));
  $(provider + '-model-save').addEventListener('click', () =>
    void saveInferenceModel(provider));
  $(provider + '-model-select').addEventListener('change', () =>
    renderInferenceModel(provider));
}
$('inference-add').addEventListener('click', () => {
  const name = $('inference-add-provider').value;
  if (!inferenceCatalog.some(item => item.id === name) || inferenceDraft.includes(name)) return;
  // Advanced routing must not re-enable Groq and LiteRouter together.
  inferenceDraft = ['groq', 'literouter'].includes(name)
    ? [...inferenceDraft.filter(item => item !== 'groq' && item !== 'literouter'), name]
    : [...inferenceDraft, name];
  renderInferenceProviders();
});
$('inference-save').addEventListener('click', async () => {
  if (inferenceEditing || !inferenceDraft.length) return;
  if (['groq', 'literouter'].every(item => inferenceDraft.includes(item))) {
    message('Only Groq or LiteRouter can be enabled; remove one before saving.', true);
    return;
  }
  inferenceEditing = true;
  renderInferenceProviders();
  try {
    await saveBotConfig({ providers: [...inferenceDraft] }, '/api/inference-providers');
    inferenceSaved = [...inferenceDraft];
    message('LLM API provider order applied. Actual inference availability remains unverified.');
  } catch (error) {
    message(error.message, true);
    $('inference-status').textContent = 'Provider update failed. Refresh and inspect current settings.';
  } finally {
    inferenceEditing = false;
    renderInferenceProviders();
    await loadInferenceProviders(true);
  }
});
$('groq-add-form').addEventListener('submit', async event => {
  event.preventDefault();
  const key = $('groq-new-key').value.trim();
  if (!key) return;
  await mutateGroqKey({ action: 'add', key });
});
$('literouter-key-toggle').addEventListener('click', () => {
  if (!$('literouter-key-toggle').disabled) {
    toggleLiteRouterKeyForm($('literouter-key-form').hidden);
  }
});
$('literouter-key-form').addEventListener('submit', async event => {
  event.preventDefault();
  const field = $('literouter-model-key');
  const key = field.value.trim();
  if (!key) return;
  await mutateLiteRouterKey({ action: 'add', key });
});

$('avatar-file').addEventListener('change', async event => {
  const file = event.target.files?.[0];
  if (!file) return;
  if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type)) {
    message('Use a PNG, JPEG, or WebP image.', true); event.target.value = ''; return;
  }
  if (file.size > 1024 * 1024) {
    message('Avatar must be 1 MiB or smaller.', true); event.target.value = ''; return;
  }
  const preview = URL.createObjectURL(file);
  $('avatar-editor-image').src = preview;
  try {
    const dataUrl = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(new Error('Could not read image'));
      reader.readAsDataURL(file);
    });
    message('Updating Discord avatar…', false, true);
    await request('/api/avatar', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify({ image: dataUrl }),
    });
    shownAvatarKey = '';
    message('Discord avatar updated.');
    await refresh();
  } catch (error) {
    message(error.message, true);
    shownAvatarKey = '';
    await refresh();
  } finally {
    URL.revokeObjectURL(preview);
    event.target.value = '';
  }
});

$('password-form').addEventListener('submit', async event => {
  event.preventDefault();
  const current = $('current-password').value;
  const next = $('new-password').value;
  const confirmPassword = $('confirm-password').value;
  if (next !== confirmPassword) { message('New passwords do not match.', true); return; }
  if (next.length < 12) { message('New password must be at least 12 characters.', true); return; }
  const form = event.currentTarget;
  const button = form.querySelector('button[type=submit]');
  button.disabled = true;
  message('Changing the controls password and ending other sessions…', false, true);
  try {
    await request('/api/password', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify({ current, new: next }),
    });
    form.reset();
    csrf = '';
    resetPersonalitySession();
    guildsLoaded = false;
    selectedGuildId = '';
    $('dashboard').hidden = true;
    $('login').hidden = false;
    message('');
    $('login-error').textContent = 'Password changed. Sign in again with the new password.';
  } catch (error) {
    message(error.message, true);
  } finally {
    button.disabled = false;
  }
});

document.querySelectorAll('[data-action]').forEach(button => {
  button.addEventListener('click', async () => {
    if (!confirm('Run “' + button.textContent.trim() + '” on the VPS?')) return;
    const action = button.dataset.action;
    button.disabled = true;
    message('Applying ' + action + '…', false, true);
    try {
      await request('/api/action', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
        body: JSON.stringify({ action }),
      });
      message('Completed: ' + action);
      await refresh();
    } catch (error) {
      message(error.message, true);
    } finally {
      button.disabled = false;
    }
  });
});

function updateVoiceKeywordsVisibility() {
  const enabled = settingValue('text_voice_mode') === 'keywords';
  $('voice-keywords-row').hidden = !enabled;
  $('text_voice_keywords').required = enabled;
}

function updateChannelScopeNote() {
  const voiceSelect = $('voice_channel_id');
  const selectedVoice = voiceSelect.selectedOptions[0]?.textContent || 'selected voice channel';
  const selectedText = $('text_channel_id').value;
  const changing = BOT_CONFIG_KEYS.some(key => settingValue(key) !== savedSettings[key])
    || selectedGuildId !== currentBotSettings.guild_id;
  const actual = currentBotSettings.text_channel_id
    ? 'voice-channel chat + a separate text channel'
    : 'voice-channel chat only';
  const desired = selectedText
    ? 'voice-channel chat + the selected text channel'
    : 'only the selected voice channel’s attached chat';
  const note = $('channel-scope-note');
  note.classList.toggle('pending', Boolean(changing));
  $('reply-any-label').textContent = selectedText
    ? 'All messages in both chats'
    : 'All messages in voice-channel chat';
  $('reply-any-detail').textContent = selectedText
    ? 'Reply in voice chat and the selected text channel.'
    : 'Reply in the selected voice chat.';
  note.textContent = changing
    ? 'LIVE NOW: ' + actual + '. AFTER SAVE: ' + desired
      + '. Changes take effect only after you save.'
    : 'ACTIVE NOW: ' + desired + '. Messages in other text channels are ignored.';
  if (changing && selectedGuildId !== currentBotSettings.guild_id) {
    note.textContent = 'LIVE NOW: another server is active (' + actual
      + '). AFTER SAVE: ' + desired + ' on this server. Save to switch.';
  }
  if (changing && selectedVoice.startsWith('Choose')) {
    note.textContent = 'Select a voice channel and save to apply the channel scope.';
  }
}

function showBotSettingsDirtyState() {
  updateVoiceKeywordsVisibility();
  updateChannelScopeNote();
  const dirty = BOT_CONFIG_KEYS.some(key => settingValue(key) !== savedSettings[key]);
  const otherServer = selectedGuildId && selectedGuildId !== currentBotSettings.guild_id;
  $('guild-save-note').textContent = dirty || otherServer
    ? 'Unsaved changes · click Save server settings to apply and reconnect Tsuki.'
    : 'Saved on VPS · no changes pending.';
  $('guild-config-state').textContent = dirty || otherServer ? 'UNSAVED' : 'ACTIVE SERVER';
  $('guild-config-state').dataset.state = dirty || otherServer ? 'pending' : 'ready';
}
$('reload-voice-session').addEventListener('click', () => void refreshVoiceSession());

$('leave-voice').addEventListener('click', async () => {
  if (voiceOperationBusy || !liveVoiceSession?.connected ||
      selectedGuildId !== currentBotSettings.guild_id) return;
  if (!confirm('Disconnect Tsuki from the current voice channel? Playback will stop.')) return;
  voiceOperationBusy = true;
  renderLiveVoiceControls();
  try {
    await request('/api/voice-leave', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify({ guild_id: selectedGuildId }),
    });
    liveVoiceSession = { connected: false };
    message('Tsuki left voice. Saved channel settings were not changed.');
  } catch (error) {
    message(error.message, true);
  } finally {
    voiceOperationBusy = false;
    renderLiveVoiceControls();
    void refreshVoiceSession();
  }
});

$('custom-tts-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (voiceOperationBusy || !liveVoiceSession?.connected ||
      selectedGuildId !== currentBotSettings.guild_id || selectedProvider === 'off') return;
  const text = $('custom-tts-text').value.trim();
  if (!text || text.length > 280) {
    message('Enter 1–280 characters for custom TTS.', true);
    return;
  }
  voiceOperationBusy = true;
  renderLiveVoiceControls();
  message('Sending custom TTS to the selected notebook…', false, true);
  try {
    const accepted = await request('/api/voice-tts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify({ guild_id: selectedGuildId, text }),
    });
    if (!accepted.job_id) throw new Error('Voice service did not return a job ID.');
    let done = false;
    for (let poll = 0; poll < 180; poll += 1) {
      await new Promise(resolve => setTimeout(resolve, 800));
      const job = await request('/api/config-jobs/' + encodeURIComponent(accepted.job_id));
      if (job.state === 'failed') throw new Error(job.error || 'Voice playback failed.');
      if (job.state === 'done') {
        done = true;
        break;
      }
    }
    if (!done) throw new Error('Playback status timed out. Check the voice channel before retrying.');
    $('custom-tts-text').value = '';
    message('Custom TTS playback completed.');
  } catch (error) {
    message(error.message, true);
  } finally {
    voiceOperationBusy = false;
    renderLiveVoiceControls();
    void refreshVoiceSession();
  }
});

$('personality-editor').addEventListener('input', renderPersonalityDirty);
$('personality-override-editor').addEventListener('input', renderPersonalityOverrideDirty);
$('personality-reset').addEventListener('click', () => {
  $('personality-editor').value = personalitySaved;
  renderPersonalityDirty();
  $('personality-editor').focus();
});
$('personality-override-clear').addEventListener('click', () => {
  $('personality-override-editor').value = '';
  renderPersonalityOverrideDirty();
});
$('personality-reload').addEventListener('click', async () => {
  const unsavedBase = $('personality-editor').value.trim() !== personalitySaved;
  const unsavedOverride = $('personality-override-editor').value.trim() !== personalityOverrideSaved;
  if ((unsavedBase || unsavedOverride) && !confirm('Discard unsaved persona edits?')) return;
  personalityLoaded = false;
  $('personality-editor').disabled = true;
  $('personality-override-editor').disabled = true;
  renderPersonalityDirty();
  renderPersonalityOverrideDirty();
  await loadPersonality();
});
$('personality-settings').addEventListener('submit', async event => {
  event.preventDefault();
  if (!personalityLoaded) return;
  const base = $('personality-editor').value.trim();
  if (base === personalitySaved) return;
  if (!base || !event.currentTarget.reportValidity()) return;
  if (!confirm('Save the existing Modelfile personality and restart the API? Current inference will stop.')) return;
  const button = $('personality-save');
  button.disabled = true;
  try {
    await saveBotConfig({ base_personality: base }, '/api/personality');
    personalitySaved = base;
    renderPersonalityDirty();
    message('Existing Modelfile personality saved and API restarted.');
    await refresh();
  } catch (error) {
    message(error.message, true);
    renderPersonalityDirty();
  }
});
$('personality-override-settings').addEventListener('submit', async event => {
  event.preventDefault();
  if (!personalityLoaded) return;
  const override = $('personality-override-editor').value.trim();
  if (override === personalityOverrideSaved) return;
  if (!event.currentTarget.reportValidity()) return;
  if (!confirm('Save the optional override and restart the API?')) return;
  $('personality-override-save').disabled = true;
  try {
    await saveBotConfig({ personality_injection: override }, '/api/personality');
    personalityOverrideSaved = override;
    renderPersonalityOverrideDirty();
    message('Optional personality override saved.');
    await refresh();
  } catch (error) {
    message(error.message, true);
    renderPersonalityOverrideDirty();
  }
});
$('engagement-settings').addEventListener('input', renderAwarenessDirty);
$('engagement-settings').addEventListener('change', renderAwarenessDirty);
$('engagement-settings').addEventListener('submit', async event => {
  event.preventDefault();
  const values = awarenessValues();
  if (!awarenessDirty) { message('No awareness settings have changed.'); return; }
  if (!document.getElementById('engagement-settings').reportValidity()) return;
  if (!confirm('Apply context-awareness settings and reconnect Tsuki? Active playback will stop.')) return;
  const button = event.currentTarget.querySelector('button[type=submit]');
  button.disabled = true;
  try {
    await saveBotConfig(values);
    awarenessDirty = false;
    awarenessSaved = null;
    message('Conversation-awareness settings applied. Tsuki is reconnecting.');
    await refresh();
  } catch (error) {
    message(error.message, true);
  } finally {
    button.disabled = false;
  }
});
$('bot-settings').addEventListener('change', showBotSettingsDirtyState);
$('text_voice_keywords').addEventListener('input', showBotSettingsDirtyState);

$('bot-settings').addEventListener('submit', async event => {
  event.preventDefault();
  const values = Object.fromEntries(BOT_CONFIG_KEYS.map(key => [key, settingValue(key)]));
  if (BOT_CONFIG_KEYS.every(key => values[key] === savedSettings[key]) &&
      selectedGuildId === currentBotSettings.guild_id) {
    message('No bot settings have changed.'); return;
  }
  if (values.text_voice_mode === 'keywords' && !values.text_voice_keywords.trim()) {
    message('Enter at least one voice trigger word.', true);
    $('text_voice_keywords').focus(); return;
  }
  if (!confirm('Save bot settings and reconnect Tsuki? Current playback will stop.')) return;
  const button = event.currentTarget.querySelector('button[type=submit]');
  button.disabled = true;
  message('Saving bot settings…', false, true);
  try {
    await saveBotConfig(values);
    message('Bot configuration saved. Tsuki is reconnecting.');
    await refresh();
    await selectGuild($('guild_id').value);
  } catch (error) {
    message(error.message, true);
  } finally {
    button.disabled = false;
  }
});

async function bootstrap() {
  try {
    const session = await request('/api/session');
    if (session.authenticated) await refresh();
    else {
      $('dashboard').hidden = true;
      $('login').hidden = false;
    }
  } catch (error) {
    $('login-error').textContent = error.message;
  }
}
void bootstrap();
async function pollSelectedVoiceConnection() {
  if (voiceHealthPolling || busy || providerChanging || $('dashboard').hidden ||
      selectedProvider === 'off') return;
  const interval = selectedVoiceOnline === true ? 30000 : 10000;
  if (Date.now() - lastVoiceHealthCheck < interval) return;
  voiceHealthPolling = true;
  const previous = selectedProvider;
  try {
    const data = await request('/api/provider-health');
    lastVoiceHealthCheck = Date.now();
    if (providerChanging || previous !== data.selected_provider) return;
    if (latestSnapshot) {
      latestSnapshot.providers = data.providers;
      latestSnapshot.selected_provider = data.selected_provider;
      renderProviders(data);
      renderSnapshot(latestSnapshot);
    }
    $('voice-health-checked').textContent = 'Checked ' + (data.checked_at || 'just now');
  } catch (error) {
    lastVoiceHealthCheck = Date.now();
    $('voice-health-text').textContent = 'Connection check failed · retrying in 10 seconds';
    $('voice-health-checked').textContent = error.message;
    selectedVoiceOnline = false;
  } finally {
    voiceHealthPolling = false;
  }
}
setInterval(() => { if (!$('dashboard').hidden) void refresh(); }, 30000);
setInterval(() => { void pollSelectedVoiceConnection(); }, 2000);
