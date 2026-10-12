export type CheckoutMode = "sandbox" | "live";

/** Anything other than the exact word live stays on sandbox. */
export function checkoutMode(raw: string | undefined): CheckoutMode {
  return raw === "live" ? "live" : "sandbox";
}

export function activePriceId(mode: CheckoutMode, sandboxPriceId: string, livePriceId: string): string {
  return mode === "live" ? livePriceId : sandboxPriceId;
}

export function activeSecret(mode: CheckoutMode, sandboxSecret: string, liveSecret: string): string {
  return mode === "live" ? liveSecret : sandboxSecret;
}
