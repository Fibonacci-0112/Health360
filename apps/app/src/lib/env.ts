// EXPO_PUBLIC_* variables are inlined at build time. See .env.example.
function required(name: string, value: string | undefined): string {
  if (!value) throw new Error(`${name} is not set. Copy apps/app/.env.example to apps/app/.env.`);
  return value;
}

export const env = {
  supabaseUrl: required('EXPO_PUBLIC_SUPABASE_URL', process.env.EXPO_PUBLIC_SUPABASE_URL),
  supabaseAnonKey: required('EXPO_PUBLIC_SUPABASE_ANON_KEY', process.env.EXPO_PUBLIC_SUPABASE_ANON_KEY),
  powersyncUrl: required('EXPO_PUBLIC_POWERSYNC_URL', process.env.EXPO_PUBLIC_POWERSYNC_URL),
};
