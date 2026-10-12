import Stripe from "stripe";
import { CheckoutNotConfiguredError } from "./checkout.ts";
import { activePriceId, activeSecret, checkoutMode } from "./mode.ts";

const CACHE_MS = 10 * 60 * 1000;

/** Stripe amounts for these currencies are already in the major unit. */
const ZERO_DECIMAL = new Set([
  "bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf",
  "ugx", "vnd", "vuv", "xaf", "xof", "xpf",
]);

/** Stripe amounts for these currencies use three decimal places. */
const THREE_DECIMAL = new Set(["bhd", "jod", "kwd", "omr", "tnd"]);

export interface DisplayPrice {
  display: string;
  currency: string;
  unitAmount: number;
}

let cached: (DisplayPrice & { priceId: string; expires: number }) | null = null;

/** Reads the active Stripe Price and formats it for the game menu. */
export async function readDisplayPrice(env: Env): Promise<DisplayPrice> {
  const mode = checkoutMode(env.CHECKOUT_MODE);
  const priceId = activePriceId(mode, env.STRIPE_PRICE_ID, env.STRIPE_PRICE_ID_LIVE);
  const apiKey = activeSecret(mode, env.STRIPE_RESTRICTED_KEY, env.STRIPE_RESTRICTED_KEY_LIVE);
  if (!apiKey)
    throw new CheckoutNotConfiguredError();

  const now = Date.now();
  if (cached && cached.priceId === priceId && cached.expires > now) {
    return { display: cached.display, currency: cached.currency, unitAmount: cached.unitAmount };
  }

  const stripe = new Stripe(apiKey, {
    apiVersion: "2026-09-30.endive",
    httpClient: Stripe.createFetchHttpClient(),
  });
  const price = await stripe.prices.retrieve(priceId);
  if (price.unit_amount == null)
    throw new Error("Stripe Price has no unit_amount");

  const display = formatStripeAmount(price.unit_amount, price.currency);
  const result = { display, currency: price.currency, unitAmount: price.unit_amount };
  cached = { ...result, priceId, expires: now + CACHE_MS };
  return result;
}

/** Turns a Stripe unit amount into a menu label, such as 2000 USD to $20. */
export function formatStripeAmount(unitAmount: number, currency: string): string {
  const code = currency.toLowerCase();
  const digits = ZERO_DECIMAL.has(code) ? 0 : THREE_DECIMAL.has(code) ? 3 : 2;
  const major = unitAmount / 10 ** digits;
  const whole = Number.isInteger(major);
  const fractionDigits = whole ? 0 : digits;
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: code.toUpperCase(),
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
  }).format(major);
}
