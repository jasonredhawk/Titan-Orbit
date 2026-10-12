import Stripe from "stripe";
import { shouldGrantOrbitUnlocked } from "./fulfill.ts";
import { logError, logInfo } from "./log.ts";
import { grantOrbitUnlocked } from "./unity.ts";

const FULFILL_EVENTS = new Set([
  "checkout.session.completed",
  "checkout.session.async_payment_succeeded",
]);

/** Verifies either webhook secret, then grants Orbit Unlocked for that session's own price. */
export async function handleWebhook(env: Env, payload: string, signature: string | null): Promise<Response> {
  if (!signature)
    return new Response("missing signature", { status: 400 });

  let event: Stripe.Event;
  try {
    event = await readSignedEvent(env, payload, signature);
  } catch (error) {
    logError({
      message: "webhook_signature_rejected",
      error: error instanceof Error ? error.message : "invalid signature",
    });
    return new Response("invalid signature", { status: 400 });
  }

  if (!FULFILL_EVENTS.has(event.type)) {
    logInfo({ message: "webhook_ignored", eventType: event.type, eventId: event.id });
    return new Response("ok", { status: 200 });
  }

  const session = event.data.object as Stripe.Checkout.Session;
  const grant = shouldGrantOrbitUnlocked(
    {
      livemode: session.livemode,
      payment_status: session.payment_status,
      client_reference_id: session.client_reference_id,
      metadata: session.metadata,
    },
    env.STRIPE_PRICE_ID,
    env.STRIPE_PRICE_ID_LIVE,
  );

  if (!grant) {
    logInfo({
      message: "webhook_not_granted",
      eventType: event.type,
      eventId: event.id,
      paymentStatus: session.payment_status,
      livemode: session.livemode,
    });
    return new Response("ok", { status: 200 });
  }

  const playerId = session.client_reference_id!.trim();
  await grantOrbitUnlocked(env, playerId);
  logInfo({
    message: "orbit_unlocked_granted",
    eventType: event.type,
    eventId: event.id,
    livemode: session.livemode,
    playerId,
    sessionId: session.id,
  });
  return new Response("ok", { status: 200 });
}

async function readSignedEvent(env: Env, payload: string, signature: string): Promise<Stripe.Event> {
  const apiKey = env.STRIPE_RESTRICTED_KEY || env.STRIPE_RESTRICTED_KEY_LIVE;
  if (!apiKey)
    throw new Error("no stripe key");
  const stripe = new Stripe(apiKey, {
    apiVersion: "2026-09-30.endive",
    httpClient: Stripe.createFetchHttpClient(),
  });
  const secrets = [env.STRIPE_WEBHOOK_SECRET, env.STRIPE_WEBHOOK_SECRET_LIVE].filter((secret) => secret);
  let lastError: unknown;
  for (const secret of secrets) {
    try {
      return await stripe.webhooks.constructEventAsync(payload, signature, secret);
    } catch (error) {
      lastError = error;
    }
  }
  throw lastError instanceof Error ? lastError : new Error("invalid signature");
}
