// Testwerkzeug für ClipSound-Lobbys (spricht dasselbe Protokoll wie die Apps).
//   node lobby-tool.mjs check <url>                 alles einmal durchprüfen (Host + Gast in einem)
//   node lobby-tool.mjs host  <url> <ordner> [sek]  Lobby mit den Sounds aus <ordner> öffnen, Anfragen annehmen
//   node lobby-tool.mjs guest <url> <code> [sek]    beitreten, ersten Sound abspielen, danach behalten wollen
import WebSocket from 'ws';
import fs from 'fs';
import path from 'path';
import crypto from 'crypto';

const [mode, base, arg, secs] = process.argv.slice(2);
const wsBase = base.replace(/^http/, 'ws');
const sha = buf => crypto.createHash('sha256').update(buf).digest('hex');
const ok = (c, m) => { if (!c) { console.error('FEHLER:', m); process.exit(1); } console.log('✓', m); };
const log = (...a) => console.log(new Date().toISOString().slice(11, 23), ...a);

function client(code, name, token) {
  const ws = new WebSocket(`${wsBase}/ws?lobby=${code}&name=${encodeURIComponent(name)}${token ? '&token=' + token : ''}`);
  const queue = [];
  const waiters = [];
  ws.on('message', d => {
    const m = JSON.parse(d);
    const w = waiters.findIndex(x => x.type === m.type);
    if (w >= 0) waiters.splice(w, 1)[0].res(m); else queue.push(m);
  });
  ws.next = (type, ms = 8000) => {
    const i = queue.findIndex(m => m.type === type);
    if (i >= 0) return Promise.resolve(queue.splice(i, 1)[0]);
    return new Promise((res, rej) => {
      const w = { type, res };
      waiters.push(w);
      setTimeout(() => { const j = waiters.indexOf(w); if (j >= 0) { waiters.splice(j, 1); rej(new Error('Timeout: ' + type)); } }, ms);
    });
  };
  ws.none = async (type, ms = 800) => { try { await ws.next(type, ms); return false; } catch { return true; } };
  ws.sendJ = m => ws.send(JSON.stringify(m));
  ws.closed = new Promise(r => ws.on('close', (c, why) => r({ code: c, why: String(why) })));
  return ws;
}

async function openLobby(name, files) {
  const r = await (await fetch(`${base}/api/lobbies`, { method: 'POST', body: JSON.stringify({ name }) })).json();
  const auth = { Authorization: 'Bearer ' + r.hostToken };
  const sounds = files.map(f => { const buf = fs.readFileSync(f); return { id: sha(buf), name: path.basename(f), size: buf.length, buf }; });
  const set = await (await fetch(`${base}/api/lobbies/${r.code}/sounds`, { method: 'PUT', headers: auth,
    body: JSON.stringify({ sounds: sounds.map(({ buf, ...s }) => s) }) })).json();
  for (const id of set.missing) {
    const s = sounds.find(x => x.id === id);
    const up = await fetch(`${base}/api/lobbies/${r.code}/blobs/${id}`, { method: 'PUT', headers: auth, body: s.buf });
    if (!up.ok) throw new Error('Upload ' + s.name + ': ' + (await up.text()));
  }
  return { ...r, auth, sounds, set };
}

const audioIn = dir => fs.readdirSync(dir).filter(f => /\.(mp3|wav|m4a|ogg|flac|aiff?|caf)$/i.test(f)).map(f => path.join(dir, f));

if (mode === 'check') {
  const files = audioIn('../windows/testdata').slice(0, 2);
  const L = await openLobby('Daniel', files);
  ok(/^[A-Z2-9]{5}$/.test(L.code), 'Lobby offen, Code ' + L.code + ' (5 Stellen)');
  ok(L.set.missing.length === 2 || L.set.missing.length === 0, 'Server wollte beide Dateien (oder hatte sie schon vom letzten Test)');
  const again = await (await fetch(`${base}/api/lobbies/${L.code}/sounds`, { method: 'PUT', headers: L.auth,
    body: JSON.stringify({ sounds: L.sounds.map(({ buf, ...s }) => s) }) })).json();
  ok(again.missing.length === 0, 'Zweites Mal: nichts muss neu hoch (Hash-Abgleich)');

  const H = client(L.code, 'Daniel', L.hostToken);
  const hh = await H.next('hello');
  ok(hh.isHost && hh.sounds.length === 2, 'Host verbunden, sieht seine 2 Sounds');
  const G = client(L.code.toLowerCase(), 'Lukas');
  const gh = await G.next('hello');
  ok(!gh.isHost && gh.host === 'Daniel' && gh.sounds.map(s => s.id).join() === L.sounds.map(s => s.id).join(), 'Gast (Code klein geschrieben) sieht die Host-Sounds');
  const blob = Buffer.from(await (await fetch(`${base}/api/lobbies/${L.code}/blobs/${gh.sounds[0].id}`)).arrayBuffer());
  ok(sha(blob) === gh.sounds[0].id, 'Gast lädt die Datei, Prüfsumme stimmt');

  const gPut = await fetch(`${base}/api/lobbies/${L.code}/sounds`, { method: 'PUT', body: '{"sounds":[]}' });
  ok(gPut.status === 401, 'Gast darf die Liste nicht ändern');
  const badTok = client(L.code, 'X', 'falsch');
  ok((await badTok.closed).code === 4401, 'Falscher Host-Token wird abgewiesen');

  G.sendJ({ type: 'play', id: gh.sounds[0].id });
  const [p1, p2] = await Promise.all([H.next('play'), G.next('play')]);
  ok(p1.at === p2.at && p1.by === 'Lukas', 'Gast drückt → bei beiden mit gleicher Startzeit');
  H.sendJ({ type: 'stop' });
  ok((await G.next('stop')).by === 'Daniel', 'Stop vom Host kommt beim Gast an');

  // Gast fragt, Host lehnt ab
  G.sendJ({ type: 'ask', id: gh.sounds[0].id });
  const asked = await H.next('asked');
  ok(asked.by === 'Lukas' && asked.id === gh.sounds[0].id, 'Host bekommt die Anfrage');
  G.sendJ({ type: 'answer', req: asked.req, ok: true });
  ok(await G.none('answered'), 'Gast kann seine eigene Anfrage nicht selbst genehmigen');
  H.sendJ({ type: 'answer', req: asked.req, ok: false });
  const [a1, a2] = await Promise.all([G.next('answered'), H.next('answered')]);
  ok(!a1.ok && !a2.ok, 'Ablehnung kommt bei beiden an');
  // Gast fragt, Host stimmt zu
  G.sendJ({ type: 'ask', id: gh.sounds[1].id });
  const asked2 = await H.next('asked');
  H.sendJ({ type: 'answer', req: asked2.req, ok: true });
  ok((await G.next('answered')).ok && (await H.next('answered')).ok, 'Zustimmung kommt bei beiden an');
  // Host bietet an, Gast nimmt an
  const me = (await G.next('members').catch(() => ({ members: gh.members }))).members;
  const guestId = gh.you;
  H.sendJ({ type: 'offer', id: gh.sounds[0].id, to: guestId });
  const off = await G.next('offered');
  ok(off.by === 'Daniel', 'Gast bekommt das Angebot');
  H.sendJ({ type: 'answer', req: off.req, ok: true });
  ok(await H.none('answered'), 'Host kann sein eigenes Angebot nicht selbst annehmen');
  G.sendJ({ type: 'answer', req: off.req, ok: true });
  ok((await G.next('answered')).ok, 'Angebot angenommen');

  // Lautstärke: Gast meldet, Host stellt ein, Gast stellt den Host ein
  G.sendJ({ type: 'volume', level: 0.42, system: true });
  let mem;
  do { mem = await H.next('members'); } while (!mem.members.some(m => m.volume === 0.42));
  ok(mem.members.find(m => m.id === gh.you).system === true, 'Host sieht die Lautstärke des Gasts (42 %, echte Lautsprecher)');
  H.sendJ({ type: 'setvolume', to: gh.you, level: 0.8 });
  const sv = await G.next('setvolume');
  ok(sv.level === 0.8 && sv.by === 'Daniel', 'Host stellt den Gast auf 80 %');
  G.sendJ({ type: 'setvolume', to: hh.you, level: 0.3 });
  const sh = await H.next('setvolume');
  ok(sh.level === 0.3 && sh.by === 'Lukas', 'Gast stellt den Host auf 30 %');

  const nope = await fetch(`${base}/api/lobbies/ZZZZZ`);
  ok(nope.status === 404, 'Falscher Code → 404');
  const G2 = client('ZZZZZ', 'X');
  ok((await G2.closed).code === 4404, 'Falscher Code per WebSocket → 4404');

  G.close(); H.close();
  console.log('CODE', L.code);
  process.exit(0);
}

if (mode === 'host') {
  const L = await openLobby('Testhost', audioIn(arg));
  console.log('CODE', L.code);
  const H = client(L.code, 'Testhost', L.hostToken);
  H.on('message', d => {
    const m = JSON.parse(d);
    log('←', m.type, JSON.stringify(m).slice(0, 160));
    if (m.type === 'asked') H.sendJ({ type: 'answer', req: m.req, ok: true });
    if (m.type === 'members') for (const g of m.members.filter(x => !x.host && !H.offered?.has(x.id))) {
      (H.offered ??= new Set()).add(g.id);
      H.sendJ({ type: 'offer', id: L.sounds[L.sounds.length - 1].id, to: g.id });
    }
  });
  setTimeout(() => process.exit(0), (+secs || 60) * 1000);
}

if (mode === 'guest') {
  const G = client(arg, 'Testgast');
  let first;
  G.on('message', d => {
    const m = JSON.parse(d);
    log('←', m.type, JSON.stringify(m).slice(0, 160));
    if (m.type === 'hello') {
      first = m.sounds[0]?.id;
      setTimeout(() => G.sendJ({ type: 'play', id: first }), 1500);
      setTimeout(() => G.sendJ({ type: 'ask', id: first }), 2500);
    }
    if (m.type === 'offered') G.sendJ({ type: 'answer', req: m.req, ok: true });
  });
  setTimeout(() => process.exit(0), (+secs || 20) * 1000);
}
