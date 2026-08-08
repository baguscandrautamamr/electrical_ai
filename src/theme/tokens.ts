/**
 * Dual theme: Apple-style glass morphism (light) and dark.
 *
 * Telegram messages cannot carry CSS, so the theme drives two things here:
 *   1. `themeGlyphs` — the small visual vocabulary used in bot replies.
 *   2. `themeCss`    — the variable block emitted for any HTML surface
 *                      (PDF report headers, exported dashboards, future web UI).
 */

import type { Theme } from '../types/index.ts';

export interface ThemeTokens {
  bgPrimary: string;
  bgSecondary: string;
  bgCard: string;
  bgCardBackdrop: string;
  textPrimary: string;
  textSecondary: string;
  colorPrimary: string;
  colorSuccess: string;
  colorDanger: string;
  colorWarning: string;
  borderColor: string;
  shadow: string;
}

export const THEMES: Record<Theme, ThemeTokens> = {
  light: {
    bgPrimary: '#f5f5f7',
    bgSecondary: '#ffffff',
    bgCard: 'rgba(255, 255, 255, 0.6)',
    bgCardBackdrop: 'blur(20px)',
    textPrimary: '#1d1d1f',
    textSecondary: '#86868b',
    colorPrimary: '#F9C519',
    colorSuccess: '#00a854',
    colorDanger: '#d32f2f',
    colorWarning: '#ff7a45',
    borderColor: 'rgba(0, 0, 0, 0.1)',
    shadow: '0 2px 8px rgba(0, 0, 0, 0.1)',
  },
  dark: {
    bgPrimary: '#1d1d1f',
    bgSecondary: '#424245',
    bgCard: 'rgba(255, 255, 255, 0.08)',
    bgCardBackdrop: 'blur(20px)',
    textPrimary: '#f5f5f7',
    textSecondary: '#a1a1a6',
    colorPrimary: '#F9C519',
    colorSuccess: '#00a854',
    colorDanger: '#ff5252',
    colorWarning: '#ff7a45',
    borderColor: 'rgba(255, 255, 255, 0.1)',
    shadow: '0 2px 8px rgba(0, 0, 0, 0.4)',
  },
};

export const DEFAULT_THEME: Theme = 'light';

export function isTheme(value: string): value is Theme {
  return value === 'light' || value === 'dark';
}

/**
 * Glyph set per theme. Light uses filled/heavier marks that read well on a
 * white bubble; dark uses lighter outline marks.
 */
export interface ThemeGlyphs {
  branch: string;
  leaf: string;
  bullet: string;
  section: string;
  check: string;
  cross: string;
}

const GLYPHS: Record<Theme, ThemeGlyphs> = {
  light: { branch: '├─', leaf: '└─', bullet: '•', section: '▸', check: '✓', cross: '✗' },
  dark: { branch: '├─', leaf: '└─', bullet: '◦', section: '▹', check: '✓', cross: '✗' },
};

export function themeGlyphs(theme: Theme): ThemeGlyphs {
  return GLYPHS[theme] ?? GLYPHS[DEFAULT_THEME];
}

/** CSS custom-property block for HTML surfaces. */
export function themeCss(theme: Theme): string {
  const tokens = THEMES[theme] ?? THEMES[DEFAULT_THEME];
  const vars = [
    `--bg-primary: ${tokens.bgPrimary};`,
    `--bg-secondary: ${tokens.bgSecondary};`,
    `--bg-card: ${tokens.bgCard};`,
    `--bg-card-backdrop: ${tokens.bgCardBackdrop};`,
    `--text-primary: ${tokens.textPrimary};`,
    `--text-secondary: ${tokens.textSecondary};`,
    `--color-primary: ${tokens.colorPrimary};`,
    `--color-success: ${tokens.colorSuccess};`,
    `--color-danger: ${tokens.colorDanger};`,
    `--color-warning: ${tokens.colorWarning};`,
    `--border-color: ${tokens.borderColor};`,
    `--shadow: ${tokens.shadow};`,
  ].join('\n  ');

  return `:root[data-theme="${theme}"] {\n  ${vars}\n}\n\n.card {\n  background: var(--bg-card);\n  backdrop-filter: var(--bg-card-backdrop);\n  border: 1px solid var(--border-color);\n  border-radius: 12px;\n  padding: 16px;\n  box-shadow: var(--shadow);\n}\n`;
}
