/**
 * Small builder for the tree-style Telegram messages this bot sends.
 *
 * Keeps the box-drawing glyphs in one place so light/dark theming and HTML
 * escaping are applied consistently instead of being re-implemented per device.
 */

import { escapeHtml } from '../lib/telegram.js';
import { themeGlyphs, type ThemeGlyphs } from '../theme/tokens.js';
import type { Theme } from '../types/index.js';

export interface Row {
  label: string;
  value: string;
  /** Rendered one level deeper. */
  indent?: number;
}

export class MessageBuilder {
  private readonly lines: string[] = [];
  private readonly glyphs: ThemeGlyphs;

  constructor(theme: Theme) {
    this.glyphs = themeGlyphs(theme);
  }

  /** Bold headline, e.g. "✅ CABLE TRAY CREATED". */
  title(emoji: string, text: string): this {
    this.lines.push(`${emoji} <b>${escapeHtml(text)}</b>`);
    return this;
  }

  /** A bold sub-heading with a blank line above it. */
  section(text: string): this {
    this.lines.push('');
    this.lines.push(`<b>${escapeHtml(text)}</b>`);
    return this;
  }

  text(value: string): this {
    this.lines.push(escapeHtml(value));
    return this;
  }

  raw(html: string): this {
    this.lines.push(html);
    return this;
  }

  blank(): this {
    this.lines.push('');
    return this;
  }

  /**
   * Renders a list as a box-drawing tree; the final entry gets the corner glyph.
   */
  tree(rows: Row[]): this {
    rows.forEach((row, index) => {
      const isLast = index === rows.length - 1;
      const glyph = isLast ? this.glyphs.leaf : this.glyphs.branch;
      const pad = '   '.repeat(row.indent ?? 0);
      const label = escapeHtml(row.label);
      const value = escapeHtml(row.value);
      this.lines.push(value === '' ? `${pad}${glyph} ${label}` : `${pad}${glyph} ${label}: ${value}`);
    });
    return this;
  }

  bullets(items: string[]): this {
    for (const item of items) {
      this.lines.push(`${this.glyphs.bullet} ${escapeHtml(item)}`);
    }
    return this;
  }

  /** A ✓/✗ compliance line. */
  check(passed: boolean, label: string, detail?: string): this {
    const mark = passed ? this.glyphs.check : this.glyphs.cross;
    const suffix = detail ? ` (${escapeHtml(detail)})` : '';
    this.lines.push(`${this.glyphs.bullet} ${escapeHtml(label)} ${mark}${suffix}`);
    return this;
  }

  /** Renders links as an inline row, skipping any that are absent. */
  links(entries: Array<[label: string, url: string | undefined]>): this {
    const rendered = entries
      .filter((entry): entry is [string, string] => Boolean(entry[1]))
      .map(([label, url]) => `<a href="${escapeHtml(url)}">${escapeHtml(label)}</a>`);
    if (rendered.length > 0) this.lines.push(rendered.join(' · '));
    return this;
  }

  code(value: string): this {
    this.lines.push(`<code>${escapeHtml(value)}</code>`);
    return this;
  }

  build(): string {
    // Collapse runs of blank lines and trim the ends; Telegram renders stray
    // blank lines literally.
    const out: string[] = [];
    for (const line of this.lines) {
      if (line === '' && (out.length === 0 || out[out.length - 1] === '')) continue;
      out.push(line);
    }
    while (out.length > 0 && out[out.length - 1] === '') out.pop();
    return out.join('\n');
  }
}

/** Formats a number without trailing `.0` noise. */
export function num(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '-';
  const rounded = Number(value.toFixed(digits));
  return String(rounded);
}
