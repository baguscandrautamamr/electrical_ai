/**
 * The bot's command menu — what appears behind the button beside the chat
 * input, and the only thing that tells a new user the bot understands anything
 * at all. Telegram does not discover commands on its own.
 *
 * The list lives in `bot-menu.json` rather than in this module because
 * `scripts/telegram-webhook.mjs` is plain Node and cannot import TypeScript.
 * One file, both readers. `tests/bot-menu.test.ts` fails if it drifts from the
 * commands the parser actually accepts.
 */

import menu from './bot-menu.json' with { type: 'json' };
import type { BotCommand } from '../lib/telegram.js';

export const botCommands: readonly BotCommand[] = menu;
