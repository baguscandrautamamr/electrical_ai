/**
 * Rendering for standards mode.
 *
 * Every message this module produces carries the mode marker. That is not
 * decoration: the mode is sticky, and a person who cannot tell at a glance
 * which conversation they are in is the person who types a placement command
 * expecting it to run. The banner on entry, the marker on every answer and the
 * explicit confirmation on exit are three chances to notice.
 */

import { translator } from '../i18n/index.js';
import { MessageBuilder } from './message.js';
import type { FormatContext } from './index.js';

/** Shown on every standards message, so the mode is never in doubt. */
const MARKER = '📘';

/** The banner that opens a session. */
export function formatStandardsEnter(ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(MARKER, t('standards.mode_on'));
  b.blank().text(t('standards.mode_on_detail'));
  b.bullets([t('standards.mode_on_scope'), t('standards.mode_on_safety')]);
  b.blank().text(t('standards.mode_on_exit'));

  return b.build();
}

/** Confirmation that command parsing is live again. */
export function formatStandardsExit(ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.success'), t('standards.mode_off'));
  b.blank().text(t('standards.mode_off_detail'));

  return b.build();
}

/** The same message the idle sweep produces, so a timeout is never silent. */
export function formatStandardsTimedOut(ctx: FormatContext): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.warning'), t('standards.mode_expired'));
  b.blank().text(t('standards.mode_expired_detail'));

  return b.build();
}

/**
 * A Revit command typed while in standards mode.
 *
 * Refused rather than answered as a question. Someone typing
 * `/place_lighting Office_A count=6` wants it to run, and telling them it did
 * not — naming the way out — beats an essay about lighting standards.
 */
export function formatStandardsBlocked(ctx: FormatContext, command: string): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(t('common.warning'), t('standards.blocked'));
  b.tree([{ label: t('ack.command'), value: command }]);
  b.blank().text(t('standards.blocked_detail'));

  return b.build();
}

/** An answer, with its sources and the standing disclaimer. */
export function formatStandardsAnswer(
  ctx: FormatContext,
  answer: { text: string; sources: string[] },
  disclaimerText: string,
): string {
  const t = translator(ctx.language);
  const b = new MessageBuilder(ctx.theme);

  b.title(MARKER, t('standards.answer_title'));
  b.blank();

  // Claude replies in plain text with "- " bullets. Rendered line by line so
  // MessageBuilder escapes each one; anything else would put unescaped model
  // output into an HTML-parsed Telegram message.
  for (const line of answer.text.split('\n')) {
    const trimmed = line.trim();
    if (trimmed === '') {
      b.blank();
    } else if (trimmed.startsWith('- ') || trimmed.startsWith('• ')) {
      b.bullets([trimmed.slice(2).trim()]);
    } else {
      b.text(trimmed);
    }
  }

  if (answer.sources.length > 0) {
    b.section(t('standards.sources'));
    b.text([...new Set(answer.sources)].join(' · '));
  }

  b.blank().text(`ⓘ ${disclaimerText}`);
  b.text(t('standards.footer_exit'));

  return b.build();
}
