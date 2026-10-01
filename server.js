// ClipSound (Web) – läuft nur auf localhost, keine Dependencies.
// Start: node server.js  →  http://localhost:3000
const http = require('http');
const fs = require('fs');
const path = require('path');

const PORT = process.env.PORT || 3000;
const SOUND_DIR = path.join(__dirname, 'sounds');
const PUBLIC_DIR = path.join(__dirname, 'public');
const MAX_SIZE = 15 * 1024 * 1024; // 15 MB pro Sound

const AUDIO_TYPES = {
  '.mp3': 'audio/mpeg',
  '.wav': 'audio/wav',
  '.ogg': 'audio/ogg',
  '.opus': 'audio/ogg',
  '.m4a': 'audio/mp4',
  '.aac': 'audio/aac',
  '.webm': 'audio/webm',
  '.flac': 'audio/flac',
};
const STATIC_TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript' };

fs.mkdirSync(SOUND_DIR, { recursive: true });

// Prüft die ersten Bytes, damit wirklich nur Audio-Dateien landen
function looksLikeAudio(buf) {
  const s = (a, b) => buf.toString('latin1', a, b);
  if (buf.length < 12) return false;
  if (s(0, 3) === 'ID3') return true;                                  // mp3 mit Tag
  if (buf[0] === 0xff && (buf[1] & 0xe0) === 0xe0) return true;        // mp3/aac Frame
  if (s(0, 4) === 'RIFF' && s(8, 12) === 'WAVE') return true;          // wav
  if (s(0, 4) === 'OggS') return true;                                 // ogg/opus
  if (s(0, 4) === 'fLaC') return true;                                 // flac
  if (s(4, 8) === 'ftyp') return true;                                 // m4a
  if (buf[0] === 0x1a && buf[1] === 0x45 && buf[2] === 0xdf && buf[3] === 0xa3) return true; // webm
  return false;
}

function cleanName(name) {
  const ext = path.extname(name).toLowerCase();
  const base = path.basename(name, path.extname(name))
    .replace(/[^\p{L}\p{N} _\-()!?]/gu, '')
    .trim()
    .slice(0, 60) || 'sound';
  return { base, ext };
}

function uniqueName(base, ext) {
  let name = base + ext;
  for (let i = 2; fs.existsSync(path.join(SOUND_DIR, name)); i++) name = `${base} (${i})${ext}`;
  return name;
}

function json(res, code, data) {
  res.writeHead(code, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(data));
}

function listSounds() {
  return fs.readdirSync(SOUND_DIR)
    .filter(f => AUDIO_TYPES[path.extname(f).toLowerCase()])
    .map(f => ({ name: f, mtime: fs.statSync(path.join(SOUND_DIR, f)).mtimeMs }))
    .sort((a, b) => b.mtime - a.mtime)
    .map(f => f.name);
}

// Audio mit Range-Support ausliefern (Safari braucht das)
function serveFile(req, res, file, type) {
  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) return json(res, 404, { error: 'Nicht gefunden' });
    const range = req.headers.range && /bytes=(\d*)-(\d*)/.exec(req.headers.range);
    if (range) {
      const start = range[1] ? +range[1] : 0;
      const end = range[2] ? Math.min(+range[2], st.size - 1) : st.size - 1;
      res.writeHead(206, {
        'Content-Type': type, 'Accept-Ranges': 'bytes',
        'Content-Range': `bytes ${start}-${end}/${st.size}`, 'Content-Length': end - start + 1,
      });
      fs.createReadStream(file, { start, end }).pipe(res);
    } else {
      res.writeHead(200, { 'Content-Type': type, 'Content-Length': st.size, 'Accept-Ranges': 'bytes' });
      fs.createReadStream(file).pipe(res);
    }
  });
}

function handleUpload(req, res) {
  const { base, ext } = cleanName(decodeURIComponent(req.headers['x-filename'] || ''));
  if (!AUDIO_TYPES[ext]) return json(res, 415, { error: 'Nur Sounds erlaubt (mp3, wav, ogg, m4a, …)' });

  const chunks = [];
  let size = 0, aborted = false;
  req.on('data', c => {
    size += c.length;
    if (size > MAX_SIZE && !aborted) {
      aborted = true;
      json(res, 413, { error: 'Datei zu groß (max 15 MB)' });
      req.destroy();
    } else if (!aborted) chunks.push(c);
  });
  req.on('end', () => {
    if (aborted) return;
    const buf = Buffer.concat(chunks);
    if (!looksLikeAudio(buf)) return json(res, 415, { error: 'Das ist keine Audio-Datei' });
    const name = uniqueName(base, ext);
    fs.writeFile(path.join(SOUND_DIR, name), buf, err =>
      err ? json(res, 500, { error: 'Speichern fehlgeschlagen' }) : json(res, 200, { name }));
  });
}

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const p = decodeURIComponent(url.pathname);

  if (p === '/api/sounds' && req.method === 'GET') return json(res, 200, listSounds());
  if (p === '/api/sounds' && req.method === 'POST') return handleUpload(req, res);

  if (p.startsWith('/sounds/')) {
    const name = path.basename(p.slice(8));
    const file = path.join(SOUND_DIR, name);
    if (req.method === 'DELETE') {
      return fs.unlink(file, err => err ? json(res, 404, { error: 'Nicht gefunden' }) : json(res, 200, { ok: true }));
    }
    const type = AUDIO_TYPES[path.extname(name).toLowerCase()];
    if (!type) return json(res, 404, { error: 'Nicht gefunden' });
    return serveFile(req, res, file, type);
  }

  const rel = p === '/' ? 'index.html' : path.basename(p);
  const type = STATIC_TYPES[path.extname(rel)];
  if (!type) return json(res, 404, { error: 'Nicht gefunden' });
  serveFile(req, res, path.join(PUBLIC_DIR, rel), type);
}).listen(PORT, '127.0.0.1', () => console.log(`🔊 ClipSound läuft auf http://localhost:${PORT}`));
