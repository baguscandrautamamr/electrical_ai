/** Telegram Bot API client (only the handful of methods this bot uses). */

import { env } from '../config/env.js';

export interface TelegramUser {
  id: number;
  is_bot: boolean;
  first_name: string;
  last_name?: string;
  username?: string;
  language_code?: string;
}

export interface TelegramChat {
  id: number;
  type: 'private' | 'group' | 'supergroup' | 'channel';
  title?: string;
  username?: string;
}

export interface TelegramMessage {
  message_id: number;
  from?: TelegramUser;
  chat: TelegramChat;
  date: number;
  text?: string;
}

/** A tappable button under a message. `callback_data` is capped at 64 bytes. */
export interface InlineKeyboardButton {
  text: string;
  callback_data: string;
}

export interface InlineKeyboardMarkup {
  inline_keyboard: InlineKeyboardButton[][];
}

/** Delivered when a user taps an inline button. */
export interface CallbackQuery {
  id: string;
  from: TelegramUser;
  message?: TelegramMessage;
  data?: string;
}

export interface TelegramUpdate {
  update_id: number;
  message?: TelegramMessage;
  edited_message?: TelegramMessage;
  callback_query?: CallbackQuery;
}

export class TelegramError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: string,
  ) {
    super(message);
    this.name = 'TelegramError';
  }
}

export interface SendMessageOptions {
  parseMode?: 'MarkdownV2' | 'HTML';
  disableWebPagePreview?: boolean;
  replyToMessageId?: number;
  replyMarkup?: InlineKeyboardMarkup;
}

/** getWebhookInfo. `last_error_message` is where a 403 or a crash surfaces. */
export interface SendDocumentOptions {
  caption?: string;
  replyToMessageId?: number;
  /** Deliver without a notification sound. Defaults to true. */
  silent?: boolean;
}

export interface WebhookInfo {
  url: string;
  pending_update_count?: number;
  allowed_updates?: string[];
  last_error_message?: string;
  last_error_date?: number;
}

/** One entry in the bot's command menu. Telegram caps description at 256. */
export interface BotCommand {
  /** Without the leading slash: lowercase letters, digits and underscores. */
  command: string;
  description: string;
}

export class TelegramClient {
  private readonly base: string;

  constructor(token = env.telegramBotToken) {
    this.base = `https://api.telegram.org/bot${token}`;
  }

  private async call<T>(method: string, payload: Record<string, unknown>): Promise<T> {
    const response = await fetch(`${this.base}/${method}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });

    const text = await response.text();
    if (!response.ok) {
      throw new TelegramError(`Telegram ${method} failed (${response.status})`, response.status, text);
    }

    const parsed = JSON.parse(text) as { ok: boolean; result: T; description?: string };
    if (!parsed.ok) {
      throw new TelegramError(
        `Telegram ${method} returned ok=false: ${parsed.description ?? 'unknown'}`,
        response.status,
        text,
      );
    }
    return parsed.result;
  }

  async sendMessage(
    chatId: number,
    text: string,
    options: SendMessageOptions = {},
  ): Promise<TelegramMessage> {
    return this.call<TelegramMessage>('sendMessage', {
      chat_id: chatId,
      text,
      parse_mode: options.parseMode ?? 'HTML',
      disable_web_page_preview: options.disableWebPagePreview ?? true,
      ...(options.replyToMessageId ? { reply_to_message_id: options.replyToMessageId } : {}),
      ...(options.replyMarkup ? { reply_markup: options.replyMarkup } : {}),
    });
  }

  /**
   * Sends a file by URL.
   *
   * Telegram fetches the URL itself, which is the only way a drawing written on
   * the machine running Revit reaches a phone — this process never sees the
   * bytes. It caps a document fetched this way at 20 MB and gives up after a
   * few seconds, so the URL has to be public and quick; Supabase Storage is
   * both, and is where the add-in puts them.
   */
  async sendDocument(
    chatId: number,
    documentUrl: string,
    options: SendDocumentOptions = {},
  ): Promise<TelegramMessage> {
    return this.call<TelegramMessage>('sendDocument', {
      chat_id: chatId,
      document: documentUrl,
      ...(options.caption ? { caption: options.caption, parse_mode: 'HTML' } : {}),
      ...(options.replyToMessageId ? { reply_to_message_id: options.replyToMessageId } : {}),
      // The reply that names the sheets has already arrived; a second
      // notification per file turns one print into a buzzing phone.
      disable_notification: options.silent ?? true,
    });
  }

  async editMessageText(
    chatId: number,
    messageId: number,
    text: string,
    options: SendMessageOptions = {},
  ): Promise<void> {
    await this.call('editMessageText', {
      chat_id: chatId,
      message_id: messageId,
      text,
      parse_mode: options.parseMode ?? 'HTML',
      disable_web_page_preview: options.disableWebPagePreview ?? true,
      // Always sent: omitting it leaves a stale keyboard under an edited
      // message, so a tapped picker would stay tappable.
      reply_markup: options.replyMarkup ?? { inline_keyboard: [] },
    });
  }

  /**
   * Clears the spinner Telegram shows on a tapped button. Required within a few
   * seconds or the client reports the tap as failed, however well it worked.
   */
  async answerCallbackQuery(callbackQueryId: string, text?: string): Promise<void> {
    await this.call('answerCallbackQuery', {
      callback_query_id: callbackQueryId,
      ...(text ? { text } : {}),
    });
  }

  async setWebhook(url: string, secretToken?: string): Promise<void> {
    await this.call('setWebhook', {
      url,
      ...(secretToken ? { secret_token: secretToken } : {}),
      // callback_query included: the project picker is inline buttons, and a
      // tap that Telegram is not told to deliver simply never arrives.
      allowed_updates: ['message', 'edited_message', 'callback_query'],
    });
  }

  /**
   * Populates the command menu — the button beside the chat input that lists
   * what the bot understands. Without it a new user faces an empty text box
   * with no indication that /help exists.
   *
   * Telegram scopes menus by language; passing none makes this the default for
   * every user.
   */
  async setMyCommands(commands: BotCommand[], languageCode?: string): Promise<void> {
    await this.call('setMyCommands', {
      commands,
      ...(languageCode ? { language_code: languageCode } : {}),
    });
  }

  /** What Telegram believes about the webhook — the first thing to read when the bot is silent. */
  async getWebhookInfo(): Promise<WebhookInfo> {
    return this.call<WebhookInfo>('getWebhookInfo', {});
  }

  async getMe(): Promise<TelegramUser> {
    return this.call<TelegramUser>('getMe', {});
  }
}

let cached: TelegramClient | null = null;

export function telegram(): TelegramClient {
  cached ??= new TelegramClient();
  return cached;
}

export function __setTelegramClient(client: TelegramClient | null): void {
  cached = client;
}

/**
 * Escapes text for Telegram's HTML parse mode.
 *
 * Only these three characters are special; escaping more (as MarkdownV2 would
 * require) mangles ordinary engineering text like `150x100mm` and `PA-01`.
 */
export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
