// Sign-in. Production: OIDC authorization code + PKCE against the authority the API advertises at
// /v1/auth/config (public client, no secret). Development: the API mints a token at /v1/auth/dev-login.
// The access token lives in sessionStorage only.
interface AuthConfig { mode: 'oidc' | 'dev'; authority?: string; clientId?: string; scopes?: string }

const TOKEN = 'axiom.token';
const EXPIRES = 'axiom.token.exp';
const PENDING = 'axiom.oidc.pending';

const store = {
  get: (k: string) => { try { return sessionStorage.getItem(k); } catch { return null; } },
  set: (k: string, v: string) => { try { sessionStorage.setItem(k, v); } catch { /* storage unavailable */ } },
  del: (k: string) => { try { sessionStorage.removeItem(k); } catch { /* storage unavailable */ } },
};

export const getToken = (): string => {
  const exp = Number(store.get(EXPIRES) ?? 0);
  if (exp && Date.now() > exp) { signOut(); return ''; }
  return store.get(TOKEN) ?? '';
};
export const isSignedIn = (): boolean => getToken() !== '';
export function signOut(): void { store.del(TOKEN); store.del(EXPIRES); }

function saveToken(token: string, expiresInSeconds?: number): void {
  store.set(TOKEN, token);
  if (expiresInSeconds) store.set(EXPIRES, String(Date.now() + expiresInSeconds * 1000));
}

export async function loadConfig(): Promise<AuthConfig> {
  const res = await fetch('/v1/auth/config');
  if (!res.ok) throw new Error('Could not load sign-in configuration.');
  return (await res.json()) as AuthConfig;
}

const b64url = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const random = (n = 32) => b64url(crypto.getRandomValues(new Uint8Array(n)));
const redirectUri = () => `${location.origin}${location.pathname}`;

async function discover(authority: string): Promise<{ authorization_endpoint: string; token_endpoint: string }> {
  const res = await fetch(`${authority.replace(/\/$/, '')}/.well-known/openid-configuration`);
  if (!res.ok) throw new Error('Could not reach the identity provider.');
  return res.json();
}

export async function signIn(config: AuthConfig, name?: string): Promise<void> {
  if (config.mode === 'dev') {
    const res = await fetch('/v1/auth/dev-login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(name ? { name } : {}) });
    if (!res.ok) throw new Error('Development sign-in is not available.');
    const body = await res.json() as { accessToken: string; expiresIn: number };
    saveToken(body.accessToken, body.expiresIn);
    return;
  }
  if (!config.authority || !config.clientId) throw new Error('Sign-in is not configured (Axiom:Auth:Authority / PortalClientId).');
  const meta = await discover(config.authority);
  const verifier = random(48);
  const state = random();
  const challenge = b64url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
  store.set(PENDING, JSON.stringify({ verifier, state, authority: config.authority, clientId: config.clientId }));
  const url = new URL(meta.authorization_endpoint);
  url.search = new URLSearchParams({
    response_type: 'code', client_id: config.clientId, redirect_uri: redirectUri(), scope: config.scopes ?? 'openid',
    state, code_challenge: challenge, code_challenge_method: 'S256',
  }).toString();
  location.assign(url.toString());
}

/** Completes the redirect back from the IdP, if this page load is one. Returns true when a token was stored. */
export async function completeSignIn(): Promise<boolean> {
  const params = new URLSearchParams(location.search);
  const code = params.get('code');
  if (!code) return false;
  const pendingRaw = store.get(PENDING);
  store.del(PENDING);
  history.replaceState(null, '', location.pathname);
  if (!pendingRaw) throw new Error('Sign-in response without a pending request.');
  const pending = JSON.parse(pendingRaw) as { verifier: string; state: string; authority: string; clientId: string };
  if (params.get('state') !== pending.state) throw new Error('Sign-in state mismatch; try again.');
  const meta = await discover(pending.authority);
  const res = await fetch(meta.token_endpoint, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'authorization_code', code, redirect_uri: redirectUri(), client_id: pending.clientId, code_verifier: pending.verifier,
    }),
  });
  if (!res.ok) throw new Error('The identity provider rejected the sign-in.');
  const body = await res.json() as { access_token: string; expires_in?: number };
  saveToken(body.access_token, body.expires_in);
  return true;
}

/** Subject of the current token, for display only (the API validates the token; this is never trusted). */
export function whoAmI(): string {
  try {
    const payload = getToken().split('.')[1];
    if (!payload) return '';
    const claims = JSON.parse(atob(payload.replace(/-/g, '+').replace(/_/g, '/'))) as { name?: string; sub?: string };
    return claims.name ?? claims.sub ?? '';
  } catch { return ''; }
}

export const accessToken = (): string => getToken();
