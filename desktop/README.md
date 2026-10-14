# agentd desktop

A desktop window on your agentd servers ([docs/architect/desktop.md](../docs/architect/desktop.md)). Tauri 2: a Rust
shell in `src-tauri/` and the servers page in `ui/` (plain HTML, CSS and a script).

```bash
npm ci
npm run dev        # run it (needs Rust; on Linux also WebKitGTK: see .github/workflows/desktop.yml)
npm run build      # installers in src-tauri/target/release/bundle/
npm run icons      # icons again, from the web UI's favicon

cd src-tauri && cargo fmt --check && cargo clippy --all-targets -- -D warnings && cargo test
```
