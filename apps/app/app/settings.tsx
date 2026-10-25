import { supabaseWriter } from '@health360/api';
import { discardFailedUpload, retryFailedUpload } from '@health360/local-db';
import { usePowerSync, useQuery } from '@powersync/react';
import { useState } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { Button } from '@/components/Button';
import { SyncBadge } from '@/components/SyncBadge';
import { colors, space } from '@/components/theme';
import { UnitPrefsEditor } from '@/components/UnitPrefsEditor';
import { signOut } from '@/lib/auth';
import { parseUnitPrefs, useMyProfile } from '@/lib/profile';
import { supabase } from '@/lib/supabase';

interface FailedUpload {
  id: string;
  table_name: string;
  op: string;
  error_code: string | null;
  error_message: string | null;
  failed_at: string;
}

export default function Settings() {
  const db = usePowerSync();
  const { profile } = useMyProfile();
  const { data: failed } = useQuery<FailedUpload>('SELECT * FROM failed_uploads ORDER BY failed_at DESC');
  const prefs = parseUnitPrefs(profile?.unit_prefs);
  const [retryNotice, setRetryNotice] = useState<string | null>(null);

  const retry = async (id: string) => {
    const result = await retryFailedUpload(db, id, supabaseWriter(supabase));
    setRetryNotice(
      result === 'offline'
        ? 'Retry needs a connection to the server. Try again when you’re online.'
        : result === 'rejected'
          ? 'The server refused it again. The reason is updated below; discard it if it can’t be fixed.'
          : null,
    );
  };

  return (
    <ScrollView contentContainerStyle={styles.body}>
      <SyncBadge />

      <Text style={styles.section} role="heading">
        Units
      </Text>
      <UnitPrefsEditor
        value={prefs}
        onChange={(next) =>
          profile && db.execute('UPDATE profiles SET unit_prefs = ? WHERE id = ?', [JSON.stringify(next), profile.id])
        }
      />

      {failed.length > 0 ? (
        <>
          <Text style={styles.section} role="heading">
            Changes the server rejected
          </Text>
          <Text style={styles.muted}>
            These didn&apos;t sync and won&apos;t retry on their own. Retry once the cause is fixed, or discard them.
          </Text>
          {retryNotice ? (
            <Text style={styles.muted} aria-live="polite">
              {retryNotice}
            </Text>
          ) : null}
          {failed.map((f) => (
            <View key={f.id} style={styles.failed}>
              <Text style={styles.text}>
                {f.op} {f.table_name} · {f.error_code ?? 'error'}
              </Text>
              <Text style={styles.muted}>{f.error_message}</Text>
              <View style={styles.row}>
                <Button title="Retry" variant="secondary" onPress={() => retry(f.id)} />
                <Button title="Discard" variant="danger" onPress={() => discardFailedUpload(db, f.id)} />
              </View>
            </View>
          ))}
        </>
      ) : null}

      <Text style={styles.section} role="heading">
        Account
      </Text>
      <Text style={styles.muted}>Signing out removes this account&apos;s data from this device.</Text>
      <Button title="Sign out" variant="secondary" onPress={signOut} />
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  body: { flexGrow: 1, backgroundColor: colors.white, padding: space.lg, gap: space.md, maxWidth: 640, width: '100%', alignSelf: 'center' },
  section: { fontSize: 18, fontWeight: '700', color: colors.text, marginTop: space.md },
  muted: { color: colors.muted },
  text: { color: colors.text, fontWeight: '600' },
  failed: { gap: space.xs, padding: space.md, borderWidth: 1, borderColor: colors.danger, borderRadius: 8 },
  row: { flexDirection: 'row', gap: space.sm },
});
