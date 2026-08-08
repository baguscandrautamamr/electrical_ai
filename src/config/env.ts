/**
 * Environment access.
 *
 * Every secret is read through here so a missing variable fails loudly at the
 * edge of the request instead of surfacing as a confusing 401 from Supabase or
 * Telegram three call frames deep.
 */

export class MissingEnvError extends Error {
  constructor(name: string) {
    super(
      `Missing required environment variable: ${name}. ` +
        `Set it in .env.local for local dev, or in Vercel -> Settings -> Environment Variables.`,
    );
    this.name = 'MissingEnvError';
  }
}

function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim() === '') throw new MissingEnvError(name);
  return value.trim();
}

function optional(name: string, fallback = ''): string {
  const value = process.env[name];
  return value && value.trim() !== '' ? value.trim() : fallback;
}

function numeric(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw || raw.trim() === '') return fallback;
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : fallback;
}

/** Anthropic's own API. Anything else is a gateway or a proxy. */
export const DEFAULT_ANTHROPIC_BASE_URL = 'https://api.anthropic.com';

export const env = {
  get anthropicApiKey(): string {
    return required('ANTHROPIC_API_KEY');
  },
  get anthropicModel(): string {
    return optional('ANTHROPIC_MODEL', 'claude-opus-5');
  },
  /**
   * Where to send Claude requests. Leave unset for Anthropic itself; point it
   * at an Anthropic-compatible gateway to bill through a reseller. A gateway
   * key sent to api.anthropic.com is rejected as `invalid x-api-key`, which
   * reads like a bad key rather than a request that went to the wrong host.
   */
  get anthropicBaseUrl(): string {
    return optional('ANTHROPIC_BASE_URL', DEFAULT_ANTHROPIC_BASE_URL).replace(/\/+$/, '');
  },
  get telegramBotToken(): string {
    return required('TELEGRAM_BOT_TOKEN');
  },
  get telegramWebhookSecret(): string {
    return optional('TELEGRAM_WEBHOOK_SECRET');
  },
  get supabaseUrl(): string {
    return required('SUPABASE_URL').replace(/\/+$/, '');
  },
  get supabaseAnonKey(): string {
    return required('SUPABASE_ANON_KEY');
  },
  get supabaseServiceRoleKey(): string {
    return required('SUPABASE_SERVICE_ROLE_KEY');
  },
  get cronSecret(): string {
    return required('CRON_SECRET');
  },
  get pollingIntervalSeconds(): number {
    return numeric('POLLING_INTERVAL_SECONDS', 4);
  },
  get maxCommandRetry(): number {
    return numeric('MAX_COMMAND_RETRY', 3);
  },
  get commandTimeoutSeconds(): number {
    return numeric('COMMAND_TIMEOUT_SECONDS', 120);
  },
} as const;

/** True when the Claude NLP fallback can be used. */
export function hasAnthropicKey(): boolean {
  return Boolean(process.env.ANTHROPIC_API_KEY?.trim());
}
