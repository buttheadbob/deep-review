// Deep review ESLint configuration (flat config). Passed with --config, so a repository's own ESLint config is never
// loaded (it is code, and repository code does not run on the host).
// JavaScript: @eslint/js recommended. TypeScript: typescript-eslint strictTypeChecked (type-aware).
// Inline "eslint-disable" comments are ignored (noInlineConfig) and counted as suppression issues.
// A rule is turned off only in the "off" block at the end, each with a "// reason:". None is off today.
// The linters are pinned in ../linters/js (package-lock.json) and resolved from there.
import { createRequire } from 'node:module';

const require = createRequire(new URL('../linters/js/package.json', import.meta.url));
const js = require('@eslint/js');
const tseslint = require('typescript-eslint');
const globals = require('globals');

const root = process.env.DEEP_TS_ROOT || process.cwd();
// deep_tools.py lints .js files with import/export statements in a second pass with DEEP_JS_SOURCE_TYPE=module.
const jsSourceType = process.env.DEEP_JS_SOURCE_TYPE === 'module' ? 'module' : 'script';
const ts = ['**/*.ts', '**/*.tsx', '**/*.mts', '**/*.cts'];

export default [
    { linterOptions: { noInlineConfig: true, reportUnusedDisableDirectives: 'off' } },
    {
        files: ['**/*.js', '**/*.jsx', '**/*.cjs'],
        ...js.configs.recommended,
        languageOptions: { ecmaVersion: 'latest', sourceType: jsSourceType, globals: { ...globals.browser, ...globals.node } },
    },
    {
        files: ['**/*.mjs'],
        ...js.configs.recommended,
        languageOptions: { ecmaVersion: 'latest', sourceType: 'module', globals: { ...globals.browser, ...globals.node } },
    },
    ...tseslint.configs.strictTypeChecked.map((c) => ({ ...c, files: ts })),
    {
        files: ts,
        languageOptions: {
            globals: { ...globals.browser, ...globals.node },
            parserOptions: {
                projectService: {
                    allowDefaultProject: ['*.ts', '*/*.ts', '*/*/*.ts', '*.tsx', '*/*.tsx'],
                    maximumDefaultProjectFileMatchCount_THIS_WILL_SLOW_DOWN_LINTING: 10000,
                },
                tsconfigRootDir: root,
            },
        },
    },
    // off: rules turned off for every project, each with a reason
    {
        rules: {},
    },
];
