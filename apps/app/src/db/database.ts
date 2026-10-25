import { OPSqliteOpenFactory } from '@powersync/op-sqlite';
import { PowerSyncDatabase } from '@powersync/react-native';
import { AppSchema } from '@health360/local-db';

/** Android (and iOS later): SQLite through op-sqlite. database.web.ts is the browser build. */
export function createPowerSync() {
  return new PowerSyncDatabase({
    schema: AppSchema,
    factory: new OPSqliteOpenFactory({ dbFilename: 'health360.sqlite' }),
  });
}
