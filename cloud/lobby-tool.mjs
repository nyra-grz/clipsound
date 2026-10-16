// Testwerkzeug für ClipSound-Lobbys (spricht dasselbe Protokoll wie die Apps).
//   node lobby-tool.mjs check <url>                 alles einmal durchprüfen (Host + Gast in einem)
//   node lobby-tool.mjs clan-check <url>            Konten, Clans, Clan-Sounds und Clan-Session durchprüfen
//                                                   (braucht DATABASE_URL derselben Datenbank, um die Mail-Codes zu lesen)
//   node lobby-tool.mjs host  <url> <ordner> [sek]  Lobby mit den Sounds aus <ordner> öffnen, Anfragen annehmen
//   node lobby-tool.mjs guest <url> <code> [sek]    beitreten, ersten Sound abspielen, danach behalten wollen
// Der Server speichert keine Dateien: wer eine Datei hat, schickt sie auf „send“ an /api/relay/<rid>.
import WebSocket from 'ws';
import fs from 'fs';
import path from 'path';
import crypto from 'crypto';

const [mode, base, arg, secs] = process.argv.slice(2);
const wsBase = base.replace(/^http/, 'ws');
const sha = buf => crypto.createHash('sha256').update(buf).digest('hex');
const ok = (c, m) => { if (!c) { console.error('FEHLER:', m); process.exit(1); } console.log('✓', m); };
const log = (...a) => console.log(new Date().toISOString().slice(11, 23), ...a);

/** Datei auf „send“ an den Server schicken (oder absagen, wenn sie hier nicht liegt). */
async function serve(ws, m) {
  const buf = ws.files?.get(m.id);
  ws.served = (ws.served || 0) + 1;
  if (ws.delay) await new Promise(r => setTimeout(r, ws.delay));
  if (ws.silent) return; // Test: Gerät, das nicht mehr reagiert
  if (!buf) return ws.sendJ({ type: 'nosend', rid: m.rid });
  await fetch(`${base}/api/relay/${m.rid}`, { method: 'PUT', body: ws.corrupt ? Buffer.from('kaputt-kaputt-kaputt') : buf });
}

function client(code, name, token, query = '') {
  const url = code === null ? `${wsBase}/ws?share=1`
    : `${wsBase}/ws?lobby=${code}&name=${encodeURIComponent(name)}${token ? '&token=' + token : ''}${query}`;
  const ws = new WebSocket(url);
  const queue = [];
  const waiters = [];
  ws.on('message', d => {
    const m = JSON.parse(d);
    if (m.type === 'send') return serve(ws, m);
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
  ws.opened = new Promise(r => ws.on('open', r));
  /** Dateien bereitstellen und dem Server melden */
  ws.offerFiles = list => { ws.files = new Map(list.map(f => [f.id, f.buf])); ws.sendJ({ type: 'have', ids: [...ws.files.keys()] }); };
  return ws;
}

async function openLobby(name, files) {
  const r = await (await fetch(`${base}/api/lobbies`, { method: 'POST', body: JSON.stringify({ name }) })).json();
  const auth = { Authorization: 'Bearer ' + r.hostToken };
  const sounds = files.map(f => { const buf = fs.readFileSync(f); return { id: sha(buf), name: path.basename(f), size: buf.length, buf }; });
  const set = await (await fetch(`${base}/api/lobbies/${r.code}/sounds`, { method: 'PUT', headers: auth,
    body: JSON.stringify({ sounds: sounds.map(({ buf, ...s }) => s) }) })).json();
  return { ...r, auth, sounds, set };
}

const audioIn = dir => fs.readdirSync(dir).filter(f => /\.(mp3|wav|m4a|ogg|flac|aiff?|caf)$/i.test(f)).map(f => path.join(dir, f));

if (mode === 'check') {
  const files = audioIn('../windows/testdata').slice(0, 2);
  const L = await openLobby('Daniel', files);
  ok(/^[A-Z2-9]{5}$/.test(L.code), 'Lobby offen, Code ' + L.code + ' (5 Stellen)');
  ok(L.set.missing.length === 0, 'Server will keine Dateien haben (missing ist leer)');
  const oldUpload = await fetch(`${base}/api/lobbies/${L.code}/blobs/${L.sounds[0].id}`, { method: 'PUT', headers: L.auth, body: L.sounds[0].buf });
  ok(oldUpload.status === 410, 'Hochladen wie in alten Versionen wird abgelehnt (410)');

  const H = client(L.code, 'Daniel', L.hostToken);
  const hh = await H.next('hello');
  ok(hh.isHost && hh.sounds.length === 0, 'Host verbunden – Liste leer, solange er nichts gemeldet hat');
  H.offerFiles(L.sounds);
  ok((await H.next('sounds')).sounds.length === 2, 'Host meldet seine 2 Dateien → beide in der Liste');
  const G = client(L.code.toLowerCase(), 'Lukas');
  const gh = await G.next('hello');
  ok(!gh.isHost && gh.host === 'Daniel' && gh.sounds.map(s => s.id).join() === L.sounds.map(s => s.id).join(), 'Gast (Code klein geschrieben) sieht die Host-Sounds');
  const blob = Buffer.from(await (await fetch(`${base}/api/lobbies/${L.code}/blobs/${gh.sounds[0].id}`)).arrayBuffer());
  ok(sha(blob) === gh.sounds[0].id && H.served === 1, 'Gast lädt die Datei direkt vom Host-Gerät, Prüfsumme stimmt');
  H.delay = 300; // damit sich die beiden Anfragen sicher überschneiden
  const [b1, b2] = await Promise.all([1, 2].map(async () => Buffer.from(await (await fetch(`${base}/api/lobbies/${L.code}/blobs/${gh.sounds[1].id}`)).arrayBuffer())));
  ok(sha(b1) === gh.sounds[1].id && sha(b2) === gh.sounds[1].id && H.served === 2, 'Zwei gleichzeitige Downloads → Host schickt nur einmal');
  H.delay = 0;
  const ranged = await fetch(`${base}/api/lobbies/${L.code}/blobs/${gh.sounds[0].id}`, { headers: { Range: 'bytes=0-99' } });
  ok(ranged.status === 206 && (await ranged.arrayBuffer()).byteLength === 100, 'Range-Anfrage liefert 206 mit 100 Bytes');
  ok(!fs.existsSync(path.join(process.env.DATA_DIR || 'data', 'blobs')), 'Auf dem Server liegt kein Datei-Ordner');

  // Gast hat die Datei jetzt auch → wenn der Host absagt, springt der Gast ein
  G.offerFiles([{ id: gh.sounds[0].id, buf: blob }]);
  await new Promise(r => setTimeout(r, 400));
  const hostFiles = H.files; H.files = new Map();
  const viaGuest = await fetch(`${base}/api/lobbies/${L.code}/blobs/${gh.sounds[0].id}`);
  ok(viaGuest.ok && G.served >= 1, 'Host sagt ab (nosend) → ein anderer Gast mit der Datei schickt sie');
  H.files = hostFiles;
  // Gerät schickt Müll → wird abgewiesen
  H.corrupt = true;
  const before = H.served;
  const bad = await fetch(`${base}/api/lobbies/${L.code}/blobs/${gh.sounds[1].id}`);
  ok(bad.status === 404 && H.served === before + 1, 'Falsche Datei vom Gerät wird verworfen (Prüfsumme), nichts kommt beim Gast an');
  H.corrupt = false;
  H.offerFiles(L.sounds); // nach dem Fehler wieder melden
  await new Promise(r => setTimeout(r, 400));

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
  H.on('open', () => H.offerFiles(L.sounds));
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

if (mode === 'clan-check') {
  const pg = (await import('pg')).default;
  const db = new pg.Pool({ connectionString: process.env.DATABASE_URL });
  const api = async (p, method = 'GET', body, token) => {
    const r = await fetch(`${base}/api/${p}`, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) },
      body: body ? JSON.stringify(body) : undefined });
    return { ...(await r.json().catch(() => ({}))), http: r.status };
  };
  const stamp = Date.now().toString(36);
  async function account(name) {
    const email = `${name.toLowerCase()}-${stamp}@test.invalid`;
    ok((await api('account/register', 'POST', { email, name, password: 'geheim123' })).ok, `${name} registriert`);
    const { code } = (await db.query('SELECT code FROM email_codes WHERE email = $1', [email])).rows[0];
    const v = await api('account/verify', 'POST', { email, code });
    ok(!!v.token, `${name} bestätigt und angemeldet`);
    return { email, token: v.token, id: v.user.id };
  }
  const device = async (who, deviceId) => {
    const D = client(null);
    await D.opened;
    D.sendJ({ type: 'auth', token: who.token, device: deviceId });
    await D.next('ready');
    return D;
  };
  const files = audioIn('../windows/testdata').slice(0, 2).map(f => { const buf = fs.readFileSync(f); return { id: sha(buf), name: path.basename(f), size: buf.length, buf }; });

  const A = await account('Anna'), B = await account('Ben');
  const created = await api('clans', 'POST', { name: 'Testclan' }, A.token);
  const clanId = created.clan.id;
  const invite = created.clan.inviteCode;
  const info = await api(`clans/invite/${invite}`);
  ok(info.clan?.name === 'Testclan' && info.clan.members === 1, 'Einladung ist ohne Konto lesbar (Name, 1 Mitglied)');
  ok((await fetch(`${base}/c/${invite}`)).status === 200, 'Einladungsseite …/c/CODE antwortet');
  ok((await api('clans/invite/ZZZZZZZZ')).http === 404, 'Falscher Einladungscode → 404');
  const joined = await api('clans/join', 'POST', { code: `${base}/c/${invite.toLowerCase()}` }, B.token);
  ok(joined.status === 'member' && joined.clanName === 'Testclan', 'Ben tritt mit dem Link bei und ist sofort drin (wie Discord)');
  const view0 = await api(`clans/${clanId}`, 'GET', null, B.token);
  ok(view0.clan.role === 'member' && view0.clan.inviteCode === invite && view0.requests.length === 0, 'Ben ist Mitglied und sieht den Invite-Code zum Weitergeben');
  ok((await api(`clans/${clanId}/approve`, 'POST', { userId: B.id, ok: true }, A.token)).http === 404, 'Freigeben gibt es nicht mehr');

  const DA = await device(A, 'annas-mac');
  ok((await DA.next('want')).sounds.length === 0, 'Annas Gerät verbunden, noch nichts zu suchen');
  await new Promise(r => setTimeout(r, 200));
  const view1 = await api(`clans/${clanId}`, 'GET', null, B.token);
  ok(view1.members.find(m => m.id === A.id)?.online === true && view1.members.find(m => m.id === B.id)?.online === false,
    'Mitgliederliste zeigt: Anna online, Ben offline');
  const DA2 = await device(A, 'annas-mac');
  ok((await DA.closed).code === 4001, 'Gleiches Gerät verbindet sich neu → alte (verwaiste) Verbindung wird ersetzt');
  DA2.close();
  const DA3 = await device(A, 'annas-mac');
  await DA3.next('want');

  const bad = client(null); await bad.opened; bad.sendJ({ type: 'auth', token: 'falsch' });
  ok((await bad.closed).code === 4401, 'Geräte-Verbindung mit falschem Token wird abgewiesen');

  const add = await api(`clans/${clanId}/sounds`, 'POST', { sounds: files.map(({ buf, ...s }) => s) }, A.token);
  ok(add.added === 2 && add.missing.length === 0, 'Anna teilt 2 Sounds – Server will keine Datei');
  ok((await DA3.next('clan')).clanId === clanId, 'Geräte bekommen ein Live-Update „Clan geändert“');
  const want = await DA3.next('want');
  ok(want.sounds.length === 2, 'Annas Gerät soll nach den 2 Sounds suchen');
  let list = await api(`clans/${clanId}/sounds`, 'GET', null, B.token);
  ok(list.sounds.every(s => !s.available), 'Bevor das Gerät sie meldet: nicht verfügbar');
  DA3.offerFiles(files);
  await new Promise(r => setTimeout(r, 300));
  list = await api(`clans/${clanId}/sounds`, 'GET', null, B.token);
  ok(list.sounds.length === 2 && list.sounds.every(s => s.available && s.by === 'Anna'), 'Ben sieht beide als verfügbar, „von Anna“');
  const got = await fetch(`${base}/api/clans/${clanId}/blobs/${files[0].id}`, { headers: { Authorization: 'Bearer ' + B.token } });
  ok(got.ok && sha(Buffer.from(await got.arrayBuffer())) === files[0].id, 'Ben holt einen Clan-Sound direkt von Annas Gerät');
  const stranger = await account('Fremd');
  ok((await fetch(`${base}/api/clans/${clanId}/blobs/${files[0].id}`, { headers: { Authorization: 'Bearer ' + stranger.token } })).status === 403, 'Wer nicht im Clan ist, bekommt nichts');
  const oldPut = await fetch(`${base}/api/clans/${clanId}/blobs/${files[0].id}`, { method: 'PUT', headers: { Authorization: 'Bearer ' + A.token }, body: files[0].buf });
  ok(oldPut.status === 410, 'Clan-Upload wie in alten Versionen wird abgelehnt (410)');

  ok((await api(`clans/${clanId}/sounds/${files[1].id}`, 'DELETE', null, B.token)).http === 403, 'Ben (Mitglied) darf nicht löschen');
  ok((await api(`clans/${clanId}/sounds/${files[1].id}`, 'DELETE', null, A.token)).ok, 'Anna (Admin) löscht einen Sound');
  ok((await DA3.next('want')).sounds.length === 1, 'Geräte bekommen die neue Liste');
  ok((await api(`clans/${clanId}/sounds`, 'GET', null, B.token)).sounds.length === 1, 'Im Clan ist nur noch 1 Sound');

  // Clan-Session
  const sA = await api(`clans/${clanId}/session`, 'POST', null, A.token);
  const sB = await api(`clans/${clanId}/session`, 'POST', null, B.token);
  ok(sA.code && sA.code === sB.code && sA.ticket !== sB.ticket && !sA.hostToken, 'Beide bekommen dieselbe Session mit eigenem Ticket, ohne Host-Token');
  const noTicket = client(sA.code, 'X');
  ok((await noTicket.closed).code === 4401, 'Ohne Ticket kommt niemand in die Clan-Session');
  const forged = client(sA.code, 'X', null, '&ticket=' + B.id + '.' + sA.ticket.split('.')[1]);
  ok((await forged.closed).code === 4401, 'Ticket von Anna mit Bens Konto-ID wird abgewiesen');
  const SA = client(sA.code, 'egal', null, '&ticket=' + sA.ticket);
  const helloA = await SA.next('hello');
  ok(!helloA.isHost && helloA.host === 'Testclan' && helloA.sounds.length === 1, 'Anna in der Session: kein Host, sieht den Clan-Sound');
  const SB = client(sB.code, 'egal', null, '&ticket=' + sB.ticket);
  const helloB = await SB.next('hello');
  ok(helloB.members.some(m => m.name === 'Ben') && helloB.members.every(m => !m.host), 'Ben drin, Name kommt aus dem Konto');
  const sessBlob = await fetch(`${base}/api/lobbies/${sB.code}/blobs/${files[0].id}`);
  ok(sessBlob.ok && sha(Buffer.from(await sessBlob.arrayBuffer())) === files[0].id, 'Session-Sound kommt von Annas Gerät');
  SB.sendJ({ type: 'ask', id: files[0].id });
  ok((await SB.next('answered')).ok, 'Behalten in der Clan-Session geht ohne Nachfrage');
  await api(`clans/${clanId}/sounds`, 'POST', { sounds: [files[1]].map(({ buf, ...s }) => s) }, B.token);
  let ss;
  do { ss = await SB.next('sounds'); } while (ss.sounds.length !== 2);
  ok(true, 'Neuer Clan-Sound taucht in der laufenden Session auf');

  DA3.close();
  do { ss = await SB.next('sounds'); } while (ss.sounds.length !== 0);
  ok(true, 'Annas Gerät geht offline → Sounds verschwinden aus der Session');
  const offline = await fetch(`${base}/api/clans/${clanId}/blobs/${files[0].id}`, { headers: { Authorization: 'Bearer ' + B.token } });
  ok(offline.status === 404 && /niemand online/.test((await offline.json()).error), 'Clan-Sound holen → „niemand online“');

  // Gerät, das nicht mehr antwortet, wird nach 8 s gekappt und nicht wieder gefragt
  const DB = await device(B, 'bens-pc');
  await DB.next('want');
  DB.silent = true;
  DB.offerFiles([files[0]]);
  await new Promise(r => setTimeout(r, 300));
  const t0 = Date.now();
  const stuck = await fetch(`${base}/api/clans/${clanId}/blobs/${files[0].id}`, { headers: { Authorization: 'Bearer ' + A.token } });
  const gone = await DB.closed;
  ok(stuck.status === 404 && Date.now() - t0 < 12000 && gone.code === 1006, 'Gerät antwortet nicht → nach ~8 s gekappt, Anfrage endet sauber');
  ok(!(await api(`clans/${clanId}/sounds`, 'GET', null, A.token)).sounds.some(s => s.available), 'Danach gilt der Sound wieder als nicht verfügbar');

  // Entfernen (Admin), Wiederkommen mit dem Link, Besitzer ist geschützt
  ok((await api(`clans/${clanId}/kick`, 'POST', { userId: A.id }, B.token)).http === 403, 'Mitglied kann niemanden entfernen');
  const DBen = await device(B, 'bens-pc');
  await DBen.next('want');
  ok((await api(`clans/${clanId}/kick`, 'POST', { userId: B.id }, A.token)).ok, 'Anna (Admin) entfernt Ben');
  let ev; do { ev = await DBen.next('clan'); } while (ev.clanId !== clanId);
  ok((await SB.closed).code === 4410, 'Bens Session-Verbindung wird beendet, sein Gerät bekommt ein Update');
  ok((await api(`clans/${clanId}`, 'GET', null, B.token)).http === 403, 'Ben sieht den Clan nicht mehr');
  ok((await api('clans/join', 'POST', { code: invite }, B.token)).status === 'member', 'Mit dem Code ist Ben wieder drin');
  const rotated = await api(`clans/${clanId}/invite`, 'POST', null, A.token);
  ok(rotated.inviteCode && rotated.inviteCode !== invite && (await api('clans/join', 'POST', { code: invite }, A.token)).http === 404,
    'Neuer Invite-Code → der alte gilt nicht mehr');
  DBen.close();

  // Konto löschen
  ok((await api('account/delete', 'POST', { password: 'falsch' }, A.token)).http === 403, 'Löschen mit falschem Passwort geht nicht');
  ok((await api('account/delete', 'POST', { password: 'geheim123' }, A.token)).ok, 'Anna löscht ihr Konto');
  ok((await SA.closed).code === 4410, 'Annas Session-Verbindung wird beendet');
  ok((await api('account/me', 'GET', null, A.token)).http === 401, 'Annas Token gilt nicht mehr');
  const left = (await db.query('SELECT count(*)::int AS n FROM users WHERE email = $1', [A.email])).rows[0].n;
  ok(left === 0, 'Anna ist aus der Datenbank verschwunden');
  const view = await api(`clans/${clanId}`, 'GET', null, B.token);
  ok(view.clan.owner && view.clan.role === 'admin', 'Clan gehört jetzt Ben (Admin)');
  ok((await db.query('SELECT count(*)::int AS n FROM clan_sounds WHERE added_by = $1', [A.id])).rows[0].n === 0, 'Annas Name hängt an keinem Clan-Sound mehr');
  ok((await api('account/delete', 'POST', { password: 'geheim123' }, B.token)).ok, 'Ben löscht sein Konto auch');
  ok((await db.query('SELECT count(*)::int AS n FROM clans WHERE id = $1', [clanId])).rows[0].n === 0, 'Clan ohne Mitglieder ist aufgelöst');

  // Besitzer löst einen Clan auf
  const C1 = await account('Cleo'), C2 = await account('Cem');
  const c2 = await api('clans', 'POST', { name: 'Kurzclan' }, C1.token);
  await api('clans/join', 'POST', { code: c2.clan.inviteCode }, C2.token);
  ok((await api(`clans/${c2.clan.id}/delete`, 'POST', null, C2.token)).http === 403, 'Nur der Besitzer kann auflösen');
  ok((await api(`clans/${c2.clan.id}/delete`, 'POST', null, C1.token)).ok, 'Besitzer löst den Clan auf');
  ok((await api('account/me', 'GET', null, C2.token)).clans.length === 0, 'Bei den anderen ist der Clan weg');
  for (const who of [C1, C2]) await api('account/delete', 'POST', { password: 'geheim123' }, who.token);

  // Abmelden macht den Token ungültig
  await api('account/logout', 'POST', null, stranger.token);
  ok((await api('account/me', 'GET', null, stranger.token)).http === 401, 'Nach dem Abmelden gilt der Token nicht mehr');
  await api('account/login', 'POST', { email: stranger.email, password: 'geheim123' }).then(r => api('account/delete', 'POST', { password: 'geheim123' }, r.token));
  await db.end();
  process.exit(0);
}
