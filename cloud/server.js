// ClipSound Lobbys – läuft auf Railway.
// Wer eine Lobby öffnet (Host), bringt seine Sounds mit. Wer mit dem 5-stelligen Code beitritt,
// sieht sie und kann sie abspielen – jeder gedrückte Sound läuft bei allen gleichzeitig.
// Einen Sound behalten geht nur, wenn beide Seiten zustimmen.
// Metadaten liegen in Postgres (DATABASE_URL), die Audio-Dateien auf dem Volume (DATA_DIR).
//
// HTTP
//   POST /api/lobbies                       {name}                → {code, hostToken}
//   GET  /api/lobbies/:code                                       → {code, host, sounds}
//   PUT  /api/lobbies/:code/sounds  (Host)  {sounds:[{id,name,size}]} → {missing:[id], skipped:[name]}
//        id = sha256 der Datei (hex). Ersetzt die ganze Liste; fehlende Dateien danach hochladen.
//   PUT  /api/lobbies/:code/blobs/:id (Host) Rohdaten der Datei
//   GET  /api/lobbies/:code/blobs/:id                             → Audio
//   Host-Anfragen tragen „Authorization: Bearer <hostToken>“.
//
// WebSocket  wss://…/ws?lobby=CODE&name=Daniel[&token=hostToken]
//   Server → hello {you, isHost, host, code, sounds, members, serverTime}, sounds {sounds}, members {members},
//            play {id, by, from, at}, stop {by, from}, pong {t, serverTime},
//            asked {req, id, name, by, from}      (an den Host: Gast möchte den Sound behalten)
//            offered {req, id, name, by}          (an einen Gast: Host bietet einen Sound an)
//            answered {req, id, name, ok, kind}   (an beide: Ergebnis; kind = ask | offer)
//            closed {reason}                      (Lobby ist zu, danach Close 4410)
//            setvolume {level, by}               (an ein Gerät: der Host stellt die Lautstärke ein, 0…1)
//   Client → play {id}, stop {}, ping {t}, ask {id}, offer {id, to}, answer {req, ok},
//            volume {level, system}  (eigene Lautstärke melden; system = echte Lautsprecher, sonst nur App)
//            setvolume {to, level}   (nur Host)
//   Close-Codes: 4404 Lobby gibt's nicht, 4401 falscher Host-Token, 4409 voll, 4410 geschlossen, 4429 zu viele Versuche
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { Pool } = require('pg');
const { WebSocketServer } = require('ws');

const PORT = process.env.PORT || 3000;
const DATA_DIR = process.env.DATA_DIR || process.env.RAILWAY_VOLUME_MOUNT_PATH || path.join(__dirname, 'data');
const BLOB_DIR = path.join(DATA_DIR, 'blobs');
const PUBLIC_DIR = path.join(__dirname, 'public');
const MAX_FILE = 15 * 1024 * 1024;   // pro Sound
const MAX_LOBBY = 150 * 1024 * 1024; // alle Sounds einer Lobby zusammen
const MAX_SOUNDS = 300;
const MAX_MEMBERS = 8;
const PLAY_DELAY = 150;              // ms Vorlauf, damit alle zur gleichen Zeit starten
const HOST_GRACE = 60 * 1000;        // so lange wartet eine Lobby, wenn der Host weg ist

const AUDIO_TYPES = {
  '.mp3': 'audio/mpeg', '.wav': 'audio/wav', '.ogg': 'audio/ogg', '.opus': 'audio/ogg',
  '.m4a': 'audio/mp4', '.aac': 'audio/aac', '.webm': 'audio/webm', '.flac': 'audio/flac',
  '.aif': 'audio/aiff', '.aiff': 'audio/aiff', '.caf': 'audio/x-caf',
};
const STATIC_TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.png': 'image/png', '.svg': 'image/svg+xml' };

fs.mkdirSync(BLOB_DIR, { recursive: true });

const db = new Pool({
  connectionString: process.env.DATABASE_URL,
  ssl: /railway\.internal|localhost|127\.0\.0\.1/.test(process.env.DATABASE_URL || '') ? false : { rejectUnauthorized: false },
});

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
  `);
}

// ---------- Hilfen ----------
// 5 Zeichen ohne Verwechsler (kein 0/O, 1/I/L) → 31^5 ≈ 28 Mio. Codes
const CODE_CHARS = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
const newCode = () => [...crypto.randomBytes(5)].map(b => CODE_CHARS[b % CODE_CHARS.length]).join('');
const normCode = c => String(c || '').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 5);
const hashToken = t => crypto.createHash('sha256').update(String(t || '')).digest('hex');
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
setInterval(() => { for (const [ip, l] of misses) if (!l.some(t => Date.now() - t < 600000)) misses.delete(ip); }, 600000);

// ---------- Datenbank ----------
async function getLobby(code) {
  const { rows } = await db.query('SELECT code, host_token, host_name FROM lobbies WHERE code = $1', [code]);
  return rows[0] || null;
}

/** Nur Sounds, deren Datei schon da ist */
async function lobbySounds(code) {
  const { rows } = await db.query('SELECT id, name, size FROM lobby_sounds WHERE code = $1 ORDER BY pos', [code]);
  return rows.filter(r => fs.existsSync(path.join(BLOB_DIR, r.id)));
}

async function soundIn(code, id) {
  const { rows } = await db.query('SELECT id, name, size FROM lobby_sounds WHERE code = $1 AND id = $2', [code, id]);
  return rows[0] || null;
}

/** Dateien löschen, die keine Lobby mehr braucht */
async function sweepBlobs() {
  const { rows } = await db.query('SELECT DISTINCT id FROM lobby_sounds');
  const used = new Set(rows.map(r => r.id));
  for (const f of await fs.promises.readdir(BLOB_DIR)) {
    if (used.has(f.replace(/\.part$/, ''))) continue;
    const st = await fs.promises.stat(path.join(BLOB_DIR, f)).catch(() => null);
    // gerade hochgeladene Dateien kurz in Ruhe lassen
    if (st && Date.now() - st.mtimeMs > 5 * 60 * 1000) await fs.promises.unlink(path.join(BLOB_DIR, f)).catch(() => {});
  }
}

async function closeLobby(code, reason) {
  await db.query('DELETE FROM lobbies WHERE code = $1', [code]);
  hostAway.delete(code);
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
    const range = req.headers.range && /bytes=(\d*)-(\d*)/.exec(req.headers.range);
    const head = { 'Content-Type': type, 'Accept-Ranges': 'bytes', 'Access-Control-Allow-Origin': '*', ...extra };
    if (range) {
      const start = range[1] ? +range[1] : 0;
      const end = range[2] ? Math.min(+range[2], st.size - 1) : st.size - 1;
      res.writeHead(206, { ...head, 'Content-Range': `bytes ${start}-${end}/${st.size}`, 'Content-Length': end - start + 1 });
      fs.createReadStream(file, { start, end }).pipe(res);
    } else {
      res.writeHead(200, { ...head, 'Content-Length': st.size });
      fs.createReadStream(file).pipe(res);
    }
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
  const missing = list.filter(s => !fs.existsSync(path.join(BLOB_DIR, s.id))).map(s => s.id);
  json(res, 200, { missing, skipped });
  broadcastSounds(lobby.code);
}

async function putBlob(req, res, lobby, id) {
  const sound = await soundIn(lobby.code, id);
  if (!sound) return json(res, 404, { error: 'Sound steht nicht in der Liste' });
  const buf = await readBody(req, MAX_FILE);
  if (crypto.createHash('sha256').update(buf).digest('hex') !== id) return json(res, 400, { error: 'Prüfsumme stimmt nicht' });
  if (!looksLikeAudio(buf)) return json(res, 415, { error: 'Das ist keine Audio-Datei' });
  const file = path.join(BLOB_DIR, id);
  await fs.promises.writeFile(file + '.part', buf);
  await fs.promises.rename(file + '.part', file);
  json(res, 200, { ok: true });
  broadcastSounds(lobby.code);
}

async function route(req, res) {
  const url = new URL(req.url, 'http://x');
  const p = decodeURIComponent(url.pathname);
  const ip = clientIp(req);
  let m;

  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Methods': 'GET, POST, PUT',
      'Access-Control-Allow-Headers': 'Authorization, Content-Type, Range',
    });
    return res.end();
  }
  if (p === '/health') return json(res, 200, { ok: true });

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
      if (req.method === 'PUT') {
        if (!isHost(req, lobby)) return json(res, 401, { error: 'Nur der Host' });
        return putBlob(req, res, lobby, id);
      }
      const sound = await soundIn(lobby.code, id);
      if (!sound) return json(res, 404, { error: 'Nicht gefunden' });
      return serveFile(req, res, path.join(BLOB_DIR, id), AUDIO_TYPES[path.extname(sound.name).toLowerCase()],
        { 'Cache-Control': 'public, max-age=31536000, immutable' }); // gleicher Hash = gleiche Datei
    }
    return json(res, 404, { error: 'Nicht gefunden' });
  }

  const rel = p === '/' || /^\/l\/[A-Za-z0-9]+$/.test(p) ? 'index.html' : path.basename(p);
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
const lobbies = new Map();  // code → Set<ws>
const hostAway = new Map(); // code → seit wann kein Host verbunden ist
const requests = new Map(); // req → {code, kind, id, name, guest, host, at}

const members = code => [...(lobbies.get(code) || [])].map(ws => ({ id: ws.id, name: ws.name, host: ws.isHost, volume: ws.volume, system: ws.volumeSystem }));
const findMember = (code, id) => [...(lobbies.get(code) || [])].find(ws => ws.id === id);
const sendTo = (ws, msg) => { if (ws?.readyState === 1) ws.send(JSON.stringify(msg)); };
function broadcast(code, msg) {
  const data = JSON.stringify(msg);
  for (const ws of lobbies.get(code) || []) if (ws.readyState === 1) ws.send(data);
}
async function broadcastSounds(code) {
  if (lobbies.has(code)) broadcast(code, { type: 'sounds', sounds: await lobbySounds(code) });
}

async function onMessage(ws, code, raw) {
  let msg;
  try { msg = JSON.parse(raw); } catch { return; }
  const now = Date.now();

  if (msg.type === 'ping') return sendTo(ws, { type: 'pong', t: msg.t, serverTime: now });

  if (msg.type === 'play' && isId(msg.id)) {
    ws.plays = ws.plays.filter(t => now - t < 1000);
    if (ws.plays.length >= 8) return; // max. 8 Sounds pro Sekunde pro Person
    ws.plays.push(now);
    return broadcast(code, { type: 'play', id: msg.id, by: ws.name, from: ws.id, at: now + PLAY_DELAY });
  }
  if (msg.type === 'stop') return broadcast(code, { type: 'stop', by: ws.name, from: ws.id });

  // Lautstärke: jedes Gerät meldet seine, nur der Host darf sie bei anderen ändern
  if (msg.type === 'volume' && typeof msg.level === 'number') {
    const level = Math.round(Math.min(1, Math.max(0, msg.level)) * 100) / 100;
    if (level === ws.volume && !!msg.system === ws.volumeSystem) return;
    ws.volume = level; ws.volumeSystem = !!msg.system;
    clearTimeout(ws.volumeTimer); // beim Ziehen am Regler nicht jede Stufe verteilen
    ws.volumeTimer = setTimeout(() => broadcast(code, { type: 'members', members: members(code) }), 150);
    return;
  }
  if (msg.type === 'setvolume' && ws.isHost && typeof msg.level === 'number') {
    const target = findMember(code, msg.to);
    if (!target || target === ws) return;
    return sendTo(target, { type: 'setvolume', level: Math.min(1, Math.max(0, msg.level)), by: ws.name });
  }

  // Gast fragt: „Darf ich den Sound behalten?“ → Host entscheidet
  if (msg.type === 'ask' && !ws.isHost && isId(msg.id)) {
    const host = [...lobbies.get(code)].find(m => m.isHost);
    const sound = await soundIn(code, msg.id);
    if (!sound) return;
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

const wss = new WebSocketServer({ server, path: '/ws', maxPayload: 4096 });
wss.on('connection', async (ws, req) => {
  const url = new URL(req.url, 'http://x');
  const code = normCode(url.searchParams.get('lobby'));
  const ip = clientIp(req);
  ws.id = crypto.randomBytes(4).toString('hex');
  ws.name = cleanPerson(url.searchParams.get('name')) || 'Gast';
  ws.alive = true;
  ws.plays = [];
  // Nachrichten erst annehmen, wenn die Verbindung fertig eingerichtet ist
  const early = [];
  ws.on('message', raw => early.push(raw));

  let lobby;
  try {
    if (blocked(ip)) return ws.close(4429, 'Zu viele Versuche');
    lobby = await getLobby(code);
  } catch (e) {
    console.error('WS', e);
    return ws.close(1011, 'Serverfehler');
  }
  if (!lobby) { miss(ip); return ws.close(4404, 'Lobby gibt es nicht'); }
  const token = url.searchParams.get('token');
  if (token && hashToken(token) !== lobby.host_token) return ws.close(4401, 'Falscher Host-Token');
  ws.isHost = !!token;

  if (!lobbies.has(code)) lobbies.set(code, new Set());
  const set = lobbies.get(code);
  if (!ws.isHost && set.size >= MAX_MEMBERS) return ws.close(4409, 'Lobby ist voll');
  if (ws.isHost) {
    for (const old of set) if (old.isHost) old.close(4000, 'Host hat sich neu verbunden');
    hostAway.delete(code);
  }
  set.add(ws);

  ws.on('pong', () => { ws.alive = true; });
  ws.on('close', () => {
    set.delete(ws);
    if (ws.isHost && ![...set].some(m => m.isHost)) hostAway.set(code, Date.now());
    if (set.size) broadcast(code, { type: 'members', members: members(code) });
    else if (lobbies.get(code) === set) lobbies.delete(code);
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

// Lobbys ohne Host schließen, alte Anfragen und Dateien aufräumen
setInterval(async () => {
  try {
    const now = Date.now();
    const { rows } = await db.query('SELECT code FROM lobbies');
    for (const { code } of rows) {
      if ([...(lobbies.get(code) || [])].some(ws => ws.isHost)) continue;
      if (!hostAway.has(code)) hostAway.set(code, now); // z. B. nach Server-Neustart
      if (now - hostAway.get(code) > HOST_GRACE) await closeLobby(code, 'Der Host hat die Lobby verlassen');
    }
    for (const [req, r] of requests) if (now - r.at > 10 * 60 * 1000) requests.delete(req);
    await sweepBlobs();
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
  server.listen(PORT, '0.0.0.0', () => console.log(`ClipSound Lobbys auf Port ${PORT}, Daten in ${DATA_DIR}`));
}
start();
