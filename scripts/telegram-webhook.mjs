#!/usr/bin/env node
/**
 * Registers, inspects and removes the Telegram webhook.
 *
 * Telegram only pushes updates to a URL you have explicitly registered, so a
 * freshly deployed bot stays silent until this runs. It is a one-time step per
 * deployment URL — but re-run it after changing TELEGRAM_WEBHOOK_SECRET or
 * moving to a different domain.
 *
 *   npm run webhook:set  -- https://your-deployment.vercel.app
 *   npm run webhook:info
 *   npm run webhook:delete
 *
 * Reads TELEGRAM_BOT_TOKEN and TELEGRAM_WEBHOOK_SECRET from the environment or
 * from .env.local. Use the *production* URL: preview URLs change every deploy.
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');

/** Minimal .env reader — real values in the environment always win. */
function loadEnvFile(file) {
  let text;
  try {
    text = readFileSync(join(ROOT, file), 'utf8');
  } catch {
    return;
  }
  for (const line of text.split('\n')) {
    const match = /^\s*([A-Z0-9_]+)\s*=\s*(.*)$/.exec(line);
    if (!match) continue;
    const [, key, rawValue] = match;
    if (process.env[key]) continue;
    process.env[key] = rawValue.trim().replace(/^["']|["']$/g, '');
  }
}

function fail(message) {
  console.error(`\n  ${message}\n`);
  process.exit(1);
}

async function callTelegram(token, method, body) {
  const response = await fetch(`https://api.telegram.org/bot${token}/${method}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body ?? {}),
  });

  const payload = await response.json().catch(() => ({}));
  if (!payload.ok) {
    // 401 here means the token is wrong, not that the deployment is down.
    fail(`Telegram rejected ${method}: ${payload.description ?? response.status}`);
  }
  return payload.result;
}

function describe(info) {
  const lines = [
    ['url', info.url || '(none registered)'],
    ['pending updates', info.pending_update_count ?? 0],
    ['custom certificate', info.has_custom_certificate ? 'yes' : 'no'],
    ['max connections', info.max_connections ?? '(default)'],
    ['allowed updates', (info.allowed_updates ?? ['(all)']).join(', ')],
  ];
  if (info.last_error_message) {
    lines.push(['last error', info.last_error_message]);
    lines.push(['last error at', new Date((info.last_error_date ?? 0) * 1000).toISOString()]);
  }
  const width = Math.max(...lines.map(([label]) => label.length));
  return lines.map(([label, value]) => `  ${label.padEnd(width)}  ${value}`).join('\n');
}

async function main() {
  loadEnvFile('.env.local');

  const [command = 'info', urlArgument] = process.argv.slice(2);
  const token = process.env.TELEGRAM_BOT_TOKEN?.trim();
  if (!token) fail('TELEGRAM_BOT_TOKEN is not set. Put it in .env.local or export it.');

  if (command === 'info') {
    const [info, me] = await Promise.all([
      callTelegram(token, 'getWebhookInfo'),
      callTelegram(token, 'getMe'),
    ]);
    console.log(`\n  bot: @${me.username} (${me.first_name})\n`);
    console.log(describe(info));
    console.log();
    if (info.last_error_message) {
      console.log('  A last error usually means a mismatched TELEGRAM_WEBHOOK_SECRET (403)');
      console.log('  or a function that crashed (500). Check the Vercel function logs.\n');
    }
    return;
  }

  if (command === 'delete') {
    await callTelegram(token, 'deleteWebhook');
    console.log('\n  Webhook removed. The bot will not receive updates until you set it again.\n');
    return;
  }

  if (command !== 'set') {
    fail(`Unknown command "${command}". Use: set <url> | info | delete`);
  }

  const base = (urlArgument ?? process.env.DEPLOYMENT_URL ?? '').trim().replace(/\/+$/, '');
  if (!base) fail('Pass the deployment URL: npm run webhook:set -- https://your-app.vercel.app');
  if (!base.startsWith('https://')) fail('Telegram requires an https:// URL.');

  const secret = process.env.TELEGRAM_WEBHOOK_SECRET?.trim();
  if (!secret) {
    // Without it the endpoint cannot tell Telegram from anyone else who found the URL.
    console.warn('\n  Warning: TELEGRAM_WEBHOOK_SECRET is not set, so the webhook will be unauthenticated.');
  }

  await callTelegram(token, 'setWebhook', {
    url: `${base}/api/telegram/message`,
    allowed_updates: ['message', 'edited_message', 'callback_query'],
    ...(secret ? { secret_token: secret } : {}),
  });

  const [info, me] = await Promise.all([
    callTelegram(token, 'getWebhookInfo'),
    callTelegram(token, 'getMe'),
  ]);

  console.log(`\n  Webhook set for @${me.username}\n`);
  console.log(describe(info));
  console.log('\n  The same secret must be set as TELEGRAM_WEBHOOK_SECRET in Vercel,');
  console.log('  and Vercel needs a redeploy after any variable change.');
  console.log(`\n  Now message the bot. If nothing comes back, run: npm run webhook:info\n`);
}

main().catch((error) => fail(String(error)));
