// ClipSound Lobbys – läuft auf Railway.
// Wer eine Lobby öffnet (Host), bringt seine Sounds mit. Wer mit dem 5-stelligen Code beitritt,
// sieht sie und kann sie abspielen – jeder gedrückte Sound läuft bei allen gleichzeitig.
// Einen Sound behalten geht nur, wenn beide Seiten zustimmen.
//
// Der Server speichert KEINE Audio-Dateien. In Postgres stehen nur Listen (Name, Größe, sha256).
// Braucht jemand eine Datei, bittet der Server ein Gerät, das sie hat, sie zu schicken, und reicht sie
// im Arbeitsspeicher direkt weiter (Durchleitung). Ist gerade niemand online, der sie hat, gibt es sie nicht.
//
// HTTP
//   POST /api/lobbies                       {name}                → {code, hostToken}
//   GET  /api/lobbies/:code                                       → {code, host, sounds}
//   PUT  /api/lobbies/:code/sounds  (Host)  {sounds:[{id,name,size}]} → {missing:[], skipped:[name]}
//        id = sha256 der Datei (hex). Ersetzt die ganze Liste.
//   GET  /api/lobbies/:code/blobs/:id                             → Audio (von einem Gerät in der Lobby)
//   PUT  /api/relay/:rid                    Rohdaten der Datei     (Antwort eines Geräts auf „send“)
//   Host-Anfragen tragen „Authorization: Bearer <hostToken>“.
//   Clans: GET|POST /api/clans/:id/sounds, DELETE /api/clans/:id/sounds/:sid (Admin),
//          GET /api/clans/:id/blobs/:sid, POST /api/clans/:id/session → {code, ticket}; Rest in clans.js
//
// WebSocket  wss://…/ws?lobby=CODE&name=Daniel[&token=hostToken][&ticket=clanTicket]
//   Server → hello {you, isHost, host, code, sounds, members, serverTime}, sounds {sounds}, members {members},
//            play {id, by, from, at}, stop {by, from}, pong {t, serverTime},
//            asked {req, id, name, by, from}      (an den Host: Gast möchte den Sound behalten)
//            offered {req, id, name, by}          (an einen Gast: Host bietet einen Sound an)
//            answered {req, id, name, ok, kind}   (an beide: Ergebnis; kind = ask | offer)
//            closed {reason}                      (Lobby ist zu, danach Close 4410)
//            setvolume {level, by}               (an ein Gerät: jemand stellt die Lautstärke ein, 0…1)
//            send {rid, id}                       (bitte Datei id an PUT /api/relay/rid schicken)
//   Client → play {id}, stop {}, ping {t}, ask {id}, offer {id, to}, answer {req, ok},
//            volume {level, system}  (eigene Lautstärke melden; system = echte Lautsprecher, sonst nur App)
//            setvolume {to, level}   (jeder bei jedem)
//            have {ids}              (diese Dateien liegen bei mir – ersetzt die bisherige Liste)
//            nosend {rid}            (die angefragte Datei habe ich doch nicht)
//   In einer Clan-Session gibt es keinen Host; man kommt nur mit dem Ticket aus POST …/session hinein.
//
// WebSocket  wss://…/ws?share=1   Geräte-Verbindung, solange man in der App angemeldet ist
//   Client → auth {token, device?} (als Erstes; device = feste ID dieser App-Installation), have {ids}, nosend {rid}, ping {t}
//   Server → ready {}, want {sounds:[{id,size}]} (Clan-Sounds, nach denen das Gerät schauen soll), send {rid, id}, pong,
//            clan {clanId}   (im Clan hat sich etwas geändert: Sounds, wer online ist, Mitglieder, Session – neu laden)
//   Close 4001 = durch eine neue Verbindung desselben Geräts ersetzt (nicht neu verbinden)
//
//   Close-Codes: 4404 Lobby gibt's nicht, 4401 kein Zugang, 4409 voll, 4410 geschlossen, 4429 zu viele Versuche
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { Pool } = require('pg');
const { WebSocketServer } = require('ws');
const clans = require('./clans');

const PORT = process.env.PORT || 3000;
const PUBLIC_DIR = path.join(__dirname, 'public');
const MAX_FILE = 15 * 1024 * 1024;   // pro Sound
const MAX_LOBBY = 150 * 1024 * 1024; // alle Sounds einer Lobby zusammen
const MAX_SOUNDS = 300;
const MAX_MEMBERS = 8;
const PLAY_DELAY = 150;              // ms Vorlauf, damit alle zur gleichen Zeit starten
const HOST_GRACE = 60 * 1000;        // so lange wartet eine Lobby, wenn der Host weg ist (Clan-Session: wenn keiner mehr drin ist)
const SEND_START = 8000;             // so lange hat ein Gerät, um mit dem Schicken anzufangen
const SEND_TIME = 45000;             // … und so lange, um fertig zu werden
const MAX_RELAYS = 24;               // gleichzeitige Durchleitungen (jede hält höchstens MAX_FILE im Arbeitsspeicher)
const MAX_DEVICES = 8;               // Geräte-Verbindungen pro Konto

const AUDIO_TYPES = {
  '.mp3': 'audio/mpeg', '.wav': 'audio/wav', '.ogg': 'audio/ogg', '.opus': 'audio/ogg',
  '.m4a': 'audio/mp4', '.aac': 'audio/aac', '.webm': 'audio/webm', '.flac': 'audio/flac',
  '.aif': 'audio/aiff', '.aiff': 'audio/aiff', '.caf': 'audio/x-caf',
};
const STATIC_TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.png': 'image/png', '.svg': 'image/svg+xml' };

// Bis Version 2.4 lagen hochgeladene Sounds auf dem Volume (blobs/, davor sounds/). Die braucht niemand mehr – weg damit.
const OLD_DATA = process.env.DATA_DIR || process.env.RAILWAY_VOLUME_MOUNT_PATH || path.join(__dirname, 'data');
for (const dir of ['blobs', 'sounds']) fs.rmSync(path.join(OLD_DATA, dir), { recursive: true, force: true });

const db = new Pool({
  connectionString: process.env.DATABASE_URL,
  ssl: /railway\.internal|localhost|127\.0\.0\.1/.test(process.env.DATABASE_URL || '') ? false : { rejectUnauthorized: false },
});

let ticketSecret = '';

async function migrate() {
  await db.query(`
    DROP TABLE IF EXISTS sounds;
    DROP TABLE IF EXISTS rooms;
    CREATE TABLE IF NOT EXISTS lobbies (
      code       text PRIMARY KEY,
      host_token text NOT NULL,
      host_name  text NOT NULL,
      created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE TABLE IF NOT EXISTS lobby_sounds (
      code  text NOT NULL REFERENCES lobbies(code) ON DELETE CASCADE,
      id    text NOT NULL,
      name  text NOT NULL,
      size  integer NOT NULL,
      pos   integer NOT NULL,
      PRIMARY KEY (code, id)
    );
    CREATE TABLE IF NOT EXISTS server_secrets (
      name  text PRIMARY KEY,
      value text NOT NULL
    );
  `);
  await clans.migrate(db); // Accounts & Clans (additiv, bricht die Lobbys nicht)
  await db.query('INSERT INTO server_secrets (name, value) VALUES ($1, $2) ON CONFLICT DO NOTHING',
    ['session-ticket', crypto.randomBytes(32).toString('hex')]);
  ticketSecret = (await db.query('SELECT value FROM server_secrets WHERE name = $1', ['session-ticket'])).rows[0].value;
  for (const r of (await db.query('SELECT clan_id, code FROM clan_sessions')).rows) sessionClan.set(r.code, r.clan_id);
}

// ---------- Hilfen ----------
// 5 Zeichen ohne Verwechsler (kein 0/O, 1/I/L) → 31^5 ≈ 28 Mio. Codes
const CODE_CHARS = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
const newCode = () => [...crypto.randomBytes(5)].map(b => CODE_CHARS[b % CODE_CHARS.length]).join('');
const normCode = c => String(c || '').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 5);
const hashToken = t => crypto.createHash('sha256').update(String(t || '')).digest('hex');
const sha256 = buf => crypto.createHash('sha256').update(buf).digest('hex');
const cleanPerson = n => String(n || '').replace(/[^\p{L}\p{N} _\-.]/gu, '').trim().slice(0, 24);
const isId = id => typeof id === 'string' && /^[0-9a-f]{64}$/.test(id);

function cleanFileName(name) {
  const ext = path.extname(String(name)).toLowerCase();
  const base = path.basename(String(name), path.extname(String(name)))
    .replace(/[^\p{L}\p{N} _\-()!?.,']/gu, '').trim().slice(0, 80) || 'sound';
  return AUDIO_TYPES[ext] ? base + ext : null;
}

function looksLikeAudio(buf) {
  const s = (a, b) => buf.toString('latin1', a, b);
  if (buf.length < 12) return false;
  if (s(0, 3) === 'ID3') return true;
  if (buf[0] === 0xff && (buf[1] & 0xe0) === 0xe0) return true;
  if (s(0, 4) === 'RIFF' && s(8, 12) === 'WAVE') return true;
  if (s(0, 4) === 'OggS') return true;
  if (s(0, 4) === 'fLaC') return true;
  if (s(4, 8) === 'ftyp') return true;
  if (s(0, 4) === 'FORM' && (s(8, 12) === 'AIFF' || s(8, 12) === 'AIFC')) return true;
  if (s(0, 4) === 'caff') return true;
  if (buf[0] === 0x1a && buf[1] === 0x45 && buf[2] === 0xdf && buf[3] === 0xa3) return true;
  return false;
}

function json(res, code, data) {
  res.writeHead(code, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' });
  res.end(JSON.stringify(data));
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', c => {
      size += c.length;
      if (size > limit) { reject(Object.assign(new Error('zu groß'), { status: 413 })); req.destroy(); }
      else chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

// Falsche Codes pro IP begrenzen, damit niemand Lobbys durchprobiert
const misses = new Map(); // ip → [zeitpunkte]
// Railways Proxy setzt X-Real-IP selbst; X-Forwarded-For kann der Client fälschen
const clientIp = req => String(req.headers['x-real-ip'] || req.socket.remoteAddress || '');
function blocked(ip) {
  const now = Date.now();
  const list = (misses.get(ip) || []).filter(t => now - t < 10 * 60 * 1000);
  misses.set(ip, list);
  return list.length >= 30;
}
function miss(ip) { (misses.get(ip) || misses.set(ip, []).get(ip)).push(Date.now()); }
// Dateien anfordern kostet die Geräte Upload – pro IP begrenzen
const pulls = new Map(); // ip → [zeitpunkte]
function tooManyPulls(ip) {
  const now = Date.now();
  const list = (pulls.get(ip) || []).filter(t => now - t < 5 * 60 * 1000);
  list.push(now);
  pulls.set(ip, list);
  return list.length > 600;
}
setInterval(() => {
  const now = Date.now();
  for (const [ip, l] of misses) if (!l.some(t => now - t < 600000)) misses.delete(ip);
  for (const [ip, l] of pulls) if (!l.some(t => now - t < 300000)) pulls.delete(ip);
}, 600000);

// ---------- Wer hat welche Datei (nur im Arbeitsspeicher) ----------
const lobbies = new Map();      // code → Set<ws>
const hostAway = new Map();     // code → seit wann kein Host (Clan-Session: niemand) verbunden ist
const requests = new Map();     // req → {code, kind, id, name, guest, host, at}
const devices = new Map();      // userId → Set<ws> (Geräte-Verbindungen angemeldeter Nutzer)
const sessionClan = new Map();  // Lobby-Code → Clan-ID (laufende Clan-Sessions)

const live = ws => ws.readyState === 1;
const sessionOf = clanId => { for (const [code, c] of sessionClan) if (c === clanId) return code; return null; };

/** Geräte von Clan-Mitgliedern, die Datei id haben (Geräte-Verbindungen + Leute in der Clan-Session). */
function clanDevices(clanId, id) {
  const out = [];
  for (const set of devices.values()) for (const ws of set) if (ws.clans.has(clanId) && ws.has.has(id)) out.push(ws);
  const code = sessionOf(clanId);
  if (code) for (const ws of lobbies.get(code) || []) if (ws.has.has(id)) out.push(ws);
  return out;
}

/** Geräte in der Lobby, die Datei id haben (bei einer Clan-Session auch die übrigen Clan-Geräte). */
function lobbyDevices(code, id) {
  const clanId = sessionClan.get(code);
  if (clanId) return clanDevices(clanId, id);
  return [...(lobbies.get(code) || [])].filter(ws => ws.has.has(id));
}

// ---------- Durchleitung: Dateien von Gerät zu Gerät ----------
const relays = new Map();   // rid → {id, ws, started, done, timer}
const inflight = new Map(); // id → Promise<Buffer|null>: dieselbe Datei nur einmal gleichzeitig holen

function askDevice(ws, id) {
  return new Promise((resolve, reject) => {
    if (relays.size >= MAX_RELAYS) return reject(Object.assign(new Error('Server ausgelastet'), { busy: true }));
    const rid = crypto.randomBytes(16).toString('hex');
    const r = { id, ws, started: false };
    r.done = (err, buf) => {
      if (relays.get(rid) !== r) return;
      relays.delete(rid);
      clearTimeout(r.timer);
      ws.sending--;
      err ? reject(err) : resolve(buf);
    };
    r.timer = setTimeout(() => {
      r.done(new Error('keine Antwort'));
      // Gerät reagiert nicht (eingefroren, im Hintergrund, verwaiste Verbindung): Verbindung kappen,
      // damit es nicht wieder gefragt wird. Lebt die App noch, verbindet sie sich neu und meldet sich frisch.
      try { ws.terminate(); } catch {}
    }, SEND_START);
    relays.set(rid, r);
    ws.sending++;
    sendTo(ws, { type: 'send', rid, id });
  });
}

/** Datei bei einem Gerät holen, das sie hat (bis zu 3 versuchen). null = gerade hat sie niemand. */
function fetchFromDevices(id, candidates) {
  if (inflight.has(id)) return inflight.get(id);
  const p = (async () => {
    const tried = new Set();
    for (let i = 0; i < 3; i++) {
      // wer gerade am wenigsten schickt, sonst zufällig
      const next = candidates().filter(ws => live(ws) && !tried.has(ws))
        .map(ws => [ws, ws.sending + Math.random()]).sort((a, b) => a[1] - b[1])[0]?.[0];
      if (!next) return null;
      tried.add(next);
      try { return await askDevice(next, id); } catch (e) { if (e.busy) throw e; }
    }
    return null;
  })().finally(() => inflight.delete(id));
  inflight.set(id, p);
  return p;
}

/** Gerät ist weg oder hat abgesagt: offene Anfragen an es beenden. */
function dropRelays(ws) {
  for (const r of relays.values()) if (r.ws === ws) r.done(new Error('Gerät weg'));
}

async function relayIn(req, res, rid) {
  const r = relays.get(rid);
  if (!r || r.started) return json(res, 404, { error: 'Nicht (mehr) angefragt' });
  r.started = true;
  clearTimeout(r.timer);
  r.timer = setTimeout(() => r.done(new Error('zu langsam')), SEND_TIME);
  let buf;
  try { buf = await readBody(req, MAX_FILE); } catch (e) { r.done(e); throw e; }
  if (sha256(buf) !== r.id || !looksLikeAudio(buf)) {
    r.ws.has.delete(r.id); // dieses Gerät nicht mehr danach fragen
    r.done(new Error('falsche Datei'));
    return json(res, 400, { error: 'Datei passt nicht' });
  }
  r.done(null, buf);
  json(res, 200, { ok: true });
}

/** Audio ausliefern (nur aus dem Arbeitsspeicher, nichts wird zwischengespeichert). */
function sendAudio(req, res, buf, name) {
  const head = {
    'Content-Type': AUDIO_TYPES[path.extname(name).toLowerCase()] || 'application/octet-stream',
    'Accept-Ranges': 'bytes', 'Access-Control-Allow-Origin': '*', 'Cache-Control': 'no-store',
  };
  const range = req.headers.range && /bytes=(\d*)-(\d*)/.exec(req.headers.range);
  if (range && (range[1] || range[2])) {
    const start = range[1] ? +range[1] : Math.max(0, buf.length - +range[2]);
    const end = range[1] && range[2] ? Math.min(+range[2], buf.length - 1) : buf.length - 1;
    if (start > end || start >= buf.length) { res.writeHead(416, { ...head, 'Content-Range': `bytes */${buf.length}` }); return res.end(); }
    res.writeHead(206, { ...head, 'Content-Range': `bytes ${start}-${end}/${buf.length}`, 'Content-Length': end - start + 1 });
    return res.end(buf.subarray(start, end + 1));
  }
  res.writeHead(200, { ...head, 'Content-Length': buf.length });
  res.end(buf);
}

/** Datei holen und ausliefern, oder sagen, warum es nicht geht. */
async function pull(req, res, id, name, candidates) {
  if (tooManyPulls(clientIp(req))) return json(res, 429, { error: 'Zu viele Downloads, warte ein paar Minuten' });
  let buf;
  try { buf = await fetchFromDevices(id, candidates); } catch (e) {
    if (e.busy) return json(res, 503, { error: 'Server ausgelastet, gleich nochmal versuchen' });
    throw e;
  }
  if (!buf) return json(res, 404, { error: 'Gerade ist niemand online, der diesen Sound hat' });
  sendAudio(req, res, buf, name);
}

// ---------- Datenbank ----------
async function getLobby(code) {
  const { rows } = await db.query('SELECT code, host_token, host_name FROM lobbies WHERE code = $1', [code]);
  return rows[0] || null;
}

/** Nur Sounds, die gerade jemand in der Lobby (bzw. im Clan) auf dem Gerät hat */
async function lobbySounds(code) {
  const { rows } = await db.query('SELECT id, name, size FROM lobby_sounds WHERE code = $1 ORDER BY pos', [code]);
  return rows.filter(r => lobbyDevices(code, r.id).length > 0);
}

async function soundIn(code, id) {
  const { rows } = await db.query('SELECT id, name, size FROM lobby_sounds WHERE code = $1 AND id = $2', [code, id]);
  return rows[0] || null;
}

async function closeLobby(code, reason) {
  await db.query('DELETE FROM lobbies WHERE code = $1', [code]);
  await db.query('DELETE FROM clan_sessions WHERE code = $1', [code]); // falls das eine Clan-Session war
  hostAway.delete(code);
  const clanId = sessionClan.get(code);
  sessionClan.delete(code);
  if (clanId) notifyClan(clanId);
  lastSounds.delete(code);
  const set = lobbies.get(code);
  if (set) {
    for (const ws of set) {
      sendTo(ws, { type: 'closed', reason });
      ws.close(4410, 'Lobby geschlossen');
    }
    lobbies.delete(code);
  }
  for (const [req, r] of requests) if (r.code === code) requests.delete(req);
  console.log(`Lobby ${code} zu: ${reason}`);
}

// ---------- HTTP ----------
function serveFile(req, res, file, type, extra = {}) {
  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) return json(res, 404, { error: 'Nicht gefunden' });
    res.writeHead(200, { 'Content-Type': type, 'Content-Length': st.size, 'Access-Control-Allow-Origin': '*', ...extra });
    fs.createReadStream(file).pipe(res);
  });
}

function isHost(req, lobby) {
  const m = /^Bearer (.+)$/.exec(req.headers.authorization || '');
  return !!m && hashToken(m[1]) === lobby.host_token;
}

async function setSounds(req, res, lobby) {
  let body;
  try { body = JSON.parse(await readBody(req, 256 * 1024)); } catch (e) {
    if (e.status) throw e;
    return json(res, 400, { error: 'Ungültige Liste' });
  }
  const list = [], seen = new Set(), skipped = [];
  let total = 0;
  for (const s of Array.isArray(body.sounds) ? body.sounds : []) {
    const name = cleanFileName(s.name);
    const size = Math.floor(Number(s.size));
    if (!isId(s.id) || !name || seen.has(s.id)) continue;
    if (!(size > 0 && size <= MAX_FILE) || total + size > MAX_LOBBY || list.length >= MAX_SOUNDS) { skipped.push(String(s.name)); continue; }
    seen.add(s.id); total += size;
    list.push({ id: s.id, name, size });
  }
  const client = await db.connect();
  try {
    await client.query('BEGIN');
    await client.query('DELETE FROM lobby_sounds WHERE code = $1', [lobby.code]);
    for (const [pos, s] of list.entries()) {
      await client.query('INSERT INTO lobby_sounds (code, id, name, size, pos) VALUES ($1, $2, $3, $4, $5)',
        [lobby.code, s.id, s.name, s.size, pos]);
    }
    await client.query('COMMIT');
  } catch (e) {
    await client.query('ROLLBACK'); throw e;
  } finally { client.release(); }
  json(res, 200, { missing: [], skipped }); // „missing“ bleibt für alte Apps: nichts hochladen
  soundsChanged(lobby.code);
}

// ---------- Clan-Sounds & Clan-Session ----------
const ticketFor = (code, userId) =>
  userId + '.' + crypto.createHmac('sha256', ticketSecret).update(code + ':' + userId).digest('hex').slice(0, 32);
function ticketUser(code, ticket) {
  const [userId, mac] = String(ticket || '').split('.');
  if (!userId || !mac) return null;
  const want = ticketFor(code, userId).split('.')[1];
  return want.length === mac.length && crypto.timingSafeEqual(Buffer.from(want), Buffer.from(mac)) ? userId : null;
}

/** Lobby-Soundliste einer laufenden Clan-Session auf die Clan-Bibliothek bringen. */
async function syncSessionSounds(code, clanId) {
  const snd = await db.query('SELECT id, name, size FROM clan_sounds WHERE clan_id = $1 ORDER BY pos', [clanId]);
  const client = await db.connect();
  try {
    await client.query('BEGIN');
    await client.query('DELETE FROM lobby_sounds WHERE code = $1', [code]);
    for (const [pos, s] of snd.rows.entries())
      await client.query('INSERT INTO lobby_sounds (code, id, name, size, pos) VALUES ($1,$2,$3,$4,$5)', [code, s.id, s.name, s.size, pos]);
    await client.query('COMMIT');
  } catch (e) { await client.query('ROLLBACK'); throw e; } finally { client.release(); }
  soundsChanged(code);
}

/** Allen Geräten der Mitglieder sagen, dass sie die Clan-Seite neu laden sollen (gesammelt, höchstens alle 400 ms). */
const clanTimers = new Map();
function notifyClan(clanId) {
  if (clanTimers.has(clanId)) return;
  clanTimers.set(clanId, setTimeout(() => {
    clanTimers.delete(clanId);
    for (const set of devices.values()) for (const ws of set) if (ws.clans.has(clanId)) sendTo(ws, { type: 'clan', clanId });
  }, 400));
}

/** Wer im Clan gerade online ist (Geräte-Verbindung) und wer in der Session ist. */
function presence(clanId) {
  const online = new Set(), inSession = new Set();
  for (const [userId, set] of devices) for (const ws of set) if (ws.clans.has(clanId)) online.add(userId);
  const code = sessionOf(clanId);
  if (code) for (const ws of lobbies.get(code) || []) if (ws.userId) { inSession.add(ws.userId); online.add(ws.userId); }
  return { online, inSession };
}

/** Welche Clan-Sounds ein Gerät suchen soll (alle aus allen Clans des Nutzers). */
async function sendWant(ws) {
  const { rows } = await db.query(
    'SELECT DISTINCT s.id, s.size FROM clan_sounds s JOIN clan_members m ON m.clan_id = s.clan_id WHERE m.user_id = $1', [ws.userId]);
  sendTo(ws, { type: 'want', sounds: rows });
}

/** Sounds eines Clans haben sich geändert: Geräte der Mitglieder neu suchen lassen, Session angleichen. */
async function clanSoundsChanged(clanId) {
  for (const set of devices.values()) for (const ws of set) if (ws.clans.has(clanId)) await sendWant(ws);
  const code = sessionOf(clanId);
  if (code) await syncSessionSounds(code, clanId);
  notifyClan(clanId);
}

/** Clans eines Nutzers haben sich geändert (beigetreten, ausgetreten, entfernt, angelegt, aufgelöst). */
async function membershipChanged(userId) {
  const now = new Set((await db.query('SELECT clan_id FROM clan_members WHERE user_id = $1', [userId])).rows.map(r => r.clan_id));
  const touched = new Set(now);
  for (const ws of devices.get(userId) || []) {
    ws.clans.forEach(c => touched.add(c));
    ws.clans = new Set(now);
    await sendWant(ws);
  }
  // wer nicht mehr im Clan ist, fliegt aus der Clan-Session (auch ohne Geräte-Verbindung)
  for (const [code, clanId] of sessionClan) {
    if (now.has(clanId)) continue;
    for (const ws of lobbies.get(code) || []) if (ws.userId === userId) { ws.close(4410, 'Nicht mehr im Clan'); touched.add(clanId); }
  }
  for (const clanId of touched) {
    notifyClan(clanId);
    for (const ws of devices.get(userId) || []) sendTo(ws, { type: 'clan', clanId }); // auch wer gerade rausgeflogen ist
    const code = sessionOf(clanId);
    if (code) soundsChanged(code);
  }
}

/** Konto gelöscht: alle Verbindungen des Nutzers beenden. */
function userGone(userId) {
  for (const ws of devices.get(userId) || []) ws.close(4401, 'Konto gelöscht');
  for (const set of lobbies.values()) for (const ws of set) if (ws.userId === userId) ws.close(4410, 'Konto gelöscht');
}

/** Clan aufgelöst (letztes Mitglied hat sein Konto gelöscht): Session schließen. */
async function clanGone(clanId) {
  const code = sessionOf(clanId);
  if (code) await closeLobby(code, 'Der Clan wurde aufgelöst');
}

/** true = hier beantwortet. Deckt /api/clans/:id/sounds[/:sid] | /session | /blobs/:sid ab. */
async function clanMedia(req, res, p) {
  const m = /^\/api\/clans\/([0-9a-f]{32})\/(sounds|session|sounds\/[0-9a-f]{64}|blobs\/[0-9a-f]{64})$/.exec(p);
  if (!m) return false;
  const clanId = m[1], tail = m[2];
  const me = await clans.authUser(db, req);
  if (!me) { json(res, 401, { error: 'Nicht eingeloggt' }); return true; }
  const role = await clans.memberRole(db, clanId, me.id);
  if (!role) { json(res, 403, { error: 'Du bist nicht in diesem Clan' }); return true; }

  if (tail === 'sounds' && req.method === 'GET') {
    const { rows } = await db.query(
      'SELECT s.id, s.name, s.size, u.name AS "by" FROM clan_sounds s LEFT JOIN users u ON u.id = s.added_by WHERE s.clan_id = $1 ORDER BY s.pos', [clanId]);
    json(res, 200, { sounds: rows.map(s => ({ ...s, available: clanDevices(clanId, s.id).length > 0 })) });
    return true;
  }

  if (tail === 'sounds' && req.method === 'POST') {
    let body;
    try { body = JSON.parse(String(await readBody(req, 256 * 1024)) || '{}'); }
    catch (e) { if (e.status) throw e; json(res, 400, { error: 'Ungültige Liste' }); return true; }
    const cur = (await db.query('SELECT id, size FROM clan_sounds WHERE clan_id = $1', [clanId])).rows;
    const maxpos = (await db.query('SELECT COALESCE(MAX(pos), -1) AS m FROM clan_sounds WHERE clan_id = $1', [clanId])).rows[0].m;
    const existing = new Set(cur.map(r => r.id));
    let total = cur.reduce((a, r) => a + r.size, 0), count = cur.length, pos = maxpos + 1;
    const added = [], skipped = [];
    for (const s of Array.isArray(body.sounds) ? body.sounds : []) {
      const name = cleanFileName(s.name), size = Math.floor(Number(s.size));
      if (!isId(s.id) || !name) { skipped.push(String(s.name)); continue; }
      if (existing.has(s.id)) continue;
      if (!(size > 0 && size <= MAX_FILE) || total + size > MAX_LOBBY || count >= MAX_SOUNDS) { skipped.push(String(s.name)); continue; }
      await db.query('INSERT INTO clan_sounds (clan_id, id, name, size, pos, added_by) VALUES ($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING',
        [clanId, s.id, name, size, pos++, me.id]);
      existing.add(s.id); total += size; count++;
      added.push({ id: s.id, name, size });
    }
    json(res, 200, { added: added.length, missing: [], skipped });
    if (added.length) await clanSoundsChanged(clanId);
    return true;
  }

  if (tail.startsWith('sounds/') && req.method === 'DELETE') {
    if (role !== 'admin') { json(res, 403, { error: 'Nur Admins können Clan-Sounds löschen' }); return true; }
    const r = await db.query('DELETE FROM clan_sounds WHERE clan_id = $1 AND id = $2', [clanId, tail.slice(7)]);
    if (!r.rowCount) { json(res, 404, { error: 'Den Sound gibt es im Clan nicht' }); return true; }
    json(res, 200, { ok: true });
    await clanSoundsChanged(clanId);
    return true;
  }

  if (tail.startsWith('blobs/')) {
    const id = tail.slice(6);
    if (req.method === 'PUT') { json(res, 410, { error: 'Sounds werden nicht mehr hochgeladen – bitte ClipSound aktualisieren.' }); return true; }
    const row = (await db.query('SELECT name FROM clan_sounds WHERE clan_id = $1 AND id = $2', [clanId, id])).rows[0];
    if (!row) { json(res, 404, { error: 'Nicht gefunden' }); return true; }
    await pull(req, res, id, row.name, () => clanDevices(clanId, id));
    return true;
  }

  if (tail === 'session' && req.method === 'POST') {
    let code = sessionOf(clanId);
    if (code && !await getLobby(code)) { sessionClan.delete(code); code = null; }
    if (!code) {
      await db.query('DELETE FROM clan_sessions WHERE clan_id = $1', [clanId]); // verwaiste Session aufräumen
      const clanName = (await db.query('SELECT name FROM clans WHERE id = $1', [clanId])).rows[0]?.name || 'Clan';
      for (let i = 0; i < 10 && !code; i++) {
        const c = newCode();
        // Clan-Sessions haben keinen Host: der Token wird nirgends verraten
        const r = await db.query('INSERT INTO lobbies (code, host_token, host_name) VALUES ($1,$2,$3) ON CONFLICT DO NOTHING',
          [c, hashToken(crypto.randomBytes(24)), clanName]);
        if (r.rowCount) code = c;
      }
      if (!code) { json(res, 503, { error: 'Kein Code frei' }); return true; }
      await db.query('INSERT INTO clan_sessions (clan_id, code) VALUES ($1,$2) ON CONFLICT (clan_id) DO UPDATE SET code = $2, created_at = now()', [clanId, code]);
      sessionClan.set(code, clanId);
      hostAway.set(code, Date.now()); // bis jemand reinkommt
      await syncSessionSounds(code, clanId);
      notifyClan(clanId);
    }
    json(res, 200, { code, ticket: ticketFor(code, me.id) });
    return true;
  }

  json(res, 404, { error: 'Nicht gefunden' });
  return true;
}

async function route(req, res) {
  const url = new URL(req.url, 'http://x');
  const p = decodeURIComponent(url.pathname);
  const ip = clientIp(req);
  let m;

  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE',
      'Access-Control-Allow-Headers': 'Authorization, Content-Type, Range',
    });
    return res.end();
  }
  if (p === '/health') return json(res, 200, { ok: true });

  if ((m = /^\/api\/relay\/([0-9a-f]{32})$/.exec(p)) && req.method === 'PUT') return relayIn(req, res, m[1]);

  if (p === '/api/lobbies' && req.method === 'POST') {
    let name = '';
    try { name = cleanPerson(JSON.parse(String(await readBody(req, 4096)) || '{}').name); } catch {}
    const token = crypto.randomBytes(24).toString('base64url');
    for (let i = 0; i < 10; i++) {
      const code = newCode();
      const r = await db.query('INSERT INTO lobbies (code, host_token, host_name) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING',
        [code, hashToken(token), name || 'Host']);
      if (r.rowCount) {
        hostAway.set(code, Date.now()); // bis der Host sich verbindet
        return json(res, 200, { code, hostToken: token });
      }
    }
    return json(res, 503, { error: 'Kein Code frei' });
  }

  if ((m = /^\/api\/lobbies\/([^/]+)(\/.*)?$/.exec(p))) {
    if (blocked(ip)) return json(res, 429, { error: 'Zu viele falsche Codes, warte ein paar Minuten' });
    const lobby = await getLobby(normCode(m[1]));
    if (!lobby) { miss(ip); return json(res, 404, { error: 'Lobby gibt es nicht' }); }
    const rest = m[2] || '';

    if (!rest && req.method === 'GET') {
      return json(res, 200, { code: lobby.code, host: lobby.host_name, sounds: await lobbySounds(lobby.code) });
    }
    if (rest === '/sounds' && req.method === 'PUT') {
      if (!isHost(req, lobby)) return json(res, 401, { error: 'Nur der Host' });
      return setSounds(req, res, lobby);
    }
    if ((m = /^\/blobs\/([0-9a-f]{64})$/.exec(rest))) {
      const id = m[1];
      if (req.method === 'PUT') return json(res, 410, { error: 'Sounds werden nicht mehr hochgeladen – bitte ClipSound aktualisieren.' });
      const sound = await soundIn(lobby.code, id);
      if (!sound) return json(res, 404, { error: 'Nicht gefunden' });
      return pull(req, res, id, sound.name, () => lobbyDevices(lobby.code, id));
    }
    return json(res, 404, { error: 'Nicht gefunden' });
  }

  if (p.startsWith('/api/account') || p.startsWith('/api/clans')) {
    if (await clanMedia(req, res, p)) return; // Clan-Sounds/Session (brauchen die Geräte-Verbindungen)
    await clans.handle(req, res, { db, readBody, json, hooks: { membershipChanged, userGone, clanGone, clanChanged: notifyClan, presence } });
    return;
  }

  const rel = p === '/' || /^\/l\/[A-Za-z0-9]+$/.test(p) ? 'index.html'
    : p === '/datenschutz' ? 'datenschutz.html'
    : /^\/c\/[A-Za-z0-9-]+$/.test(p) ? 'einladung.html'
    : path.basename(p);
  const type = STATIC_TYPES[path.extname(rel)];
  if (!type) return json(res, 404, { error: 'Nicht gefunden' });
  serveFile(req, res, path.join(PUBLIC_DIR, rel), type, { 'Cache-Control': 'no-cache' });
}

const server = http.createServer((req, res) => {
  route(req, res).catch(e => {
    if (e.status === 413) return res.headersSent || json(res, 413, { error: 'Datei zu groß (max 15 MB)' });
    console.error(req.method, req.url, e);
    if (!res.headersSent) json(res, 500, { error: 'Serverfehler' });
  });
});

// ---------- Echtzeit ----------
const members = code => [...(lobbies.get(code) || [])].map(ws => ({ id: ws.id, name: ws.name, host: ws.isHost, volume: ws.volume, system: ws.volumeSystem }));
const findMember = (code, id) => [...(lobbies.get(code) || [])].find(ws => ws.id === id);
const sendTo = (ws, msg) => { if (ws?.readyState === 1) ws.send(JSON.stringify(msg)); };
function broadcast(code, msg) {
  const data = JSON.stringify(msg);
  for (const ws of lobbies.get(code) || []) if (ws.readyState === 1) ws.send(data);
}

// Wer was hat, ändert sich oft (jeder Gast meldet seine fertigen Dateien) – Liste nur neu schicken,
// wenn sie wirklich anders ist, und kurz sammeln
const lastSounds = new Map();   // code → zuletzt verschickte Liste (JSON)
const soundTimers = new Map();  // code → Timer
function soundsChanged(code) {
  if (!lobbies.has(code) || soundTimers.has(code)) return;
  soundTimers.set(code, setTimeout(async () => {
    soundTimers.delete(code);
    try {
      const sounds = await lobbySounds(code);
      const key = JSON.stringify(sounds);
      if (lastSounds.get(code) === key) return;
      lastSounds.set(code, key);
      broadcast(code, { type: 'sounds', sounds });
    } catch (e) { console.error('Soundliste', e); }
  }, 250));
}
// Eine Clan-Geräte-Verbindung hat sich geändert: laufende Sessions ihrer Clans angleichen
const deviceChanged = ws => {
  for (const clanId of ws.clans) {
    notifyClan(clanId); // wer online ist / welche Sounds verfügbar sind
    const code = sessionOf(clanId);
    if (code) soundsChanged(code);
  }
};

/** Nachrichten, die Lobby- und Geräte-Verbindungen gleich behandeln. true = erledigt. */
function deviceMessage(ws, msg, onHaveChanged) {
  if (msg.type === 'ping') { sendTo(ws, { type: 'pong', t: msg.t, serverTime: Date.now() }); return true; }
  if (msg.type === 'have' && Array.isArray(msg.ids)) {
    ws.has = new Set(msg.ids.filter(isId).slice(0, 2000));
    onHaveChanged();
    return true;
  }
  if (msg.type === 'nosend' && typeof msg.rid === 'string') {
    const r = relays.get(msg.rid);
    if (r && r.ws === ws) { ws.has.delete(r.id); r.done(new Error('nicht da')); }
    return true;
  }
  return false;
}

async function onMessage(ws, code, raw) {
  let msg;
  try { msg = JSON.parse(raw); } catch { return; }
  const now = Date.now();

  if (deviceMessage(ws, msg, () => soundsChanged(code))) return;

  if (msg.type === 'play' && isId(msg.id)) {
    ws.plays = ws.plays.filter(t => now - t < 1000);
    if (ws.plays.length >= 8) return; // max. 8 Sounds pro Sekunde pro Person
    ws.plays.push(now);
    return broadcast(code, { type: 'play', id: msg.id, by: ws.name, from: ws.id, at: now + PLAY_DELAY });
  }
  if (msg.type === 'stop') return broadcast(code, { type: 'stop', by: ws.name, from: ws.id });

  // Lautstärke: jedes Gerät meldet seine, jeder darf sie bei den anderen ändern
  if (msg.type === 'volume' && typeof msg.level === 'number') {
    const level = Math.round(Math.min(1, Math.max(0, msg.level)) * 100) / 100;
    if (level === ws.volume && !!msg.system === ws.volumeSystem) return;
    ws.volume = level; ws.volumeSystem = !!msg.system;
    clearTimeout(ws.volumeTimer); // beim Ziehen am Regler nicht jede Stufe verteilen
    ws.volumeTimer = setTimeout(() => broadcast(code, { type: 'members', members: members(code) }), 150);
    return;
  }
  if (msg.type === 'setvolume' && typeof msg.level === 'number') {
    const target = findMember(code, msg.to);
    if (!target || target === ws) return;
    return sendTo(target, { type: 'setvolume', level: Math.min(1, Math.max(0, msg.level)), by: ws.name });
  }

  // Gast fragt: „Darf ich den Sound behalten?“ → Host entscheidet
  if (msg.type === 'ask' && !ws.isHost && isId(msg.id)) {
    const sound = await soundIn(code, msg.id);
    if (!sound) return;
    // In der Clan-Session gehören die Sounds allen im Clan
    if (sessionClan.has(code)) return sendTo(ws, { type: 'answered', req: 'clan', id: sound.id, name: sound.name, ok: true, kind: 'ask' });
    const host = [...lobbies.get(code)].find(m => m.isHost);
    if (!host) return sendTo(ws, { type: 'answered', id: sound.id, name: sound.name, ok: false, kind: 'ask', reason: 'Der Host ist gerade nicht da' });
    const req = crypto.randomBytes(6).toString('hex');
    requests.set(req, { code, kind: 'ask', id: sound.id, name: sound.name, guest: ws.id, host: host.id, at: now });
    return sendTo(host, { type: 'asked', req, id: sound.id, name: sound.name, by: ws.name, from: ws.id });
  }
  // Host bietet einem Gast einen Sound an → Gast entscheidet
  if (msg.type === 'offer' && ws.isHost && isId(msg.id)) {
    const guest = findMember(code, msg.to);
    const sound = await soundIn(code, msg.id);
    if (!guest || guest.isHost || !sound) return;
    const req = crypto.randomBytes(6).toString('hex');
    requests.set(req, { code, kind: 'offer', id: sound.id, name: sound.name, guest: guest.id, host: ws.id, at: now });
    return sendTo(guest, { type: 'offered', req, id: sound.id, name: sound.name, by: ws.name });
  }
  // Antworten zählen nur von der Seite, die gefragt wurde
  if (msg.type === 'answer' && typeof msg.req === 'string') {
    const r = requests.get(msg.req);
    if (!r || r.code !== code || ws.id !== (r.kind === 'ask' ? r.host : r.guest)) return;
    requests.delete(msg.req);
    const result = { type: 'answered', req: msg.req, id: r.id, name: r.name, ok: msg.ok === true, kind: r.kind };
    sendTo(findMember(code, r.guest), result);
    sendTo(findMember(code, r.host), result);
  }
}

/** Geräte-Verbindung eines angemeldeten Nutzers: meldet, welche Clan-Sounds hier liegen, und schickt sie auf Anfrage. */
async function deviceConnection(ws) {
  const first = await new Promise(resolve => {
    const t = setTimeout(() => resolve(null), 10000);
    ws.once('message', raw => { clearTimeout(t); try { resolve(JSON.parse(raw)); } catch { resolve(null); } });
    ws.once('close', () => { clearTimeout(t); resolve(null); });
  });
  if (!live(ws)) return;
  const user = first?.type === 'auth' ? await clans.userFromToken(db, first.token) : null;
  if (!user) return ws.close(4401, 'Nicht eingeloggt');
  ws.userId = user.id;
  ws.clans = new Set((await db.query('SELECT clan_id FROM clan_members WHERE user_id = $1', [user.id])).rows.map(r => r.clan_id));
  if (!live(ws)) return;
  const set = devices.get(user.id) || devices.set(user.id, new Set()).get(user.id);
  // Dieselbe App-Installation verbindet sich neu (z. B. Fenster wieder geöffnet): die alte Verbindung ist verwaist
  ws.device = typeof first.device === 'string' ? first.device.slice(0, 64) : '';
  if (ws.device) for (const old of set) if (old.device === ws.device) old.close(4001, 'Ersetzt');
  // Zu viele: die älteste geht – mit 4401, damit auch ältere Apps nicht sofort wieder verbinden
  if (set.size >= MAX_DEVICES) [...set][0].close(4401, 'Zu viele Verbindungen');
  set.add(ws);
  ws.on('close', () => {
    set.delete(ws);
    if (!set.size && devices.get(user.id) === set) devices.delete(user.id);
    deviceChanged(ws);
  });
  ws.on('message', raw => {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }
    deviceMessage(ws, msg, () => deviceChanged(ws));
  });
  sendTo(ws, { type: 'ready' });
  await sendWant(ws);
  deviceChanged(ws); // ist jetzt online
}

const wss = new WebSocketServer({ server, path: '/ws', maxPayload: 192 * 1024 });
wss.on('connection', async (ws, req) => {
  const url = new URL(req.url, 'http://x');
  ws.alive = true;
  ws.has = new Set();   // Dateien (sha256), die dieses Gerät gemeldet hat
  ws.clans = new Set();
  ws.sending = 0;
  // Ohne diesen Handler beendet ein einzelner Verbindungsfehler (z. B. eine Nachricht über maxPayload) den ganzen Server.
  // ws schließt die Verbindung danach selbst (1009), die close-Handler räumen auf.
  ws.on('error', e => console.error('WS-Fehler', e.code || e.message));
  ws.on('pong', () => { ws.alive = true; });
  ws.on('close', () => dropRelays(ws));

  if (url.searchParams.get('share')) return deviceConnection(ws).catch(e => { console.error('Geräte-Verbindung', e); ws.close(1011); });

  const code = normCode(url.searchParams.get('lobby'));
  const ip = clientIp(req);
  ws.id = crypto.randomBytes(4).toString('hex');
  ws.name = cleanPerson(url.searchParams.get('name')) || 'Gast';
  ws.plays = [];
  // Nachrichten erst annehmen, wenn die Verbindung fertig eingerichtet ist
  const early = [];
  ws.on('message', raw => early.push(raw));

  let lobby;
  try {
    if (blocked(ip)) return ws.close(4429, 'Zu viele Versuche');
    lobby = await getLobby(code);
    if (lobby && sessionClan.has(code)) {
      // Clan-Session: nur Mitglieder mit Ticket, der Name kommt aus dem Konto
      const userId = ticketUser(code, url.searchParams.get('ticket'));
      const user = userId && await clans.memberRole(db, sessionClan.get(code), userId)
        ? (await db.query('SELECT id, name FROM users WHERE id = $1', [userId])).rows[0] : null;
      if (!user) return ws.close(4401, 'Clan-Session: bitte in der App mit deinem Konto beitreten');
      ws.userId = user.id;
      ws.name = user.name;
    }
  } catch (e) {
    console.error('WS', e);
    return ws.close(1011, 'Serverfehler');
  }
  if (!lobby) { miss(ip); return ws.close(4404, 'Lobby gibt es nicht'); }
  const token = url.searchParams.get('token');
  if (token && hashToken(token) !== lobby.host_token) return ws.close(4401, 'Falscher Host-Token');
  ws.isHost = !!token;
  const keepsOpen = m => m.isHost || sessionClan.has(code);

  if (!lobbies.has(code)) lobbies.set(code, new Set());
  const set = lobbies.get(code);
  if (!ws.isHost && set.size >= MAX_MEMBERS) return ws.close(4409, 'Lobby ist voll');
  if (ws.isHost) for (const old of set) if (old.isHost) old.close(4000, 'Host hat sich neu verbunden');
  if (keepsOpen(ws)) hostAway.delete(code);
  set.add(ws);
  if (sessionClan.has(code)) notifyClan(sessionClan.get(code)); // ist jetzt in der Session

  ws.on('close', () => {
    set.delete(ws);
    if (sessionClan.has(code)) notifyClan(sessionClan.get(code));
    if (keepsOpen(ws) && ![...set].some(keepsOpen)) hostAway.set(code, Date.now());
    if (set.size) {
      broadcast(code, { type: 'members', members: members(code) });
      if (ws.has.size) soundsChanged(code);
    } else if (lobbies.get(code) === set) { lobbies.delete(code); lastSounds.delete(code); }
  });

  sendTo(ws, { type: 'hello', you: ws.id, isHost: ws.isHost, host: lobby.host_name, code,
    sounds: await lobbySounds(code), members: members(code), serverTime: Date.now() });
  broadcast(code, { type: 'members', members: members(code) });

  ws.removeAllListeners('message');
  const handle = raw => onMessage(ws, code, raw).catch(e => console.error('WS-Nachricht', e));
  ws.on('message', handle);
  early.forEach(handle);
});

setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.alive) { ws.terminate(); continue; }
    ws.alive = false;
    ws.ping();
  }
}, 25000);

// Lobbys ohne Host (Clan-Sessions ohne Leute) schließen, alte Anfragen aufräumen
setInterval(async () => {
  try {
    const now = Date.now();
    const { rows } = await db.query('SELECT code FROM lobbies');
    for (const { code } of rows) {
      const clan = sessionClan.has(code);
      if ([...(lobbies.get(code) || [])].some(ws => clan || ws.isHost)) continue;
      if (!hostAway.has(code)) hostAway.set(code, now); // z. B. nach Server-Neustart
      if (now - hostAway.get(code) > HOST_GRACE) await closeLobby(code, clan ? 'Alle haben die Clan-Session verlassen' : 'Der Host hat die Lobby verlassen');
    }
    for (const [req, r] of requests) if (now - r.at > 10 * 60 * 1000) requests.delete(req);
  } catch (e) { console.error('Aufräumen', e); }
}, 15000);

async function start(attempt = 1) {
  try {
    await migrate();
  } catch (e) {
    console.error(`Datenbank nicht erreichbar (Versuch ${attempt}): ${e.code || e.message}`);
    if (attempt >= 30) process.exit(1);
    return setTimeout(() => start(attempt + 1), 2000);
  }
  server.listen(PORT, '0.0.0.0', () => console.log(`ClipSound Lobbys auf Port ${PORT} – speichert keine Audio-Dateien`));
}
start();
