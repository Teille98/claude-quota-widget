import { test } from "node:test";
import assert from "node:assert/strict";
import { buildQuota, shouldWrite, renderStatusLine } from "./statusline.mjs";

const sample = {
  version: "2.1.325",
  model: { display_name: "Opus 5.5" },
  context_window: { used_percentage: 34.2 },
  rate_limits: {
    five_hour: { used_percentage: 23.5, resets_at: 1738425600 },
    seven_day: { used_percentage: 41.2, resets_at: 1738857600 },
  },
};

test("buildQuota extracts both windows", () => {
  const q = buildQuota(sample, 1000);
  assert.equal(q.updated_at, 1000);
  assert.deepEqual(q.five_hour, { used_percentage: 23.5, resets_at: 1738425600 });
  assert.deepEqual(q.seven_day, { used_percentage: 41.2, resets_at: 1738857600 });
  assert.equal(q.claude_code_version, "2.1.325");
});

test("buildQuota returns null without rate_limits (start of session, API account)", () => {
  assert.equal(buildQuota({}), null);
  assert.equal(buildQuota({ rate_limits: {} }), null);
});

test("buildQuota handles a missing window (dropped after its reset)", () => {
  const q = buildQuota({ rate_limits: { seven_day: { used_percentage: 10, resets_at: 5 } } }, 1);
  assert.equal(q.five_hour, null);
  assert.equal(q.seven_day.used_percentage, 10);
});

test("shouldWrite: new values → yes; same recent values → no; heartbeat → yes", () => {
  const a = buildQuota(sample, 1000);
  assert.equal(shouldWrite(null, a), true);
  assert.equal(shouldWrite(a, { ...a, updated_at: 1100 }), false);
  assert.equal(shouldWrite(a, { ...a, updated_at: 1300 }), true);
  const changed = { ...a, updated_at: 1010, five_hour: { used_percentage: 30, resets_at: 1738425600 } };
  assert.equal(shouldWrite(a, changed), true);
  assert.equal(shouldWrite(a, null), false);
});

test("renderStatusLine shows model, context and quotas", () => {
  const plain = renderStatusLine(sample, buildQuota(sample)).replace(/\x1b\[[0-9;]*m/g, "");
  assert.match(plain, /^Opus 5\.5 · ctx 34% · 5h 24% ↺\d\d:\d\d · 7d 41% ↺\S+ \d\d:\d\d$/);
});

test("renderStatusLine without quota shows only model and context", () => {
  const plain = renderStatusLine({ model: { display_name: "X" } }, null).replace(/\x1b\[[0-9;]*m/g, "");
  assert.equal(plain, "X");
});
