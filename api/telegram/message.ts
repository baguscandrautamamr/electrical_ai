/**
 * POST /api/telegram/message — Telegram webhook.
 *
 * Flow:
 *   1. Verify the secret token Telegram echoes back.
 *   2. Resolve the sender to a registered user.
 *   3. Parse (grammar, then Claude for natural language).
 *   4. Admin commands answer inline; device commands go on the queue.
 *   5. Ack immediately, then wait inline for the Revit result so the common
 *      case reads as a single synchronous exchange.
 *
 * Telegram retries any non-2xx, so this always returns 200 once the update has
 * been accepted — failures are reported to the user in-chat, not via status code.
 */

import { env } from '../../src/config/env.js';
import { telegram, type TelegramUpdate } from '../../src/lib/telegram.js';
import { parseMessage } from '../../src/parser/index.js';
import { specFor } from '../../src/parser/schema.js';
import { validateParams } from '../../src/parser/validate.js';
import { handleAdminCommand } from '../../src/services/admin.js';
import { attachAckMessage, enqueueCommand } from '../../src/services/queue.js';
import { deliverCommandResult, waitForCommand } from '../../src/services/delivery.js';
import {
  findUserByTelegramId,
  resolveActiveProject,
  roleAtLeast,
  touchUser,
} from '../../src/services/users.js';
import {
  formatAck,
  formatError,
  formatValidationIssues,
  type FormatContext,
} from '../../src/format/index.js';

/** How long the webhook waits inline before handing off to the sweeper. */
const INLINE_WAIT_MS = 40_000;
const POLL_INTERVAL_MS = 1_500;

function ok(body: Record<string, unknown> = { ok: true }): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

export async function POST(request: Request): Promise<Response> {
  // --- 1. Authenticate the webhook -----------------------------------------
  const expectedSecret = env.telegramWebhookSecret;
  if (expectedSecret) {
    const presented = request.headers.get('x-telegram-bot-api-secret-token');
    if (presented !== expectedSecret) {
      return new Response('forbidden', { status: 403 });
    }
  }

  let update: TelegramUpdate;
  try {
    update = (await request.json()) as TelegramUpdate;
  } catch {
    return ok({ ok: true, ignored: 'unparseable body' });
  }

  const message = update.message ?? update.edited_message;
  const text = message?.text?.trim();
  const from = message?.from;

  if (!message || !text || !from || from.is_bot) {
    return ok({ ok: true, ignored: 'no actionable message' });
  }

  const chatId = message.chat.id;

  // --- 2. Resolve the sender ------------------------------------------------
  let user = await findUserByTelegramId(from.id);
  if (!user) {
    // No user row yet: reply with defaults so an unregistered person still gets
    // a comprehensible message rather than silence.
    const ctx: FormatContext = { language: 'id', theme: 'light' };
    await telegram().sendMessage(chatId, formatError(ctx, 'errors.not_registered'));
    return ok();
  }

  user = await touchUser(user, from);

  let ctx: FormatContext = { language: user.language, theme: user.theme };

  if (!user.is_active) {
    await telegram().sendMessage(chatId, formatError(ctx, 'errors.inactive_user'));
    return ok();
  }

  // --- 3. Parse -------------------------------------------------------------
  let outcome;
  try {
    outcome = await parseMessage(text);
  } catch (error) {
    console.error('[webhook] parse failed', error);
    await telegram().sendMessage(chatId, formatError(ctx, 'errors.parse_failed'));
    return ok();
  }

  if (outcome.kind === 'unparsed') {
    const key =
      outcome.reason === 'unknown_command' ? 'errors.unknown_command' : 'errors.parse_failed';
    await telegram().sendMessage(
      chatId,
      formatError(ctx, key, { command: text.split(/\s+/)[0] ?? text }),
    );
    return ok();
  }

  // --- 4a. Admin commands ---------------------------------------------------
  if (outcome.kind === 'admin') {
    try {
      const reply = await handleAdminCommand({ ...ctx, user }, outcome.admin);
      if (reply.languageChangedTo) ctx = { ...ctx, language: reply.languageChangedTo };
      if (reply.themeChangedTo) ctx = { ...ctx, theme: reply.themeChangedTo };
      await telegram().sendMessage(chatId, reply.text);
    } catch (error) {
      console.error('[webhook] admin command failed', error);
      await telegram().sendMessage(
        chatId,
        formatError(ctx, 'common.unknown_error', {}, String(error)),
      );
    }
    return ok();
  }

  // --- 4b. Device commands --------------------------------------------------
  const command = outcome.command;
  const spec = specFor(command.type);
  if (!spec) {
    await telegram().sendMessage(
      chatId,
      formatError(ctx, 'errors.unknown_command', { command: command.type }),
    );
    return ok();
  }

  const access = await resolveActiveProject(user);
  if (!access) {
    await telegram().sendMessage(chatId, formatError(ctx, 'errors.no_active_project'));
    return ok();
  }

  if (!roleAtLeast(access.role, spec.role)) {
    await telegram().sendMessage(
      chatId,
      formatError(ctx, 'errors.insufficient_role', { required: spec.role, actual: access.role }),
    );
    return ok();
  }

  const validation = validateParams(spec, command.subject, command.params);
  if (!validation.ok) {
    await telegram().sendMessage(
      chatId,
      formatValidationIssues(ctx, command.type, validation.issues),
    );
    return ok();
  }

  // --- 5. Enqueue, ack, and wait -------------------------------------------
  let enqueued;
  try {
    enqueued = await enqueueCommand({
      userId: user.id,
      projectId: access.project.id,
      chatId,
      replyToMessageId: message.message_id,
      commandType: command.type,
      commandText: command.raw,
      params: validation.normalized,
    });
  } catch (error) {
    console.error('[webhook] enqueue failed', error);
    await telegram().sendMessage(
      chatId,
      formatError(ctx, 'common.unknown_error', {}, String(error)),
    );
    return ok();
  }

  const ack = await telegram().sendMessage(
    chatId,
    formatAck(ctx, {
      commandType: command.type,
      queuePosition: enqueued.queuePosition,
      pollSeconds: env.pollingIntervalSeconds,
    }),
    { replyToMessageId: message.message_id },
  );
  await attachAckMessage(enqueued.command.id, ack.message_id);

  const finished = await waitForCommand(enqueued.command.id, {
    timeoutMs: INLINE_WAIT_MS,
    intervalMs: POLL_INTERVAL_MS,
  });

  if (finished) {
    // Re-read is unnecessary; waitForCommand already returned the fresh row,
    // but ack_message_id was written after enqueue so patch it in.
    await deliverCommandResult({ ...finished, ack_message_id: ack.message_id });
  }
  // Still running: /api/telegram/callback will deliver it.

  return ok({ ok: true, command_id: enqueued.command.id, delivered: Boolean(finished) });
}

/** Health probe for the webhook URL itself. */
export function GET(): Response {
  return ok({ ok: true, endpoint: 'telegram/message', method: 'POST' });
}
