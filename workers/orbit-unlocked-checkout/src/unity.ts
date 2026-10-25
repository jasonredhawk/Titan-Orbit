const CLOUD_SAVE_HOST = "https://cloud-save.services.api.unity.com";
const TOKEN_EXCHANGE = "https://services.api.unity.com/auth/v1/token-exchange";
const ENTITLEMENT_KEY = "orbit_unlocked";
const SOURCE_KEY = "orbit_unlocked_source";
const BETA_KEY = "beta_tester";
const BETA_SOURCE_KEY = "beta_tester_source";

export function isPlausiblePlayerId(playerId: string): boolean {
  return /^[A-Za-z0-9_-]{1,128}$/.test(playerId);
}

/**
 * Confirms the Unity access token belongs to this player by reading their Cloud Save.
 * A 404 means the token was accepted and the key is not written yet.
 */
export async function assertUnityPlayer(env: Env, playerId: string, accessToken: string): Promise<void> {
  const url =
    `${CLOUD_SAVE_HOST}/v1/data/projects/${env.UNITY_PROJECT_ID}/players/` +
    `${encodeURIComponent(playerId)}/items?keys=${ENTITLEMENT_KEY}`;
  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${accessToken}`,
      ProjectId: env.UNITY_PROJECT_ID,
    },
  });
  if (response.status === 401 || response.status === 403)
    throw new UnityAuthError();
  if (!response.ok && response.status !== 404) {
    const detail = (await response.text()).slice(0, 200);
    throw new Error(`Unity player check failed: ${response.status} ${detail}`);
  }
}

/** Writes orbit_unlocked with a Unity service account. The browser cannot call this. */
export async function grantOrbitUnlocked(env: Env, playerId: string): Promise<void> {
  const token = await exchangeServiceAccount(env);
  await writeItem(env, token, playerId, ENTITLEMENT_KEY, true);
}

/**
 * Admin gift. Reads the flag first so a purchase is not relabeled.
 * When it is still locked, writes the same true flag Stripe uses, plus a source note.
 */
export async function grantOrbitUnlockedFromAdmin(
  env: Env,
  playerId: string,
  grantedBy: string,
): Promise<{ alreadyOwned: boolean }> {
  const token = await exchangeServiceAccount(env);
  if (await readOrbitUnlockedOwned(env, token, playerId))
    return { alreadyOwned: true };

  await writeItem(env, token, playerId, ENTITLEMENT_KEY, true);
  try {
    await writeItem(env, token, playerId, SOURCE_KEY, `admin:${grantedBy}`);
  } catch (error) {
    const message = error instanceof Error ? error.message : "source write failed";
    console.error(JSON.stringify({ level: "error", message: "admin_source_write_failed", detail: message }));
  }
  return { alreadyOwned: false };
}

async function readOrbitUnlockedOwned(env: Env, token: string, playerId: string): Promise<boolean> {
  return readFlag(env, token, playerId, ENTITLEMENT_KEY);
}

/**
 * Admin gift for the beta-tester item. Same Cloud Save write as a flag the game
 * already uses for Orbit Unlocked, on the key `beta_tester`.
 * Already-owned accounts are not relabeled.
 */
export async function grantBetaTesterFromAdmin(
  env: Env,
  playerId: string,
  grantedBy: string,
): Promise<{ alreadyOwned: boolean }> {
  const token = await exchangeServiceAccount(env);
  if (await readFlag(env, token, playerId, BETA_KEY))
    return { alreadyOwned: true };

  await writeItem(env, token, playerId, BETA_KEY, true);
  try {
    await writeItem(env, token, playerId, BETA_SOURCE_KEY, `admin:${grantedBy}`);
  } catch (error) {
    const message = error instanceof Error ? error.message : "source write failed";
    console.error(JSON.stringify({ level: "error", message: "beta_source_write_failed", detail: message }));
  }
  return { alreadyOwned: false };
}

/** Server lookup. True only when Cloud Save has beta_tester set. Missing key is false. */
export async function readBetaTesterOwned(env: Env, playerId: string): Promise<boolean> {
  const token = await exchangeServiceAccount(env);
  return readFlag(env, token, playerId, BETA_KEY);
}

async function readFlag(env: Env, token: string, playerId: string, key: string): Promise<boolean> {
  const url =
    `${CLOUD_SAVE_HOST}/v1/data/projects/${env.UNITY_PROJECT_ID}/players/` +
    `${encodeURIComponent(playerId)}/items?keys=${encodeURIComponent(key)}`;
  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${token}`,
      ProjectId: env.UNITY_PROJECT_ID,
    },
  });
  if (response.status === 404)
    return false;
  if (!response.ok) {
    const detail = (await response.text()).slice(0, 300);
    throw new Error(`Cloud Save read failed: ${response.status} ${detail}`);
  }
  const json = await response.text();
  return cloudSaveKeyIsTrue(json, key);
}

/**
 * True only when this key's own value is true.
 * A player who owns Orbit Unlocked must not look like they already own beta tester
 * just because some other key in the body is true.
 */
export function cloudSaveKeyIsTrue(json: string, key: string): boolean {
  const markers = [`"key":"${key}"`, `"key": "${key}"`];
  for (let i = 0; i < markers.length; i++) {
    const at = json.indexOf(markers[i]);
    if (at < 0)
      continue;
    const window = json.slice(at, at + 160);
    if (window.indexOf('"value":true') >= 0 || window.indexOf('"value": true') >= 0)
      return true;
  }
  return false;
}

async function writeItem(
  env: Env,
  token: string,
  playerId: string,
  key: string,
  value: boolean | string,
): Promise<void> {
  const url =
    `${CLOUD_SAVE_HOST}/v1/data/projects/${env.UNITY_PROJECT_ID}/players/` +
    `${encodeURIComponent(playerId)}/items`;
  const response = await fetch(url, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
      ProjectId: env.UNITY_PROJECT_ID,
    },
    body: JSON.stringify({ key, value }),
  });
  if (!response.ok) {
    const detail = (await response.text()).slice(0, 300);
    throw new Error(`Cloud Save write failed: ${response.status} ${detail}`);
  }
}

export class UnityAuthError extends Error {
  constructor() {
    super("unity_auth");
    this.name = "UnityAuthError";
  }
}

async function exchangeServiceAccount(env: Env): Promise<string> {
  if (!env.UNITY_ENVIRONMENT_ID)
    throw new Error("UNITY_ENVIRONMENT_ID is not set");
  const credentials = btoa(`${env.UNITY_SERVICE_ACCOUNT_KEY_ID}:${env.UNITY_SERVICE_ACCOUNT_SECRET}`);
  const params = new URLSearchParams({
    projectId: env.UNITY_PROJECT_ID,
    environmentId: env.UNITY_ENVIRONMENT_ID,
  });
  const response = await fetch(`${TOKEN_EXCHANGE}?${params}`, {
    method: "POST",
    headers: {
      Authorization: `Basic ${credentials}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ scopes: [] }),
  });
  if (!response.ok) {
    const detail = (await response.text()).slice(0, 300);
    throw new Error(`Unity token exchange failed: ${response.status} ${detail}`);
  }
  const body = (await response.json()) as { accessToken?: string };
  if (!body.accessToken)
    throw new Error("Unity token exchange returned no accessToken");
  return body.accessToken;
}
