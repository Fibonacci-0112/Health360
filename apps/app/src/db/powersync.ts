import { drizzleSchema } from '@health360/local-db';
import { wrapPowerSyncWithDrizzle } from '@powersync/drizzle-driver';
import { createPowerSync } from './database';

/** One local database per install, shared by the whole app. */
export const powersync = createPowerSync();
export const db = wrapPowerSyncWithDrizzle(powersync, { schema: drizzleSchema });
