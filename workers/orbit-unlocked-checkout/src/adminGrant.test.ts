import assert from "node:assert/strict";
import test from "node:test";
import { decideAdminGrant } from "./adminGrant.ts";

const secret = "grant-secret";

test("rejects a missing or wrong secret", () => {
  const missing = decideAdminGrant({
    origin: null,
    authorization: null,
    secret,
    playerId: "player-1",
    grantedBy: "admin-1",
  });
  assert.equal(missing.ok, false);
  if (!missing.ok)
    assert.equal(missing.status, 401);

  const wrong = decideAdminGrant({
    origin: null,
    authorization: "Bearer other-secret",
    secret,
    playerId: "player-1",
    grantedBy: "admin-1",
  });
  assert.equal(wrong.ok, false);
  if (!wrong.ok)
    assert.equal(wrong.error, "unauthorized");

  const unset = decideAdminGrant({
    origin: null,
    authorization: "Bearer " + secret,
    secret: "",
    playerId: "player-1",
    grantedBy: "admin-1",
  });
  assert.equal(unset.ok, false);
  if (!unset.ok)
    assert.equal(unset.status, 503);
});

test("rejects a browser origin and a bad player id", () => {
  const browser = decideAdminGrant({
    origin: "https://titanorbit.io",
    authorization: "Bearer " + secret,
    secret,
    playerId: "player-1",
    grantedBy: "admin-1",
  });
  assert.equal(browser.ok, false);
  if (!browser.ok)
    assert.equal(browser.status, 403);

  const badId = decideAdminGrant({
    origin: null,
    authorization: "Bearer " + secret,
    secret,
    playerId: "not a player",
    grantedBy: "admin-1",
  });
  assert.equal(badId.ok, false);
  if (!badId.ok)
    assert.equal(badId.status, 400);
});

test("accepts a valid grant shape", () => {
  const grant = decideAdminGrant({
    origin: null,
    authorization: "Bearer " + secret,
    secret,
    playerId: " player_1 ",
    grantedBy: "admin-1",
  });
  assert.deepEqual(grant, { ok: true, playerId: "player_1", grantedBy: "admin-1" });
});
