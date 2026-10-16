// ClipSound Accounts & Clans.
// Baut auf demselben Postgres wie die Lobbys auf (additive Tabellen – bestehende Lobbys bleiben).
// Accounts: E-Mail + Passwort, Bestätigung per 6-stelligem Code (Resend).
// Clans: wie ein Discord-Server – wer den Invite-Code (oder Link …/c/CODE) hat, ist sofort drin.
// Admins können einen neuen Code erzeugen (der alte gilt dann nicht mehr) und Leute entfernen.
// Clan-Sounds und Clan-Sessions stehen in server.js (sie brauchen die Geräte-Verbindungen).
//
// HTTP (alles JSON)
//   POST /api/account/register  {email, name, password}      → {ok}                 (schickt Code per Mail)
//   POST /api/account/verify    {email, code}                → {token, user}
//   POST /api/account/resend    {email}                      → {ok}
//   POST /api/account/login     {email, password}            → {token, user}
//   POST /api/account/logout    (Bearer)                     → {ok}                 (Token wird ungültig)
//   POST /api/account/delete    (Bearer) {password}          → {ok}                 (Konto und alle Daten löschen)
//   GET  /api/account/me        (Bearer)                     → {user, clans}
//   GET  /api/clans             (Bearer)                     → {clans:[{id,name,role,members}]}
//   POST /api/clans             (Bearer) {name}              → {clan}               (Ersteller = Admin)
//   GET  /api/clans/:id         (Mitglied)                   → {clan:{…, inviteCode}, members:[{id,name,role,online,inSession}]}
//   POST /api/clans/:id/invite  (Admin)                      → {inviteCode}         (neuen Code erzeugen, alter gilt nicht mehr)
//   POST /api/clans/join        (Bearer) {code}              → {status:'member', clanId, clanName}   (code darf auch der Link sein)
//   GET  /api/clans/invite/:code (ohne Konto)                → {clan:{name, members}}  (für die Einladungsseite)
//   POST /api/clans/:id/kick    (Admin) {userId}             → {ok}                 (Besitzer kann niemand entfernen)
//   POST /api/clans/:id/leave   (Mitglied)                   → {ok}
//   POST /api/clans/:id/delete  (Besitzer)                   → {ok}                 (Clan auflösen)
//   Alt (für ältere Apps): GET …/requests → {requests:[]}, POST …/approve → 404
const crypto = require('crypto');

const CODE_TTL = 15 * 60 * 1000;                  // Mail-Code gilt 15 Minuten
const INVITE_CHARS = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
const uid = () => crypto.randomBytes(16).toString('hex');
const newInvite = () => [...crypto.randomBytes(8)].map(b => INVITE_CHARS[b % INVITE_CHARS.length]).join('');
const sixDigits = () => String(crypto.randomInt(0, 1_000_000)).padStart(6, '0');
const hashToken = t => crypto.createHash('sha256').update(String(t || '')).digest('hex');
const newToken = () => crypto.randomBytes(24).toString('base64url');

const normEmail = e => String(e || '').trim().toLowerCase().slice(0, 160);
const okEmail = e => /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(e);
const cleanName = n => String(n || '').replace(/[^\p{L}\p{N} _\-.]/gu, '').trim().slice(0, 24);
const cleanClan = n => String(n || '').replace(/[^\p{L}\p{N} _\-.!?']/gu, '').trim().slice(0, 32);
// Akzeptiert den Code oder einen Link wie https://…/c/ABCD2345
const normInvite = c => String(c || '').trim().split(/[/?#=]/).filter(Boolean).pop()?.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8) || '';

function hashPassword(pw) {
  const salt = crypto.randomBytes(16);
  const dk = crypto.scryptSync(String(pw), salt, 32);
  return `s1$${salt.toString('hex')}$${dk.toString('hex')}`;
}
function verifyPassword(pw, stored) {
  const [v, saltHex, dkHex] = String(stored).split('$');
  if (v !== 's1' || !saltHex || !dkHex) return false;
  const dk = crypto.scryptSync(String(pw), Buffer.from(saltHex, 'hex'), 32);
  const want = Buffer.from(dkHex, 'hex');
  return want.length === dk.length && crypto.timingSafeEqual(want, dk);
}

async function sendCode(email, code) {
  const key = process.env.RESEND_API_KEY;
  const from = process.env.MAIL_FROM || 'ClipSound <onboarding@resend.dev>';
  if (!key) { console.log(`[DEV] ClipSound-Code für ${email}: ${code}`); return true; }
  try {
    const r = await fetch('https://api.resend.com/emails', {
      method: 'POST',
      headers: { Authorization: `Bearer ${key}`, 'Content-Type': 'application/json' },
      body: JSON.stringify({
        from, to: email, subject: `ClipSound-Code: ${code}`,
        text: `Dein Bestätigungscode für ClipSound: ${code}\n\nDer Code gilt 15 Minuten. Wenn du das nicht warst, ignoriere diese Mail.`,
      }),
    });
    if (!r.ok) console.error('Resend-Fehler', r.status, await r.text().catch(() => ''));
    return r.ok;
  } catch (e) { console.error('Mail-Versand fehlgeschlagen', e); return false; }
}

async function migrate(db) {
  await db.query(`
    CREATE TABLE IF NOT EXISTS users (
      id         text PRIMARY KEY,
      email      text UNIQUE NOT NULL,
      name       text NOT NULL,
      pass_hash  text NOT NULL,
      verified   boolean NOT NULL DEFAULT false,
      created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE TABLE IF NOT EXISTS email_codes (
      email      text PRIMARY KEY,
      code       text NOT NULL,
      expires_at timestamptz NOT NULL
    );
    CREATE TABLE IF NOT EXISTS account_tokens (
      token_hash text PRIMARY KEY,
      user_id    text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE TABLE IF NOT EXISTS clans (
      id          text PRIMARY KEY,
      name        text NOT NULL,
      invite_code text UNIQUE NOT NULL,
      owner_id    text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      created_at  timestamptz NOT NULL DEFAULT now()
    );
    CREATE TABLE IF NOT EXISTS clan_members (
      clan_id   text NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
      user_id   text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      role      text NOT NULL DEFAULT 'member',
      joined_at timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (clan_id, user_id)
    );
    CREATE TABLE IF NOT EXISTS clan_requests (
      clan_id    text NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
      user_id    text NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      created_at timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (clan_id, user_id)
    );
    CREATE TABLE IF NOT EXISTS clan_sounds (
      clan_id text NOT NULL REFERENCES clans(id) ON DELETE CASCADE,
      id      text NOT NULL,
      name    text NOT NULL,
      size    integer NOT NULL,
      pos     integer NOT NULL,
      added_by text,
      PRIMARY KEY (clan_id, id)
    );
    ALTER TABLE clan_sounds ADD COLUMN IF NOT EXISTS added_by text;
    -- Seit 2.7 gibt es keine Beitritts-Anfragen mehr: wer schon angefragt hatte, ist jetzt Mitglied
    INSERT INTO clan_members (clan_id, user_id) SELECT clan_id, user_id FROM clan_requests ON CONFLICT DO NOTHING;
    DELETE FROM clan_requests;
    CREATE TABLE IF NOT EXISTS clan_sessions (
      clan_id    text PRIMARY KEY REFERENCES clans(id) ON DELETE CASCADE,
      code       text NOT NULL,
      created_at timestamptz NOT NULL DEFAULT now()
    );
  `);
}

/** Nutzer zu einem Anmelde-Token, oder null. */
async function userFromToken(db, token) {
  if (typeof token !== 'string' || !token) return null;
  const { rows } = await db.query(
    'SELECT u.id, u.email, u.name, u.verified FROM account_tokens t JOIN users u ON u.id = t.user_id WHERE t.token_hash = $1',
    [hashToken(token)]);
  return rows[0] || null;
}

/** Nutzer aus dem Bearer-Token, oder null. */
async function authUser(db, req) {
  const m = /^Bearer (.+)$/.exec(req.headers.authorization || '');
  return m ? userFromToken(db, m[1]) : null;
}

/**
 * Konto mit allem löschen. Eigene Clans gehen an ein anderes Mitglied (Admins zuerst, sonst wer am
 * längsten drin ist); ohne weitere Mitglieder wird der Clan aufgelöst. Gibt die aufgelösten Clans zurück.
 */
async function deleteAccount(db, user) {
  const client = await db.connect();
  const gone = [];
  try {
    await client.query('BEGIN');
    const owned = (await client.query('SELECT id FROM clans WHERE owner_id = $1', [user.id])).rows;
    for (const { id } of owned) {
      const next = (await client.query(
        "SELECT user_id FROM clan_members WHERE clan_id = $1 AND user_id <> $2 ORDER BY (role = 'admin') DESC, joined_at LIMIT 1",
        [id, user.id])).rows[0];
      if (next) {
        await client.query('UPDATE clans SET owner_id = $2 WHERE id = $1', [id, next.user_id]);
        await client.query("UPDATE clan_members SET role = 'admin' WHERE clan_id = $1 AND user_id = $2", [id, next.user_id]);
      } else {
        await client.query('DELETE FROM clans WHERE id = $1', [id]);
        gone.push(id);
      }
    }
    await client.query('UPDATE clan_sounds SET added_by = NULL WHERE added_by = $1', [user.id]);
    await client.query('DELETE FROM email_codes WHERE email = $1', [user.email]);
    await client.query('DELETE FROM users WHERE id = $1', [user.id]); // Tokens, Mitgliedschaften, Anfragen hängen dran
    await client.query('COMMIT');
  } catch (e) {
    await client.query('ROLLBACK'); throw e;
  } finally { client.release(); }
  return gone;
}

/** Rolle im Clan ('admin'|'member') oder null, wenn nicht Mitglied. */
async function memberRole(db, clanId, userId) {
  const { rows } = await db.query('SELECT role FROM clan_members WHERE clan_id = $1 AND user_id = $2', [clanId, userId]);
  return rows[0]?.role || null;
}

/**
 * true = Request wurde hier beantwortet. ctx: {db, readBody, json, hooks}
 * hooks (aus server.js): membershipChanged(userId), userGone(userId), clanGone(clanId), clanChanged(clanId),
 *                        presence(clanId) → {online:Set<userId>, inSession:Set<userId>}
 */
async function handle(req, res, ctx) {
  const { db, readBody, json } = ctx;
  const hooks = ctx.hooks || {};
  const later = (fn, ...args) => fn ? Promise.resolve(fn(...args)).catch(e => console.error('Clan-Hook', e)) : Promise.resolve();
  const url = new URL(req.url, 'http://x');
  const p = decodeURIComponent(url.pathname);
  if (!p.startsWith('/api/account') && !p.startsWith('/api/clans')) return false;

  const body = async () => { try { return JSON.parse(String(await readBody(req, 64 * 1024)) || '{}'); } catch { return {}; } };
  const user = () => authUser(db, req);
  const role = (clanId, userId) => memberRole(db, clanId, userId);
  const clanView = async (clanId) => {
    const { rows } = await db.query('SELECT id, name, invite_code, owner_id FROM clans WHERE id = $1', [clanId]);
    return rows[0] || null;
  };

  try {
    // ---------- Account ----------
    if (p === '/api/account/register' && req.method === 'POST') {
      const b = await body();
      const email = normEmail(b.email), name = cleanName(b.name), pw = String(b.password || '');
      if (!okEmail(email)) return json(res, 400, { error: 'Bitte eine gültige E-Mail angeben' });
      if (name.length < 2) return json(res, 400, { error: 'Bitte einen Namen angeben' });
      if (pw.length < 6) return json(res, 400, { error: 'Passwort braucht mindestens 6 Zeichen' });
      const { rows } = await db.query('SELECT id, verified FROM users WHERE email = $1', [email]);
      if (rows[0]?.verified) return json(res, 409, { error: 'Diese E-Mail hat schon ein Konto' });
      const hash = hashPassword(pw);
      if (rows[0]) await db.query('UPDATE users SET name = $2, pass_hash = $3 WHERE id = $1', [rows[0].id, name, hash]);
      else await db.query('INSERT INTO users (id, email, name, pass_hash) VALUES ($1, $2, $3, $4)', [uid(), email, name, hash]);
      const code = sixDigits();
      await db.query(
        'INSERT INTO email_codes (email, code, expires_at) VALUES ($1, $2, now() + interval \'15 minutes\') ' +
        'ON CONFLICT (email) DO UPDATE SET code = $2, expires_at = now() + interval \'15 minutes\'',
        [email, code]);
      await sendCode(email, code);
      return json(res, 200, { ok: true });
    }

    if (p === '/api/account/resend' && req.method === 'POST') {
      const email = normEmail((await body()).email);
      const { rows } = await db.query('SELECT verified FROM users WHERE email = $1', [email]);
      if (!rows[0] || rows[0].verified) return json(res, 200, { ok: true }); // nichts verraten
      const code = sixDigits();
      await db.query(
        'INSERT INTO email_codes (email, code, expires_at) VALUES ($1, $2, now() + interval \'15 minutes\') ' +
        'ON CONFLICT (email) DO UPDATE SET code = $2, expires_at = now() + interval \'15 minutes\'', [email, code]);
      await sendCode(email, code);
      return json(res, 200, { ok: true });
    }

    if (p === '/api/account/verify' && req.method === 'POST') {
      const b = await body();
      const email = normEmail(b.email), code = String(b.code || '').replace(/\D/g, '').slice(0, 6);
      const { rows } = await db.query('SELECT code, expires_at FROM email_codes WHERE email = $1', [email]);
      if (!rows[0] || rows[0].code !== code || new Date(rows[0].expires_at) < new Date())
        return json(res, 400, { error: 'Code falsch oder abgelaufen' });
      await db.query('UPDATE users SET verified = true WHERE email = $1', [email]);
      await db.query('DELETE FROM email_codes WHERE email = $1', [email]);
      const { rows: u } = await db.query('SELECT id, email, name FROM users WHERE email = $1', [email]);
      const token = newToken();
      await db.query('INSERT INTO account_tokens (token_hash, user_id) VALUES ($1, $2)', [hashToken(token), u[0].id]);
      return json(res, 200, { token, user: u[0] });
    }

    if (p === '/api/account/login' && req.method === 'POST') {
      const b = await body();
      const email = normEmail(b.email);
      const { rows } = await db.query('SELECT id, email, name, pass_hash, verified FROM users WHERE email = $1', [email]);
      const u = rows[0];
      if (!u || !verifyPassword(b.password, u.pass_hash)) return json(res, 401, { error: 'E-Mail oder Passwort stimmt nicht' });
      if (!u.verified) return json(res, 403, { error: 'Bitte zuerst die E-Mail bestätigen', needsVerify: true });
      const token = newToken();
      await db.query('INSERT INTO account_tokens (token_hash, user_id) VALUES ($1, $2)', [hashToken(token), u.id]);
      return json(res, 200, { token, user: { id: u.id, email: u.email, name: u.name } });
    }

    // Einladung ansehen (ohne Konto, für die Seite …/c/CODE)
    let im;
    if ((im = /^\/api\/clans\/invite\/([^/]+)$/.exec(p)) && req.method === 'GET') {
      const c = (await db.query(
        'SELECT c.name, (SELECT count(*)::int FROM clan_members m WHERE m.clan_id = c.id) AS members FROM clans c WHERE c.invite_code = $1',
        [normInvite(im[1])])).rows[0];
      if (!c) return json(res, 404, { error: 'Diese Einladung gilt nicht (mehr)' });
      return json(res, 200, { clan: c });
    }

    // ab hier: eingeloggt
    const me = await user();
    if (p === '/api/account/me' && req.method === 'GET') {
      if (!me) return json(res, 401, { error: 'Nicht eingeloggt' });
      return json(res, 200, { user: { id: me.id, email: me.email, name: me.name }, clans: await myClans(db, me.id) });
    }

    if (p === '/api/account/logout' && req.method === 'POST') {
      const m = /^Bearer (.+)$/.exec(req.headers.authorization || '');
      if (m) await db.query('DELETE FROM account_tokens WHERE token_hash = $1', [hashToken(m[1])]);
      return json(res, 200, { ok: true });
    }

    if (p === '/api/account/delete' && req.method === 'POST') {
      if (!me) return json(res, 401, { error: 'Nicht eingeloggt' });
      const b = await body();
      const { rows } = await db.query('SELECT pass_hash FROM users WHERE id = $1', [me.id]);
      if (!rows[0] || !verifyPassword(b.password, rows[0].pass_hash)) return json(res, 403, { error: 'Passwort stimmt nicht' });
      const gone = await deleteAccount(db, me);
      later(hooks.userGone, me.id);
      for (const clanId of gone) later(hooks.clanGone, clanId);
      console.log('Konto gelöscht');
      return json(res, 200, { ok: true });
    }

    // ---------- Clans ----------
    if (p === '/api/clans' && req.method === 'GET') {
      if (!me) return json(res, 401, { error: 'Nicht eingeloggt' });
      return json(res, 200, { clans: await myClans(db, me.id) });
    }

    if (p === '/api/clans' && req.method === 'POST') {
      if (!me) return json(res, 401, { error: 'Nicht eingeloggt' });
      const name = cleanClan((await body()).name);
      if (name.length < 2) return json(res, 400, { error: 'Bitte einen Clan-Namen angeben' });
      const id = uid();
      let invite = '';
      for (let i = 0; i < 10; i++) {
        invite = newInvite();
        const r = await db.query(
          'INSERT INTO clans (id, name, invite_code, owner_id) VALUES ($1, $2, $3, $4) ON CONFLICT (invite_code) DO NOTHING',
          [id, name, invite, me.id]);
        if (r.rowCount) break;
        invite = '';
      }
      if (!invite) return json(res, 503, { error: 'Kein Code frei, bitte nochmal' });
      await db.query('INSERT INTO clan_members (clan_id, user_id, role) VALUES ($1, $2, \'admin\')', [id, me.id]);
      later(hooks.membershipChanged, me.id);
      return json(res, 200, { clan: { id, name, inviteCode: invite, role: 'admin', members: 1 } });
    }

    if (p === '/api/clans/join' && req.method === 'POST') {
      if (!me) return json(res, 401, { error: 'Nicht eingeloggt' });
      const code = normInvite((await body()).code);
      const c = (await db.query('SELECT id, name FROM clans WHERE invite_code = $1', [code])).rows[0];
      if (!c) return json(res, 404, { error: 'Diese Einladung gilt nicht (mehr)' });
      if (!await role(c.id, me.id)) {
        await db.query('INSERT INTO clan_members (clan_id, user_id) VALUES ($1, $2) ON CONFLICT DO NOTHING', [c.id, me.id]);
        later(hooks.membershipChanged, me.id);
        later(hooks.clanChanged, c.id);
      }
      return json(res, 200, { status: 'member', clanId: c.id, clanName: c.name });
    }

    let m;
    if ((m = /^\/api\/clans\/([0-9a-f]{32})(\/[a-z]+)?$/.exec(p))) {
      if (!me) return json(res, 401, { error: 'Nicht eingeloggt' });
      const clanId = m[1], sub = m[2] || '';
      const clan = await clanView(clanId);
      if (!clan) return json(res, 404, { error: 'Clan gibt es nicht' });
      const myRole = await role(clanId, me.id);
      const isAdmin = myRole === 'admin';

      if (!sub && req.method === 'GET') {
        if (!myRole) return json(res, 403, { error: 'Du bist nicht in diesem Clan' });
        const presence = hooks.presence ? hooks.presence(clanId) : { online: new Set(), inSession: new Set() };
        const members = (await db.query(
          'SELECT u.id, u.name, m.role FROM clan_members m JOIN users u ON u.id = m.user_id WHERE m.clan_id = $1 ORDER BY m.role, u.name',
          [clanId])).rows.map(u => ({ ...u, owner: u.id === clan.owner_id, online: presence.online.has(u.id), inSession: presence.inSession.has(u.id) }));
        const out = { clan: { id: clan.id, name: clan.name, role: myRole, owner: clan.owner_id === me.id, inviteCode: clan.invite_code }, members, requests: [] };
        out.clan.session = (await db.query('SELECT code FROM clan_sessions WHERE clan_id = $1', [clanId])).rows[0]?.code || null;
        return json(res, 200, out);
      }

      if (sub === '/invite' && req.method === 'POST') {
        if (!isAdmin) return json(res, 403, { error: 'Nur Admins' });
        let invite = '';
        for (let i = 0; i < 10; i++) {
          invite = newInvite();
          const r = await db.query('UPDATE clans SET invite_code = $2 WHERE id = $1 AND NOT EXISTS (SELECT 1 FROM clans WHERE invite_code = $2)', [clanId, invite]);
          if (r.rowCount) break;
          invite = '';
        }
        if (!invite) return json(res, 503, { error: 'Kein Code frei' });
        later(hooks.clanChanged, clanId);
        return json(res, 200, { inviteCode: invite });
      }

      // Ältere Apps fragen noch nach Beitritts-Anfragen – die gibt es nicht mehr
      if (sub === '/requests' && req.method === 'GET') return json(res, 200, { requests: [] });
      if (sub === '/approve' && req.method === 'POST') return json(res, 404, { error: 'Beitritts-Anfragen gibt es nicht mehr – mit dem Invite-Code ist man direkt drin.' });

      if (sub === '/kick' && req.method === 'POST') {
        if (!isAdmin) return json(res, 403, { error: 'Nur Admins können Leute entfernen' });
        const userId = String((await body()).userId || '');
        if (userId === me.id) return json(res, 400, { error: 'Zum Gehen „Clan verlassen“ nehmen' });
        if (userId === clan.owner_id) return json(res, 403, { error: 'Den Besitzer kann niemand entfernen' });
        const r = await db.query('DELETE FROM clan_members WHERE clan_id = $1 AND user_id = $2', [clanId, userId]);
        if (!r.rowCount) return json(res, 404, { error: 'Die Person ist nicht im Clan' });
        later(hooks.membershipChanged, userId);
        later(hooks.clanChanged, clanId);
        return json(res, 200, { ok: true });
      }

      if (sub === '/leave' && req.method === 'POST') {
        if (!myRole) return json(res, 200, { ok: true });
        if (clan.owner_id === me.id) return json(res, 400, { error: 'Der Besitzer kann den Clan nicht verlassen, nur auflösen.' });
        await db.query('DELETE FROM clan_members WHERE clan_id = $1 AND user_id = $2', [clanId, me.id]);
        later(hooks.membershipChanged, me.id);
        later(hooks.clanChanged, clanId);
        return json(res, 200, { ok: true });
      }

      if (sub === '/delete' && req.method === 'POST') {
        if (clan.owner_id !== me.id) return json(res, 403, { error: 'Nur der Besitzer kann den Clan auflösen' });
        const people = (await db.query('SELECT user_id FROM clan_members WHERE clan_id = $1', [clanId])).rows.map(r => r.user_id);
        await later(hooks.clanGone, clanId); // Session schließen, solange sie noch bekannt ist
        await db.query('DELETE FROM clans WHERE id = $1', [clanId]); // Mitglieder, Sounds, Session hängen dran
        for (const u of people) later(hooks.membershipChanged, u);
        return json(res, 200, { ok: true });
      }
    }

    return json(res, 404, { error: 'Nicht gefunden' });
  } catch (e) {
    if (e && e.status) return json(res, e.status, { error: e.message });
    console.error('Clan-Fehler', e);
    return json(res, 500, { error: 'Serverfehler' });
  }
}

async function myClans(db, userId) {
  const { rows } = await db.query(
    `SELECT c.id, c.name, m.role,
            (SELECT count(*)::int FROM clan_members cm WHERE cm.clan_id = c.id) AS members
     FROM clan_members m JOIN clans c ON c.id = m.clan_id
     WHERE m.user_id = $1 ORDER BY c.name`, [userId]);
  return rows;
}

module.exports = { migrate, handle, authUser, userFromToken, memberRole };
