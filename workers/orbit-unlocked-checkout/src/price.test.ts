import assert from "node:assert/strict";
import test from "node:test";
import { formatStripeAmount } from "./price.ts";

test("formats a whole dollar Stripe price without cents", () => {
  assert.equal(formatStripeAmount(2000, "usd"), "$20");
});

test("keeps cents when the Stripe price is not a whole dollar", () => {
  assert.equal(formatStripeAmount(1999, "usd"), "$19.99");
});

test("formats a zero-decimal currency in major units", () => {
  assert.equal(formatStripeAmount(500, "jpy"), "¥500");
});
