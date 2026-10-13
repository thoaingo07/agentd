# Desktop app

A small desktop window for agentd's web UI: you pick one of your agentd servers and the window shows it, like a
browser tab you don't have to find. It runs **no agentd of its own**: no daemon, no database, no agents (decided
2026-10-13). Everything still runs on the server; the app is a thin client.

## 1. What it does

- **Servers.** On start it shows your saved servers (name and URL) and opens the last one you used. **Add** takes a
  name and a URL, **Remove** forgets one. The list lives in `servers.json` in the app's config folder
  (`~/.config/dev.agentd.desktop` on Linux, `%APPDATA%\dev.agentd.desktop` on Windows,
  `~/Library/Application Support/dev.agentd.desktop` on macOS), never on a server.
- **Opening one** points the window at its URL: the server's own pages, sign-in and all, exactly as in a browser.
  Tailscale sign-in works when this computer is on the tailnet, and Cloudflare Access signs in inside the window.
- **The app menu** has **Servers…** (Ctrl/Cmd+Shift+S, back to the list), **Reload** (Ctrl/Cmd+R), **Open in
  browser** (the page you're on, in your default browser) and **Quit**.
- **Links that open a new tab** (a work item in Azure DevOps, a chat thread) open in your default browser.

## 2. Safety

- **Server pages get no app access.** Only the app's own servers page can call the app (list, save, open). Tauri's
  capability file (`desktop/src-tauri/capabilities/default.json`) names no remote URLs, so a server's page, or anything
  it loads, can't reach the app's commands. A server is a website to the app, nothing more.
- **Only `https://` URLs**, or `http://` to this computer (`localhost`, `127.0.0.1`, `[::1]`) for a server on the same
  machine or an SSH tunnel ([deployment.md](deployment.md)). Anything else is refused when you add it and
  again when you open it.
- **No secrets in the app.** It stores names and URLs only; sign-in cookies stay in the window's own web storage,
  like a browser profile.
- The servers page has a strict CSP: scripts and styles from the app only, nothing inline.

## 3. How it's built

- **[Tauri 2](https://tauri.app)** in `desktop/`: a Rust shell (`desktop/src-tauri`) around the system web view
  (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux), so the app is a few MB and needs no bundled browser.
- The servers page is plain HTML, CSS and a script in `desktop/ui` (no framework, nothing to build).
- Rust commands: `servers_list`, `servers_save` and `server_open`. URL checks and the servers file are in
  `servers.rs`, with unit tests (`cargo test`).
- **CI** (`.github/workflows/desktop.yml`, on changes under `desktop/`): `cargo fmt --check`, `cargo clippy`,
  `cargo test`, and a build on Linux, Windows and macOS whose installers are kept as workflow artifacts. Publishing
  them in a release is a separate, later step.

## 4. Not now

- Signed installers and auto-update (they need signing certificates).
- Native notifications for questions and approvals: the server's page would need a way to ask the app, which §2 keeps
  closed on purpose.
- Several servers side by side (tabs or windows).
