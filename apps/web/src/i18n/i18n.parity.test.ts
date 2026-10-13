import { describe, expect, it } from 'vitest';
import { resources } from './resources';

const PLURAL_SUFFIX = /_(zero|one|two|few|many|other)$/;
const ARABIC_PLURAL_FORMS = ['zero', 'one', 'two', 'few', 'many', 'other'];

function flatten(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) return [prefix];
  return Object.entries(value).flatMap(([key, child]) => flatten(child, prefix ? `${prefix}.${key}` : key));
}

function baseKeys(keys: string[]): string[] {
  return [...new Set(keys.map((key) => key.replace(PLURAL_SUFFIX, '')))].sort();
}

describe('translations', () => {
  it('has the same namespaces in Arabic and English', () => {
    expect(Object.keys(resources.ar).sort()).toEqual(Object.keys(resources.en).sort());
  });

  for (const namespace of Object.keys(resources.en) as Array<keyof typeof resources.en>) {
    it(`has the same keys in Arabic and English: ${namespace}`, () => {
      expect(baseKeys(flatten(resources.ar[namespace]))).toEqual(baseKeys(flatten(resources.en[namespace])));
    });

    it(`defines all six Arabic plural forms: ${namespace}`, () => {
      const keys = flatten(resources.ar[namespace]);
      for (const base of new Set(keys.filter((key) => PLURAL_SUFFIX.test(key)).map((key) => key.replace(PLURAL_SUFFIX, '')))) {
        for (const form of ARABIC_PLURAL_FORMS) expect(keys).toContain(`${base}_${form}`);
      }
    });

    it(`has no empty strings: ${namespace}`, () => {
      for (const locale of ['ar', 'en'] as const) {
        const json = JSON.stringify(resources[locale][namespace]);
        expect(json).not.toMatch(/:""/);
      }
    });
  }
});
