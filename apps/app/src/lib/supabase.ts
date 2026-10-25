import AsyncStorage from '@react-native-async-storage/async-storage';
import { createClient } from '@supabase/supabase-js';
import { Platform } from 'react-native';
import { env } from './env';

// The session is the only thing kept here: AsyncStorage on native, localStorage on web.
// Health data never goes through this storage; it lives in PowerSync's SQLite.
export const supabase = createClient(env.supabaseUrl, env.supabaseAnonKey, {
  auth: {
    storage: AsyncStorage,
    autoRefreshToken: true,
    persistSession: true,
    detectSessionInUrl: false,
    ...(Platform.OS === 'web' ? {} : { lock: undefined }),
  },
});
