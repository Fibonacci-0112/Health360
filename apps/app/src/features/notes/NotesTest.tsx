import { notes } from '@health360/local-db';
import { noteInputSchema } from '@health360/schemas';
import { toCompilableQuery } from '@powersync/drizzle-driver';
import { useQuery } from '@powersync/react';
import { desc } from 'drizzle-orm';
import { useState } from 'react';
import { FlatList, StyleSheet, Text, TextInput, View } from 'react-native';
import { Button } from '@/components/Button';
import { SyncBadge } from '@/components/SyncBadge';
import { colors, space } from '@/components/theme';
import { db } from '@/db/powersync';
import { localDate, newId } from '@/lib/ids';
import { useMyProfile } from '@/lib/profile';

const allNotes = toCompilableQuery(db.select().from(notes).orderBy(desc(notes.created_at)));

/**
 * Phase 0 sync test (PLAN.md §3): write a note offline on one device, reconnect, and it should
 * appear on another device signed in to the same account within about 5 seconds.
 */
export function NotesTest() {
  const { profile } = useMyProfile();
  const { data } = useQuery(allNotes);
  const [body, setBody] = useState('');

  const add = async () => {
    const parsed = noteInputSchema.safeParse({ body });
    if (!parsed.success || !profile) return;
    const now = new Date().toISOString();
    await db.insert(notes).values({
      id: newId(),
      profile_id: profile.id,
      body: parsed.data.body,
      local_date: localDate(),
      created_at: now,
      updated_at: now,
    });
    setBody('');
  };

  return (
    <View style={styles.container}>
      <SyncBadge />
      <Text style={styles.hint}>Sync test: notes written here, even offline, should reach your other devices.</Text>
      <View style={styles.row}>
        <TextInput
          aria-label="New note"
          value={body}
          onChangeText={setBody}
          placeholder="Write a note"
          style={styles.input}
          onSubmitEditing={add}
          maxLength={2000}
        />
        <Button title="Add" onPress={add} disabled={!body.trim() || !profile} />
      </View>
      <FlatList
        data={data}
        keyExtractor={(n) => n.id}
        renderItem={({ item }) => (
          <View style={styles.note}>
            <Text style={styles.body}>{item.body}</Text>
            <Text style={styles.meta}>{item.local_date}</Text>
          </View>
        )}
        ListEmptyComponent={<Text style={styles.meta}>No notes yet.</Text>}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: space.lg, gap: space.md, backgroundColor: colors.white },
  hint: { color: colors.muted },
  row: { flexDirection: 'row', gap: space.sm, alignItems: 'center' },
  input: { flex: 1, borderWidth: 1, borderColor: colors.border, borderRadius: 8, padding: space.md, fontSize: 16, color: colors.text },
  note: { paddingVertical: space.md, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: colors.border },
  body: { color: colors.text, fontSize: 16 },
  meta: { color: colors.muted, fontSize: 12 },
});
