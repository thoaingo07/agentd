//! The saved servers (docs/architect/desktop.md §1): names and URLs in `servers.json`, and which URLs the app opens.

use serde::{Deserialize, Serialize};
use std::fs;
use std::io;
use std::path::Path;
use url::{Host, Url};

/// One agentd server: a name you chose and the URL of its web UI.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Server {
    pub name: String,
    pub url: String,
}

/// `servers.json`: the list in your order, and the URL opened last (opened again at start).
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Servers {
    pub servers: Vec<Server>,
    pub last: Option<String>,
}

/// The URLs the app opens (§2): `https://` anywhere, `http://` only to this computer. No credentials in the URL.
pub fn check_url(raw: &str) -> Result<Url, String> {
    let url = Url::parse(raw.trim()).map_err(|_| format!("\"{}\" isn't a URL.", raw.trim()))?;
    if !url.username().is_empty() || url.password().is_some() {
        return Err("Leave the user name and password out of the URL; the server asks you to sign in.".into());
    }

    match url.scheme() {
        "https" if url.host().is_some() => Ok(url),
        "http" if is_loopback(url.host()) => Ok(url),
        "http" => Err("Use https://, or http:// only for this computer (localhost).".into()),
        _ => Err("The URL starts with https://.".into()),
    }
}

fn is_loopback(host: Option<Host<&str>>) -> bool {
    match host {
        Some(Host::Domain(d)) => d.eq_ignore_ascii_case("localhost"),
        Some(Host::Ipv4(ip)) => ip.is_loopback(),
        Some(Host::Ipv6(ip)) => ip.is_loopback(),
        None => false,
    }
}

/// Checks a list before it's saved: every server named, every URL one the app opens, no URL twice.
pub fn check(list: Vec<Server>) -> Result<Vec<Server>, String> {
    let mut checked: Vec<Server> = Vec::with_capacity(list.len());
    for s in list {
        let name = s.name.trim();
        if name.is_empty() {
            return Err("Every server needs a name.".into());
        }

        let url = check_url(&s.url)?.to_string();
        if checked.iter().any(|c| c.url == url) {
            return Err(format!("{url} is in the list twice."));
        }

        checked.push(Server { name: name.into(), url });
    }

    Ok(checked)
}

/// Reads `servers.json`; none yet is an empty list.
pub fn load(path: &Path) -> Result<Servers, String> {
    match fs::read(path) {
        Ok(bytes) => serde_json::from_slice(&bytes).map_err(|e| format!("{} can't be read: {e}", path.display())),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(Servers::default()),
        Err(e) => Err(format!("{} can't be read: {e}", path.display())),
    }
}

/// Writes `servers.json` whole: to a temporary file first, then renamed over the old one, so a crash never leaves half.
pub fn save(path: &Path, servers: &Servers) -> Result<(), String> {
    let fail = |e: io::Error| format!("{} can't be saved: {e}", path.display());
    if let Some(dir) = path.parent() {
        fs::create_dir_all(dir).map_err(fail)?;
    }

    let tmp = path.with_extension("json.tmp");
    let json = serde_json::to_vec_pretty(servers).map_err(|e| e.to_string())?;
    fs::write(&tmp, json).map_err(fail)?;
    fs::rename(&tmp, path).map_err(fail)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn server(name: &str, url: &str) -> Server {
        Server { name: name.into(), url: url.into() }
    }

    #[test]
    fn opens_https_anywhere_and_http_only_on_this_computer() {
        for ok in [
            "https://agentd.example.com",
            "https://ulab-ser8.tail20ff.ts.net:18008/",
            "http://localhost:7780",
            "http://127.0.0.1:7780/",
            "http://[::1]:7780",
        ] {
            assert!(check_url(ok).is_ok(), "{ok}");
        }

        for bad in [
            "http://agentd.example.com",
            "http://192.168.1.5:7780",
            "file:///etc/passwd",
            "javascript:alert(1)",
            "ftp://x",
            "agentd.example.com",
            "",
        ] {
            assert!(check_url(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn refuses_credentials_in_the_url() {
        let err = check_url("https://me:secret@agentd.example.com").unwrap_err();
        assert!(err.contains("password"), "{err}");
    }

    #[test]
    fn check_trims_names_normalizes_urls_and_refuses_blanks_and_twins() {
        let list = check(vec![server(" Work ", " https://agentd.example.com ")]).unwrap();
        assert_eq!(list, vec![server("Work", "https://agentd.example.com/")]);

        assert!(check(vec![server(" ", "https://a.example.com")]).is_err());
        assert!(check(vec![server("A", "https://a.example.com"), server("B", "https://a.example.com/")]).is_err());
        assert!(check(vec![server("A", "http://a.example.com")]).is_err());
    }

    #[test]
    fn load_and_save_round_trip_and_no_file_is_an_empty_list() {
        let dir = std::env::temp_dir().join(format!("agentd-desktop-{}", std::process::id()));
        let path = dir.join("nested").join("servers.json");
        assert_eq!(load(&path).unwrap(), Servers::default());

        let servers =
            Servers { servers: vec![server("Work", "https://agentd.example.com/")], last: Some("https://agentd.example.com/".into()) };
        save(&path, &servers).unwrap();
        assert_eq!(load(&path).unwrap(), servers);
        assert!(!path.with_extension("json.tmp").exists());

        fs::write(&path, b"{ not json").unwrap();
        assert!(load(&path).is_err());
        fs::remove_dir_all(dir).unwrap();
    }
}
