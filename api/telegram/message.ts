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
import { telegram, type CallbackQuery, type TelegramUpdate } from '../../src/lib/telegram.js';
import { parseMessage } from '../../src/parser/index.js';
import { specFor } from '../../src/parser/schema.js';
import { validateParams } from '../../src/parser/validate.js';
import { handleAdminCommand, PROJECT_CALLBACK } from '../../src/services/admin.js';
import {
  attachAckMessage,
  cancelCommand,
  confirmCommand,
  enqueueCommand,
  findLastPlacement,
  getCommand,
  placementOf,
} from '../../src/services/queue.js';
import { deliverCommandResult, waitForCommand } from '../../src/services/delivery.js';
import {
  accessForProjectOrAdmin,
  findUserByTelegramId,
  resolveActiveProject,
  roleAtLeast,
  setActiveProject,
  touchUser,
} from '../../src/services/users.js';
import {
  formatAck,
  formatError,
  formatValidationIssues,
  type FormatContext,
} from '../../src/format/index.js';
import { translator } from '../../src/i18n/index.js';
import { MessageBuilder } from '../../src/format/message.js';
import type { ParsedCommand, User } from '../../src/types/index.js';

/** How long the webhook waits inline before handing off to the sweeper. */
const INLINE_WAIT_MS = 40_000;
const POLL_INTERVAL_MS = 1_500;

/**
 * Commands that take work out of the drawing, and so get asked about first.
 *
 * The add-in cannot ask: it polls a queue and never sees the person who typed
 * the command. So the question happens here, before the row becomes visible to
 * Revit at all.
 */
const NEEDS_CONFIRMATION = new Set(['delete_devices', 'modify_devices']);

const CONFIRM_CALLBACK = 'cfm:';
const CANCEL_CALLBACK = 'cxl:';

function ok(body: Record<string, unknown> = { ok: true }): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

/**
 * Waits for a queued command and delivers it, editing `ackMessageId` in place.
 *
 * Shared by the two ways a command reaches the queue — typed straight in, or
 * released by a confirmation tap — so both feel the same: a single message that
 * turns from "queued" into the result.
 */
async function awaitAndDeliver(
  ctx: FormatContext,
  chatId: number,
  commandId: string,
  ackMessageId: number,
): Promise<boolean> {
  await attachAckMessage(commandId, ackMessageId);

  const finished = await waitForCommand(commandId, {
    timeoutMs: INLINE_WAIT_MS,
    intervalMs: POLL_INTERVAL_MS,
  });

  if (finished) {
    // waitForCommand already returned the fresh row, but ack_message_id was
    // written after enqueue so patch it in.
    await deliverCommandResult({ ...finished, ack_message_id: ackMessageId });
    return true;
  }

  // Still 'pending' after the inline wait means no add-in claimed it in ten
  // poll intervals — Revit is closed or not polling. Saying so now beats
  // leaving the user on "waiting for the add-in" until the next sweep.
  const current = await getCommand(commandId);
  if (current?.status === 'pending') {
    await telegram().sendMessage(
      chatId,
      formatError(ctx, 'errors.addin_not_polling', {
        seconds: Math.round(INLINE_WAIT_MS / 1000),
        poll: env.pollingIntervalSeconds,
      }),
    );
  }
  // Processing, or finished late: /api/telegram/callback delivers it.

  return false;
}

/**
 * A tap on a confirm or cancel button.
 *
 * Both outcomes are single-shot at the database, scoped to the asking user and
 * to the awaiting state, because a button stays tappable in chat history for as
 * long as the message exists.
 */
async function handleConfirmation(
  query: CallbackQuery,
  confirmed: boolean,
): Promise<Response> {
  const chatId = query.message?.chat.id;
  const done = (notice?: string) =>
    telegram()
      .answerCallbackQuery(query.id, notice)
      .catch((error) => console.error('[webhook] answerCallbackQuery failed', error));

  const user = await findUserByTelegramId(query.from.id);
  if (!user || chatId === undefined) {
    await done();
    return ok({ ok: true, ignored: 'unregistered' });
  }

  const ctx: FormatContext = { language: user.language, theme: user.theme };
  const t = translator(ctx.language);
  const prefix = confirmed ? CONFIRM_CALLBACK : CANCEL_CALLBACK;
  const commandId = (query.data ?? '').slice(prefix.length);

  const command = confirmed
    ? await confirmCommand(commandId, user.id)
    : await cancelCommand(commandId, user.id);

  if (!command) {
    // Already answered, expired, or someone else's button.
    await done(t('confirm.expired'));
    return ok({ ok: true, ignored: 'not awaiting confirmation' });
  }

  if (!confirmed) {
    await done(t('confirm.cancelled'));
    if (query.message) {
      await telegram()
        .editMessageText(chatId, query.message.message_id, formatCancelled(ctx, command.command_type))
        .catch(() => undefined);
    }
    return ok({ ok: true, cancelled: commandId });
  }

  await done(t('confirm.confirmed'));

  // The confirmation bubble becomes the acknowledgement, and then the result,
  // so the whole exchange stays in one message.
  const ack = formatAck(ctx, {
    commandType: command.command_type,
    pollSeconds: env.pollingIntervalSeconds,
  });

  let ackMessageId = query.message?.message_id;
  if (ackMessageId) {
    await telegram().editMessageText(chatId, ackMessageId, ack).catch(() => undefined);
  } else {
    ackMessageId = (await telegram().sendMessage(chatId, ack)).message_id;
  }

  await awaitAndDeliver(ctx, chatId, command.id, ackMessageId);
  return ok({ ok: true, confirmed: commandId });
}

/**
 * Turns /undo into the delete that reverses the last placement.
 *
 * Aimed at marks rather than at a room and a category, so an undo takes back
 * what that command placed and leaves alone anything a colleague added to the
 * same room in between.
 */
async function resolveUndo(
  user: User,
  ctx: FormatContext,
): Promise<{ command: ParsedCommand } | { error: string }> {
  const access = await resolveActiveProject(user);
  if (!access) return { error: formatError(ctx, 'errors.no_active_project') };

  const last = await findLastPlacement(user.id, access.project.id);
  const placement = last ? placementOf(last) : null;

  if (!last || !placement) {
    return { error: formatError(ctx, 'errors.nothing_to_undo') };
  }

  return {
    command: {
      type: 'delete_devices',
      subject: placement.room,
      params: {
        what: placement.kind,
        marks: placement.deviceIds.join(','),
      },
      source: 'grammar',
      raw: `/undo → ${placement.kind} ${placement.deviceIds.join(',')}`,
    },
  };
}

/** The "you cancelled it" message, so nobody wonders whether it half-ran. */
function formatCancelled(ctx: FormatContext, commandType: string): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);
  b.title(t('common.warning'), t('confirm.cancelled_title'));
  b.tree([{ label: t('ack.command'), value: commandType }]);
  b.blank().text(t('confirm.cancelled_detail'));
  return b.build();
}

/**
 * A tap on the project picker.
 *
 * Selecting the project here rather than in the add-in means one Revit instance
 * can serve whichever project the user is working on, without anyone touching a
 * config file on the machine running Revit.
 */
async function handleCallbackQuery(query: CallbackQuery): Promise<Response> {
  const data = query.data ?? '';
  const chatId = query.message?.chat.id;

  // Telegram shows a spinner until this is answered, so do it whatever happens.
  const done = (notice?: string) =>
    telegram()
      .answerCallbackQuery(query.id, notice)
      .catch((error) => console.error('[webhook] answerCallbackQuery failed', error));

  if (!data.startsWith(PROJECT_CALLBACK) || chatId === undefined) {
    await done();
    return ok({ ok: true, ignored: 'unrecognised callback' });
  }

  const user = await findUserByTelegramId(query.from.id);
  if (!user) {
    await done();
    return ok({ ok: true, ignored: 'unregistered' });
  }

  const ctx: FormatContext = { language: user.language, theme: user.theme };
  const projectId = data.slice(PROJECT_CALLBACK.length);

  // A button from an old message can name a project whose access has since been
  // revoked, so re-check it rather than trusting the payload.
  const access = await accessForProjectOrAdmin(user, projectId);
  if (!access) {
    await done();
    await telegram().sendMessage(chatId, formatError(ctx, 'errors.no_project_access'));
    return ok({ ok: true, ignored: 'no access' });
  }

  await setActiveProject(user.id, projectId);

  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);
  b.title(t('common.success'), `${t('admin.project_switched')} ${access.project.code}`);
  b.tree([
    { label: access.project.name, value: access.role },
    ...(access.project.location ? [{ label: 'Location', value: access.project.location }] : []),
  ]);

  await done(access.project.code);

  // Editing the original message replaces the picker with the outcome, so the
  // buttons cannot be tapped again from scrollback.
  if (query.message) {
    await telegram()
      .editMessageText(chatId, query.message.message_id, b.build())
      .catch(async () => {
        await telegram().sendMessage(chatId, b.build());
      });
  } else {
    await telegram().sendMessage(chatId, b.build());
  }

  return ok({ ok: true, active_project: projectId });
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

  if (update.callback_query) {
    const data = update.callback_query.data ?? '';
    if (data.startsWith(CONFIRM_CALLBACK)) {
      return handleConfirmation(update.callback_query, true);
    }
    if (data.startsWith(CANCEL_CALLBACK)) {
      return handleConfirmation(update.callback_query, false);
    }
    return handleCallbackQuery(update.callback_query);
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
    // a comprehensible message rather than silence. Include their numeric id —
    // registration is an admin INSERT keyed on it, and making someone hunt for
    // it via a third-party bot is a pointless extra step.
    const ctx: FormatContext = { language: 'id', theme: 'light' };
    const t = translator(ctx.language);
    await telegram().sendMessage(
      chatId,
      formatError(ctx, 'errors.not_registered', {}, t('errors.your_telegram_id', { id: from.id })),
    );
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
    // One message for several causes used to make a rejected API key look
    // identical to a phrasing Claude was unsure about — and they need very
    // different fixes from the person reading it.
    const key = {
      unknown_command: 'errors.unknown_command',
      not_a_device_command: 'errors.not_a_device_command',
      nlp_unavailable: 'errors.nlp_unavailable',
      low_confidence: 'errors.low_confidence',
      nlp_error: 'errors.nlp_error',
    }[outcome.reason];

    await telegram().sendMessage(
      chatId,
      formatError(ctx, key, { command: text.split(/\s+/)[0] ?? text }, outcome.detail),
    );
    return ok();
  }

  // --- 4a. Admin commands ---------------------------------------------------
  if (outcome.kind === 'admin') {
    try {
      const reply = await handleAdminCommand({ ...ctx, user }, outcome.admin);
      if (reply.languageChangedTo) ctx = { ...ctx, language: reply.languageChangedTo };
      if (reply.themeChangedTo) ctx = { ...ctx, theme: reply.themeChangedTo };
      await telegram().sendMessage(chatId, reply.text, {
        ...(reply.keyboard ? { replyMarkup: reply.keyboard } : {}),
      });
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
  let command = outcome.command;

  // /undo is not a command the add-in knows. It is a delete aimed at exactly
  // the marks the last placement reported, resolved here where the queue
  // history lives, and then run through everything below — role check,
  // validation, confirmation — like any other delete.
  if (command.type === 'undo') {
    const resolved = await resolveUndo(user, ctx);
    if ('error' in resolved) {
      await telegram().sendMessage(chatId, resolved.error);
      return ok({ ok: true, ignored: 'nothing to undo' });
    }
    command = resolved.command;
  }

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
  const needsConfirmation = NEEDS_CONFIRMATION.has(command.type);

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
      ...(needsConfirmation ? { status: 'awaiting_confirmation' as const } : {}),
    });
  } catch (error) {
    console.error('[webhook] enqueue failed', error);
    await telegram().sendMessage(
      chatId,
      formatError(ctx, 'common.unknown_error', {}, String(error)),
    );
    return ok();
  }

  // A destructive command stops here and asks. The row exists but the add-in
  // cannot see it, so nothing happens in Revit unless the button is tapped.
  if (needsConfirmation) {
    await telegram().sendMessage(
      chatId,
      formatConfirmation(ctx, command.type, validation.normalized),
      {
        replyToMessageId: message.message_id,
        replyMarkup: {
          inline_keyboard: [
            [
              { text: translator(ctx.language)('confirm.yes'), callback_data: `${CONFIRM_CALLBACK}${enqueued.command.id}` },
              { text: translator(ctx.language)('confirm.no'), callback_data: `${CANCEL_CALLBACK}${enqueued.command.id}` },
            ],
          ],
        },
      },
    );
    return ok({ ok: true, command_id: enqueued.command.id, awaiting_confirmation: true });
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

  const delivered = await awaitAndDeliver(ctx, chatId, enqueued.command.id, ack.message_id);
  return ok({ ok: true, command_id: enqueued.command.id, delivered });
}

/**
 * The question asked before a destructive command runs.
 *
 * States the room and the category back, because the failure this is guarding
 * against is a room name that resolved to the wrong room — and the only person
 * who can catch that is the one who typed it.
 */
function formatConfirmation(
  ctx: FormatContext,
  commandType: string,
  params: Record<string, string | number | boolean>,
): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.warning'), t(`confirm.${commandType}`));

  const rows = [{ label: t('ack.command'), value: commandType }];
  if (params.room) rows.push({ label: t('common.room'), value: String(params.room) });
  if (params.what) rows.push({ label: t('confirm.what'), value: String(params.what) });
  if (params.grid) rows.push({ label: t('lighting.grid'), value: String(params.grid) });
  else if (params.count) rows.push({ label: t('confirm.count'), value: String(params.count) });

  // /undo names the marks it will remove: the whole point is that it takes back
  // exactly those and nothing else, and that is checkable at a glance.
  if (typeof params.marks === 'string' && params.marks !== '') {
    const marks = params.marks.split(',');
    rows.push({
      label: t('confirm.marks'),
      value: marks.slice(0, 12).join(', ') + (marks.length > 12 ? ` (+${marks.length - 12})` : ''),
    });
  }

  b.tree(rows);
  b.blank().text(t('confirm.prompt'));

  return b.build();
}

/** Health probe for the webhook URL itself. */
export function GET(): Response {
  return ok({ ok: true, endpoint: 'telegram/message', method: 'POST' });
}
