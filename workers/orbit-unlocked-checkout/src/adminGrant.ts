import { timingSafeEqual } from "node:crypto";
import { isPlausiblePlayerId } from "./unity.ts";

const MAX_BODY = 4_096;

export type AdminGrantBody = {
  playerId?: unknown;
  grantedBy?: unknown;
};

export type AdminGrantDecision =
  | { ok: true; playerId: string; grantedBy: string }
  | { ok: false; status: number; error: string };

/**
 * Decides whether a server gift call may write Cloud Save.
 * A browser Origin is refused. The bearer token must match the Worker secret.
 * Both ids must look like Unity player ids.
 */
export function decideAdminGrant(input: {
  origin: string | null;
  authorization: string | null;
  secret: string | undefined;
  playerId: string;
  grantedBy: string;
}): AdminGrantDecision {
  if (input.origin)
    return { ok: false, status: 403, error: "browser_not_allowed" };

  const secret = input.secret ?? "";
  if (!secret)
    return { ok: false, status: 503, error: "grant_not_configured" };
  if (!bearerMatches(input.authorization, secret))
    return { ok: false, status: 401, error: "unauthorized" };

  const playerId = input.playerId.trim();
  const grantedBy = input.grantedBy.trim();
  if (!isPlausiblePlayerId(playerId) || !isPlausiblePlayerId(grantedBy))
    return { ok: false, status: 400, error: "bad_request" };

  return { ok: true, playerId, grantedBy };
}

/** Reads a small JSON body. Null when the body is too large or not an object. */
export async function readAdminGrantBody(request: Request): Promise<AdminGrantBody | null> {
  const raw = await request.text();
  if (raw.length > MAX_BODY)
    return null;
  try {
    const body = JSON.parse(raw) as AdminGrantBody;
    if (body == null || typeof body !== "object")
      return null;
    return body;
  } catch {
    return null;
  }
}

export type AdminLookupDecision =
  | { ok: true; playerId: string }
  | { ok: false; status: number; error: string };

/**
 * Same bearer check as a gift, but the body only names the player to read.
 * The dedicated server calls this on join. Browsers are refused.
 */
export function decideAdminLookup(input: {
  origin: string | null;
  authorization: string | null;
  secret: string | undefined;
  playerId: string;
}): AdminLookupDecision {
  if (input.origin)
    return { ok: false, status: 403, error: "browser_not_allowed" };

  const secret = input.secret ?? "";
  if (!secret)
    return { ok: false, status: 503, error: "grant_not_configured" };
  if (!bearerMatches(input.authorization, secret))
    return { ok: false, status: 401, error: "unauthorized" };

  const playerId = input.playerId.trim();
  if (!isPlausiblePlayerId(playerId))
    return { ok: false, status: 400, error: "bad_request" };

  return { ok: true, playerId };
}

export function asId(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function bearerMatches(authorization: string | null, secret: string): boolean {
  if (!authorization)
    return false;
  const prefix = "Bearer ";
  if (!authorization.startsWith(prefix))
    return false;
  const given = Buffer.from(authorization.slice(prefix.length));
  const expected = Buffer.from(secret);
  if (given.length !== expected.length)
    return false;
  return timingSafeEqual(given, expected);
}
