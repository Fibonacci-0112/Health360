import { pendingUploadCount } from '@health360/local-db';
import { usePowerSync, useQuery, useStatus } from '@powersync/react';
import { useEffect, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, space } from './theme';

/**
 * Shows whether this device is online and how many entries haven't reached the server yet
 * (PLAN.md §2.7: the "unsynced entries" indicator), plus any writes the server rejected.
 */
export function SyncBadge() {
  const db = usePowerSync();
  const status = useStatus();
  const [pending, setPending] = useState(0);
  const { data: failed } = useQuery<{ n: number }>('SELECT count(*) AS n FROM failed_uploads');

  useEffect(() => {
    let alive = true;
    const tick = async () => {
      const n = await pendingUploadCount(db);
      if (alive) setPending(n);
    };
    void tick();
    const timer = setInterval(tick, 1000);
    return () => {
      alive = false;
      clearInterval(timer);
    };
  }, [db]);

  const failedCount = failed[0]?.n ?? 0;
  const label = [
    status.connected ? 'Online' : 'Offline',
    pending > 0 ? `${pending} unsynced` : status.hasSynced ? 'all synced' : 'not synced yet',
    failedCount > 0 ? `${failedCount} rejected` : null,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <View
      role="status"
      aria-label={`Sync status: ${label}`}
      style={[styles.badge, !status.connected && styles.offline, failedCount > 0 && styles.failed]}
    >
      <View style={[styles.dot, { backgroundColor: status.connected ? colors.primary : colors.muted }]} />
      <Text style={styles.text}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  badge: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
    paddingHorizontal: space.md,
    paddingVertical: space.xs,
    borderRadius: 999,
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    alignSelf: 'flex-start',
  },
  offline: { borderColor: colors.muted },
  failed: { borderColor: colors.danger },
  dot: { width: 8, height: 8, borderRadius: 4 },
  text: { color: colors.text, fontSize: 13 },
});
