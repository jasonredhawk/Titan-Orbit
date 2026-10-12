import Stripe from "stripe";
import { logInfo } from "./log.ts";
import { activePriceId, activeSecret, checkoutMode } from "./mode.ts";
import { assertUnityPlayer, isPlausiblePlayerId, UnityAuthError } from "./unity.ts";

/** Stable Checkout label so sessions can be compared in the Stripe Dashboard. */
const INTEGRATION_IDENTIFIER = "titanorbit_webgl_mnxqplwd";

export interface CheckoutBody {
  playerId?: unknown;
  accessToken?: unknown;
}

export async function createCheckout(env: Env, body: CheckoutBody): Promise<{ url: string }> {
  const playerId = typeof body.playerId === "string" ? body.playerId.trim() : "";
  const accessToken = typeof body.accessToken === "string" ? body.accessToken.trim() : "";
  if (!isPlausiblePlayerId(playerId) || accessToken.length < 8 || accessToken.length > 8192)
    throw new CheckoutInputError();

  const mode = checkoutMode(env.CHECKOUT_MODE);
  const priceId = activePriceId(mode, env.STRIPE_PRICE_ID, env.STRIPE_PRICE_ID_LIVE);
  const apiKey = activeSecret(mode, env.STRIPE_RESTRICTED_KEY, env.STRIPE_RESTRICTED_KEY_LIVE);
  if (!apiKey)
    throw new CheckoutNotConfiguredError();

  await assertUnityPlayer(env, playerId, accessToken);

  const stripe = new Stripe(apiKey, {
    apiVersion: "2026-09-30.endive",
    httpClient: Stripe.createFetchHttpClient(),
  });

  const minute = Math.floor(Date.now() / 60_000);
  const params: Stripe.Checkout.SessionCreateParams & { integration_identifier: string } = {
    mode: "payment",
    line_items: [{ price: priceId, quantity: 1 }],
    success_url: env.SUCCESS_URL,
    cancel_url: env.CANCEL_URL,
    client_reference_id: playerId,
    metadata: {
      unity_player_id: playerId,
      entitlement: "orbit_unlocked",
      price_id: priceId,
    },
    integration_identifier: INTEGRATION_IDENTIFIER,
  };
  const session = await stripe.checkout.sessions.create(params, {
    idempotencyKey: `orbit-unlocked:${mode}:${playerId}:${minute}`,
  });

  if (!session.url)
    throw new Error("Stripe Checkout did not return a url");

  logInfo({ message: "checkout_session_created", mode, playerId, sessionId: session.id });
  return { url: session.url };
}

export class CheckoutInputError extends Error {
  constructor() {
    super("bad_checkout_request");
    this.name = "CheckoutInputError";
  }
}

export class CheckoutNotConfiguredError extends Error {
  constructor() {
    super("checkout_not_configured");
    this.name = "CheckoutNotConfiguredError";
  }
}
