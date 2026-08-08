import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { t, translator, flattenKeys, bundleFor, isLanguage } from '../src/i18n/index.js';
import { isTheme, themeCss, themeGlyphs, THEMES } from '../src/theme/tokens.js';

describe('translation bundles', () => {
  it('defines exactly the same keys in both languages', () => {
    // A key present in one bundle but not the other silently degrades to the
    // raw key in production, which looks like a bug to the user.
    const idKeys = flattenKeys(bundleFor('id'));
    const enKeys = flattenKeys(bundleFor('en'));

    const missingInEn = idKeys.filter((key) => !enKeys.includes(key));
    const missingInId = enKeys.filter((key) => !idKeys.includes(key));

    expect(missingInEn, `missing from en.json: ${missingInEn.join(', ')}`).toEqual([]);
    expect(missingInId, `missing from id.json: ${missingInId.join(', ')}`).toEqual([]);
  });

  it('leaves no value empty', () => {
    for (const language of ['id', 'en'] as const) {
      const bundle = bundleFor(language);
      for (const key of flattenKeys(bundle)) {
        expect(t(language, key).trim(), `${language}.${key} is empty`).not.toBe('');
      }
    }
  });

  it('uses the same placeholders in both languages', () => {
    // "{min}" appearing in one language but not the other produces a message
    // with a literal brace or a missing number.
    const placeholders = (text: string) =>
      [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

    for (const key of flattenKeys(bundleFor('id'))) {
      expect(placeholders(t('id', key)), `placeholder mismatch on ${key}`).toEqual(
        placeholders(t('en', key)),
      );
    }
  });

  it('keeps the add-in resource copies byte-identical to src/i18n', () => {
    // The Revit add-in ships its own copy; drift would mean the two halves of
    // the system disagree about what a message says.
    for (const language of ['id', 'en'] as const) {
      const source = readFileSync(new URL(`../src/i18n/${language}.json`, import.meta.url), 'utf8');
      const shipped = readFileSync(
        new URL(
          `../revit-addin/RevitCommandCenter.Electrical/Resources/translations/${language}.json`,
          import.meta.url,
        ),
        'utf8',
      );

      expect(shipped, `${language}.json is out of sync; run npm run sync:i18n`).toBe(source);
    }
  });
});

describe('t()', () => {
  it('resolves a dotted key', () => {
    expect(t('en', 'cable_tray.created')).toBe('CABLE TRAY CREATED');
    expect(t('id', 'cable_tray.created')).toBe('KABEL TRAY DIBUAT');
  });

  it('interpolates variables', () => {
    expect(t('en', 'validation.out_of_range', { min: 100, max: 6000 })).toBe(
      'must be between 100 and 6000',
    );
  });

  it('leaves an unsupplied placeholder untouched rather than printing undefined', () => {
    expect(t('en', 'validation.out_of_range', { min: 1 })).toBe('must be between 1 and {max}');
  });

  it('returns the key itself when it is unknown', () => {
    expect(t('en', 'nope.not_here')).toBe('nope.not_here');
  });

  it('falls back to Indonesian for an unknown language', () => {
    expect(t('fr' as never, 'cable_tray.created')).toBe('KABEL TRAY DIBUAT');
  });

  it('curries via translator()', () => {
    const tr = translator('en');
    expect(tr('common.total')).toBe('Total');
  });
});

describe('language and theme guards', () => {
  it('accepts only supported languages', () => {
    expect(isLanguage('id')).toBe(true);
    expect(isLanguage('en')).toBe(true);
    expect(isLanguage('fr')).toBe(false);
  });

  it('accepts only supported themes', () => {
    expect(isTheme('light')).toBe(true);
    expect(isTheme('dark')).toBe(true);
    expect(isTheme('sepia')).toBe(false);
  });
});

describe('theme tokens', () => {
  it('defines every token in both themes', () => {
    const lightKeys = Object.keys(THEMES.light).sort();
    const darkKeys = Object.keys(THEMES.dark).sort();
    expect(lightKeys).toEqual(darkKeys);
  });

  it('keeps the amber primary consistent across themes', () => {
    expect(THEMES.light.colorPrimary).toBe('#F9C519');
    expect(THEMES.dark.colorPrimary).toBe('#F9C519');
  });

  it('emits a scoped CSS block with the glass-morphism card', () => {
    const css = themeCss('light');

    expect(css).toContain(':root[data-theme="light"]');
    expect(css).toContain('--color-primary: #F9C519;');
    expect(css).toContain('backdrop-filter: var(--bg-card-backdrop)');
  });

  it('gives each theme a full glyph set', () => {
    for (const theme of ['light', 'dark'] as const) {
      const glyphs = themeGlyphs(theme);
      for (const value of Object.values(glyphs)) {
        expect(value).not.toBe('');
      }
    }
  });
});
