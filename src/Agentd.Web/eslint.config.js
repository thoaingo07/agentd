import js from '@eslint/js'
import tseslint from 'typescript-eslint'
import vue from 'eslint-plugin-vue'
import globals from 'globals'

export default tseslint.config(
  { ignores: ['node_modules', 'wwwroot', 'bin', 'obj'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...vue.configs['flat/recommended'],
  {
    files: ['**/*.vue'],
    languageOptions: { parserOptions: { parser: tseslint.parser } },
  },
  {
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
    rules: {
      // Security: agent output is untrusted; never render raw HTML (see docs/security).
      'vue/no-v-html': 'error',
    },
  },
  {
    // Reusable UI is built once in shared/components/ui (design system §6); features use the Ag* components.
    files: ['ClientApps/**/*.ts', 'ClientApps/**/*.vue'],
    ignores: ['ClientApps/shared/components/ui/**'],
    rules: {
      'no-restricted-imports': [
        'error',
        { paths: [{ name: 'base-ui-vue', message: 'Import the Ag* components from shared/components/ui instead.' }] },
      ],
    },
  },
  {
    // Every request goes through shared/api/http.ts and its interceptors (T3.7).
    files: ['ClientApps/**/*.ts', 'ClientApps/**/*.vue'],
    ignores: ['ClientApps/shared/api/**'],
    rules: {
      'no-restricted-globals': ['error', { name: 'fetch', message: 'Use get/send from shared/api/http.ts.' }],
    },
  },
  {
    files: ['public/**/*.js'],
    languageOptions: { sourceType: 'script' },
    // Plain ES5 for the pre-paint loader: catch bindings are required there.
    rules: { '@typescript-eslint/no-unused-vars': ['error', { caughtErrors: 'none' }] },
  },
)
