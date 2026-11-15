#!/usr/bin/env node
// Claude Quota Widget — bridge (legacy data source, kept as a Claude Code status line)
// Status line script for Claude Code:
//   1. reads the JSON Claude Code sends on stdin;
//   2. extracts rate_limits (5-hour and 7-day windows) and writes them to quota.json;
//   3. prints a compact status line: model · context · 5h · 7d.
// Input format: https://code.claude.com/docs/en/statusline ("Rate limit usage").

import { readFileSync, writeFileSync, renameSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
export const DEFAULT_QUOTA_FILE = join(HERE, "..", "data", "quota.json");
// Rewrite the file when values change, otherwise at most every 5 minutes
// (avoids churning file-sync tools on every refresh).
const HEARTBEAT_SECONDS = 300;

/** Returns a valid { used_percentage, resets_at } window, or null. */
function pickWindow(w) {
  if (!w || typeof w.used_percentage !== "number") return null;
  return {
    used_percentage: w.used_percentage,
    resets_at: typeof w.resets_at === "number" ? w.resets_at : null,
  };
}

/** Builds the quota object from the status line JSON (null when there are no rate_limits). */
export function buildQuota(input, nowSeconds = Math.floor(Date.now() / 1000)) {
  const rl = input?.rate_limits;
  if (!rl) return null;
  const five_hour = pickWindow(rl.five_hour);
  const seven_day = pickWindow(rl.seven_day);
  if (!five_hour && !seven_day) return null;
  return {
    updated_at: nowSeconds,
    five_hour,
    seven_day,
    source: "claude-code-statusline",
    claude_code_version: input.version ?? null,
  };
}

/** Should the file be rewritten? (values changed, or last write too old) */
export function shouldWrite(previous, next) {
  if (!next) return false;
  if (!previous) return true;
  const sameValues =
    JSON.stringify([previous.five_hour, previous.seven_day]) ===
    JSON.stringify([next.five_hour, next.seven_day]);
  return !sameValues || next.updated_at - (previous.updated_at ?? 0) >= HEARTBEAT_SECONDS;
}

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch {
    return null;
  }
}

/** Atomic write: temp file, then rename. */
export function writeQuota(path, quota) {
  mkdirSync(dirname(path), { recursive: true });
  const tmp = `${path}.tmp`;
  writeFileSync(tmp, JSON.stringify(quota, null, 2) + "\n", "utf8");
  renameSync(tmp, path);
}

// ---------- Rendering ----------

const color = (pct, text) => {
  const code = pct >= 80 ? 31 : pct >= 50 ? 33 : 32; // red / yellow / green
  return `\x1b[${code}m${text}\x1b[0m`;
};

const fmtTime = (epoch) =>
  new Date(epoch * 1000).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });

const fmtDay = (epoch) =>
  new Date(epoch * 1000).toLocaleDateString(undefined, { weekday: "short" }).replace(".", "");

export function formatWindow(label, w, withDay) {
  if (!w) return null;
  const pct = Math.round(w.used_percentage);
  let reset = "";
  if (w.resets_at) reset = ` ↺${withDay ? fmtDay(w.resets_at) + " " : ""}${fmtTime(w.resets_at)}`;
  return color(pct, `${label} ${pct}%`) + `\x1b[2m${reset}\x1b[0m`;
}

export function renderStatusLine(input, quota) {
  const parts = [];
  const model = input?.model?.display_name;
  if (model) parts.push(`\x1b[1m${model}\x1b[0m`);
  const ctx = input?.context_window?.used_percentage;
  if (typeof ctx === "number") parts.push(`ctx ${Math.round(ctx)}%`);
  if (quota) {
    const five = formatWindow("5h", quota.five_hour, false);
    const week = formatWindow("7d", quota.seven_day, true);
    if (five) parts.push(five);
    if (week) parts.push(week);
  }
  return parts.join(" \x1b[2m·\x1b[0m ");
}

// ---------- Entry point ----------

function main() {
  let input = {};
  try {
    input = JSON.parse(readFileSync(0, "utf8") || "{}");
  } catch {
    // Unreadable JSON: still print something rather than breaking the status line.
  }
  const quotaFile = process.env.CLAUDE_QUOTA_FILE || DEFAULT_QUOTA_FILE;
  const quota = buildQuota(input);
  try {
    if (shouldWrite(readJson(quotaFile), quota)) writeQuota(quotaFile, quota);
  } catch {
    // Never fail the status line because of the file.
  }
  // No rate_limits in this render (start of session): show the last known values.
  process.stdout.write(renderStatusLine(input, quota ?? readJson(quotaFile)));
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) main();
