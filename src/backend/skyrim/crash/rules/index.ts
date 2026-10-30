import type { CrashRule } from '../crashRules.js';
import { graphicsRules } from './graphics.js';
import { knownRules } from './known.js';
import { memoryRules } from './memory.js';
import { pluginRules } from './plugins.js';
import { skseRules } from './skse.js';

/** Every crash rule, the one list readCrashLog runs; crashRules.test.ts holds their ids unique. */
export const CRASH_RULES: readonly CrashRule[] = [
    ...pluginRules,
    ...memoryRules,
    ...graphicsRules,
    ...skseRules,
    ...knownRules
];
