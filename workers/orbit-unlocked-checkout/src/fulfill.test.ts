import assert from "node:assert/strict";
import test from "node:test";
import { shouldGrantOrbitUnlocked, type PaidUnlockSession } from "./fulfill.ts";

const sandboxPrice = "price_1UPAYIPKQWYSeF14WKeO8EUX";
const livePrice = "price_1UPAUeA2TEYBUE36j3TugK3W";

function session(overrides: Partial<PaidUnlockSession> = {}): PaidUnlockSession {
  return {
    livemode: false,
    payment_status: "paid",
    client_reference_id: "player-1",
    metadata: {
      entitlement: "orbit_unlocked",
      unity_player_id: "player-1",
      price_id: sandboxPrice,
    },
    ...overrides,
  };
}

test("grants a paid sandbox session and a paid live session", () => {
  assert.equal(shouldGrantOrbitUnlocked(session(), sandboxPrice, livePrice), true);
  assert.equal(
    shouldGrantOrbitUnlocked(
      session({
        livemode: true,
        metadata: {
          entitlement: "orbit_unlocked",
          unity_player_id: "player-1",
          price_id: livePrice,
        },
      }),
      sandboxPrice,
      livePrice,
    ),
    true,
  );
});

test("skips unpaid sessions and a price from the other mode", () => {
  assert.equal(shouldGrantOrbitUnlocked(session({ payment_status: "unpaid" }), sandboxPrice, livePrice), false);
  assert.equal(
    shouldGrantOrbitUnlocked(
      session({
        livemode: true,
        metadata: {
          entitlement: "orbit_unlocked",
          unity_player_id: "player-1",
          price_id: sandboxPrice,
        },
      }),
      sandboxPrice,
      livePrice,
    ),
    false,
  );
  assert.equal(shouldGrantOrbitUnlocked(session({ client_reference_id: "other" }), sandboxPrice, livePrice), false);
});
