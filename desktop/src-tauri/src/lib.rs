//! agentd desktop (docs/architect/desktop.md): a window on one of your agentd servers. The servers page is the app's
//! own; a server's pages are plain web pages to it, with no access to these commands (capabilities/default.json).

mod servers;

use servers::{Server, Servers};
use std::path::PathBuf;
use std::sync::Mutex;
use tauri::menu::{MenuBuilder, MenuItemBuilder, PredefinedMenuItem, SubmenuBuilder};
use tauri::webview::NewWindowResponse;
use tauri::{AppHandle, Manager, State, Url, WebviewUrl, WebviewWindow, WebviewWindowBuilder};
use tauri_plugin_opener::OpenerExt;

/// Where `servers.json` lives and the servers page's own URL; the lock keeps reads and writes of the file in turn.
struct App {
    file: PathBuf,
    home: Mutex<Option<Url>>,
    lock: Mutex<()>,
}

#[tauri::command]
fn servers_list(app: State<'_, App>) -> Result<Servers, String> {
    let _turn = app.lock.lock().map_err(|e| e.to_string())?;
    servers::load(&app.file)
}

#[tauri::command]
fn servers_save(app: State<'_, App>, list: Vec<Server>) -> Result<Servers, String> {
    let _turn = app.lock.lock().map_err(|e| e.to_string())?;
    let mut saved = servers::load(&app.file).unwrap_or_default();
    saved.servers = servers::check(list)?;
    if !saved.servers.iter().any(|s| Some(&s.url) == saved.last.as_ref()) {
        saved.last = None;
    }

    servers::save(&app.file, &saved)?;
    Ok(saved)
}

#[tauri::command]
fn server_open(window: WebviewWindow, app: State<'_, App>, url: String) -> Result<(), String> {
    let url = servers::check_url(&url)?;
    {
        let _turn = app.lock.lock().map_err(|e| e.to_string())?;
        let mut saved = servers::load(&app.file).unwrap_or_default();
        saved.last = Some(url.to_string());
        servers::save(&app.file, &saved)?;
    }

    window.navigate(url).map_err(|e| e.to_string())
}

/// Back to the servers page (menu: Servers…); `?list` keeps it from opening the last server again.
fn show_servers(app: &AppHandle) {
    let home = app.state::<App>().home.lock().ok().and_then(|h| h.clone());
    if let (Some(window), Some(mut home)) = (app.get_webview_window("main"), home) {
        home.set_query(Some("list"));
        let _ = window.navigate(home);
    }
}

/// Only web pages open in the window (a sign-in page included); anything else is handed to the system.
fn web(url: &Url) -> bool {
    matches!(url.scheme(), "https" | "http" | "tauri")
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .setup(|app| {
            let file = app.path().app_config_dir()?.join("servers.json");
            app.manage(App { file, home: Mutex::new(None), lock: Mutex::new(()) });

            let servers = MenuItemBuilder::with_id("servers", "Servers…").accelerator("CmdOrCtrl+Shift+S").build(app)?;
            let reload = MenuItemBuilder::with_id("reload", "Reload").accelerator("CmdOrCtrl+R").build(app)?;
            let browser = MenuItemBuilder::with_id("browser", "Open in browser").build(app)?;
            let menu = SubmenuBuilder::new(app, "agentd")
                .items(&[&servers, &reload, &browser])
                .item(&PredefinedMenuItem::separator(app)?)
                .item(&PredefinedMenuItem::quit(app, Some("Quit"))?)
                .build()?;
            let edit = SubmenuBuilder::new(app, "Edit").undo().redo().separator().cut().copy().paste().select_all().build()?;
            app.set_menu(MenuBuilder::new(app).items(&[&menu, &edit]).build()?)?;
            app.on_menu_event(|app, event| {
                let window = app.get_webview_window("main");
                match event.id().as_ref() {
                    "servers" => show_servers(app),
                    "reload" => {
                        if let Some(w) = window {
                            let _ = w.reload();
                        }
                    }
                    "browser" => {
                        if let Some(url) = window.and_then(|w| w.url().ok()).filter(|u| matches!(u.scheme(), "https" | "http")) {
                            let _ = app.opener().open_url(url.as_str(), None::<&str>);
                        }
                    }
                    _ => {}
                }
            });

            let opener = app.handle().clone();
            let window = WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into()))
                .title("agentd")
                .inner_size(1280.0, 860.0)
                .min_inner_size(480.0, 360.0)
                .on_navigation(web)
                .on_new_window(move |url, _features| {
                    // target=_blank (a work item in Azure DevOps, a chat thread): the default browser, not a new window.
                    if matches!(url.scheme(), "https" | "http") {
                        let _ = opener.opener().open_url(url.as_str(), None::<&str>);
                    }
                    NewWindowResponse::Deny
                })
                .build()?;
            *app.state::<App>().home.lock().map_err(|e| e.to_string())? = Some(window.url()?);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![servers_list, servers_save, server_open])
        .run(tauri::generate_context!())
        .expect("agentd desktop failed to start");
}
