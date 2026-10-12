/** Stripe session fields the webhook needs before granting Orbit Unlocked. */
export interface PaidUnlockSession {
  livemode: boolean;
  payment_status: string | null;
  client_reference_id: string | null;
  metadata: Record<string, string> | null;
}

/**
 * Grants a paid Orbit Unlocked session when its price matches its own mode.
 * A sandbox event must use the sandbox price. A live event must use the live price.
 */
export function shouldGrantOrbitUnlocked(
  session: PaidUnlockSession,
  sandboxPriceId: string,
  livePriceId: string,
): boolean {
  if (session.payment_status !== "paid")
    return false;
  const playerId = session.client_reference_id?.trim() ?? "";
  if (!playerId)
    return false;
  const meta = session.metadata ?? {};
  if (meta.entitlement !== "orbit_unlocked")
    return false;
  if (meta.unity_player_id !== playerId)
    return false;
  const expected = session.livemode ? livePriceId : sandboxPriceId;
  return meta.price_id === expected;
}
