import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import i18next from 'eslint-plugin-i18next';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// Physical (non-logical) Tailwind direction classes. Use ms-/me-/ps-/pe-/start-/end-/text-start/... instead,
// so the same component works in RTL and LTR.
const PHYSICAL_DIRECTION_CLASS =
  '/(^|\\s|:)-?(ml|mr|pl|pr|left|right|border-l|border-r|rounded-l|rounded-r|rounded-tl|rounded-tr|rounded-bl|rounded-br|scroll-ml|scroll-mr|scroll-pl|scroll-pr)-|(^|\\s|:)(text-left|text-right|float-left|float-right|border-l|border-r)(\\s|$)/';

// Raw colors belong in src/styles/tokens.css only; components consume semantic tokens.
const RAW_COLOR = '/#[0-9a-fA-F]{3,8}\\b|\\b(rgb|rgba|hsl|hsla|oklch)\\(/';

const directionMessage =
  'Use logical direction utilities (ms-/me-/ps-/pe-/start-/end-/text-start/text-end/border-s/border-e/rounded-s/rounded-e).';
const colorMessage = 'Use semantic color tokens (e.g. bg-surface, text-foreground) instead of raw colors.';

// Themes are token-only so subtrees can carry their own data-theme (the POS stays Light in a Dark page).
const DARK_VARIANT = '/(^|\\s)dark:/';
const darkMessage = 'Do not use the dark: variant. Theme differences belong in tokens.css.';

export default defineConfig([
  globalIgnores(['dist', 'test-results', 'playwright-report', 'node_modules']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [js.configs.recommended, tseslint.configs.recommended, reactHooks.configs.flat['recommended-latest']],
    languageOptions: { globals: globals.browser },
    plugins: { 'react-refresh': reactRefresh },
    rules: {
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
    },
  },
  {
    files: ['src/**/*.tsx'],
    ignores: ['src/**/*.test.tsx'],
    extends: [i18next.configs['flat/recommended']],
    rules: {
      'i18next/no-literal-string': ['error', { mode: 'jsx-text-only' }],
    },
  },
  {
    files: ['src/**/*.{ts,tsx}'],
    ignores: ['src/**/*.test.ts'],
    rules: {
      'no-restricted-syntax': [
        'error',
        { selector: `Literal[value=${PHYSICAL_DIRECTION_CLASS}]`, message: directionMessage },
        { selector: `TemplateElement[value.raw=${PHYSICAL_DIRECTION_CLASS}]`, message: directionMessage },
        { selector: `Literal[value=${RAW_COLOR}]`, message: colorMessage },
        { selector: `TemplateElement[value.raw=${RAW_COLOR}]`, message: colorMessage },
        { selector: `Literal[value=${DARK_VARIANT}]`, message: darkMessage },
        { selector: `TemplateElement[value.raw=${DARK_VARIANT}]`, message: darkMessage },
      ],
    },
  },
]);
