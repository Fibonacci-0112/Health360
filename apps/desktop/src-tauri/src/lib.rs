/// The whole app is the Expo web export; this shell only hosts it. Native menus, the auto-updater
/// and lab-PDF drag-and-drop come in Phase 1 (PLAN.md §2.1).
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .run(tauri::generate_context!())
        .expect("error while running Health360");
}
