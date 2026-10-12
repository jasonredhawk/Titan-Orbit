import { CheckoutInputError, CheckoutNotConfiguredError, createCheckout, type CheckoutBody } from "./checkout.ts";
import { allowedOrigin, corsHeaders, json } from "./http.ts";
import { logError, logInfo } from "./log.ts";
import { activePriceId, checkoutMode } from "./mode.ts";
import { readDisplayPrice } from "./price.ts";
import { UnityAuthError } from "./unity.ts";
import { handleWebhook } from "./webhook.ts";

const MAX_CHECKOUT_BODY = 16_384;

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);
    const origin = allowedOrigin(request, env.ALLOWED_ORIGINS);
    if (origin === "")
      return json({ error: "origin_not_allowed" }, 403);

    if (request.method === "OPTIONS")
      return new Response(null, { status: 204, headers: corsHeaders(origin, request) });

    try {
      if (request.method === "GET" && url.pathname === "/health") {
        const mode = checkoutMode(env.CHECKOUT_MODE);
        return json({
          ok: true,
          mode,
          priceId: activePriceId(mode, env.STRIPE_PRICE_ID, env.STRIPE_PRICE_ID_LIVE),
        }, 200, corsHeaders(origin));
      }

      if (request.method === "GET" && url.pathname === "/price") {
        const price = await readDisplayPrice(env);
        return json(price, 200, corsHeaders(origin));
      }

      if (request.method === "POST" && url.pathname === "/checkout")
        return await postCheckout(request, env, origin);

      if (request.method === "POST" && url.pathname === "/webhook") {
        const payload = await request.text();
        return await handleWebhook(env, payload, request.headers.get("stripe-signature"));
      }

      return json({ error: "not_found" }, 404, corsHeaders(origin));
    } catch (error) {
      if (error instanceof CheckoutNotConfiguredError)
        return json({ error: "checkout_not_configured" }, 503, corsHeaders(origin));
      logError({
        message: "request_failed",
        path: url.pathname,
        error: error instanceof Error ? error.message : "unknown",
      });
      return json({ error: "server_error" }, 500, corsHeaders(origin));
    }
  },
} satisfies ExportedHandler<Env>;

async function postCheckout(request: Request, env: Env, origin: string | null): Promise<Response> {
  const raw = await request.text();
  if (raw.length > MAX_CHECKOUT_BODY)
    return json({ error: "body_too_large" }, 413, corsHeaders(origin));

  let body: CheckoutBody;
  try {
    body = JSON.parse(raw) as CheckoutBody;
  } catch {
    return json({ error: "invalid_json" }, 400, corsHeaders(origin));
  }

  try {
    const created = await createCheckout(env, body);
    logInfo({ message: "checkout_ready" });
    return json(created, 200, corsHeaders(origin));
  } catch (error) {
    if (error instanceof CheckoutInputError)
      return json({ error: "bad_request" }, 400, corsHeaders(origin));
    if (error instanceof CheckoutNotConfiguredError)
      return json({ error: "checkout_not_configured" }, 503, corsHeaders(origin));
    if (error instanceof UnityAuthError)
      return json({ error: "sign_in_required" }, 401, corsHeaders(origin));
    throw error;
  }
}
