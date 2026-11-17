use crate::{account_store::AccountRecord, config::IniDoc};
use serde_json::json;
use std::{
    io::{self, Read, Write},
    net::{TcpListener, TcpStream},
    sync::{
        Arc, RwLock,
        atomic::{AtomicBool, Ordering},
        mpsc::Sender,
    },
    thread,
    time::Duration,
};

pub enum WebCommand {
    Launch {
        username: String,
        place_id: u64,
        job_id: String,
        follow: bool,
    },
    SetField {
        username: String,
        field: String,
        value: String,
    },
}

#[derive(Clone)]
pub struct Policy {
    port: u16,
    external: bool,
    password: String,
    every_request: bool,
    get_accounts: bool,
    get_cookie: bool,
    launch: bool,
    edit: bool,
}

impl Policy {
    pub fn from_settings(settings: &IniDoc) -> Option<Self> {
        if !settings.bool("Developer", "EnableWebServer", false) {
            return None;
        }
        let port = settings.number("WebServer", "WebServerPort", 7963_u16);
        if port == 0 {
            return None;
        }
        Some(Self {
            port,
            external: settings.bool("WebServer", "AllowExternalConnections", false),
            password: settings
                .get("WebServer", "Password")
                .unwrap_or("")
                .to_owned(),
            every_request: settings.bool("WebServer", "EveryRequestRequiresPassword", true),
            get_accounts: settings.bool("WebServer", "AllowGetAccounts", false),
            get_cookie: settings.bool("WebServer", "AllowGetCookie", false),
            launch: settings.bool("WebServer", "AllowLaunchAccount", false),
            edit: settings.bool("WebServer", "AllowAccountEditing", false),
        })
    }
}

pub struct WebServer {
    stopped: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}

impl WebServer {
    pub fn start(
        policy: Policy,
        accounts: Arc<RwLock<Vec<AccountRecord>>>,
        commands: Sender<WebCommand>,
    ) -> io::Result<Self> {
        if policy.password.len() < 6 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Webserver Password phải có ít nhất 6 ký tự",
            ));
        }
        let address = if policy.external {
            format!("0.0.0.0:{}", policy.port)
        } else {
            format!("127.0.0.1:{}", policy.port)
        };
        let listener = TcpListener::bind(address)?;
        listener.set_nonblocking(true)?;
        let stopped = Arc::new(AtomicBool::new(false));
        let signal = stopped.clone();
        let worker = thread::spawn(move || {
            while !signal.load(Ordering::Relaxed) {
                match listener.accept() {
                    Ok((stream, _)) => handle(stream, &policy, &accounts, &commands),
                    Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(50))
                    }
                    Err(_) => thread::sleep(Duration::from_millis(200)),
                }
            }
        });
        Ok(Self {
            stopped,
            worker: Some(worker),
        })
    }
}

impl Drop for WebServer {
    fn drop(&mut self) {
        self.stopped.store(true, Ordering::Relaxed);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn handle(
    mut stream: TcpStream,
    policy: &Policy,
    accounts: &Arc<RwLock<Vec<AccountRecord>>>,
    commands: &Sender<WebCommand>,
) {
    let _ = stream.set_read_timeout(Some(Duration::from_millis(500)));
    let _ = stream.set_write_timeout(Some(Duration::from_millis(500)));
    let mut buffer = [0_u8; 8192];
    let size = match stream.read(&mut buffer) {
        Ok(size) => size,
        Err(_) => return,
    };
    let request = String::from_utf8_lossy(&buffer[..size]);
    let Some(first) = request.lines().next() else {
        return;
    };
    let mut parts = first.split_whitespace();
    let (Some(verb), Some(uri)) = (parts.next(), parts.next()) else {
        return;
    };
    if verb != "GET" || !uri.starts_with("/api") {
        reply(
            &mut stream,
            404,
            json!({"success":false,"error":"Not found"}),
        );
        return;
    }
    let mut query = std::collections::HashMap::new();
    if let Some((_, tail)) = uri.split_once('?') {
        for pair in tail.split('&') {
            if let Some((key, value)) = pair.split_once('=') {
                let value = percent_encoding::percent_decode_str(value)
                    .decode_utf8_lossy()
                    .replace('+', " ");
                query.insert(key.to_ascii_lowercase(), value);
            }
        }
    }
    let method = query.get("method").map(String::as_str).unwrap_or("");
    let sensitive = matches!(
        method,
        "GetCookie" | "LaunchAccount" | "FollowUser" | "SetAlias" | "SetDescription"
    );
    let supplied = request
        .lines()
        .find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("X-Mizuauto-Password")
                .then(|| value.trim())
        })
        .unwrap_or("");
    if (sensitive || policy.every_request)
        && !constant_time_equal(supplied.as_bytes(), policy.password.as_bytes())
    {
        reply(
            &mut stream,
            401,
            json!({"success":false,"error":"Unauthorized"}),
        );
        return;
    }
    let username = query.get("account").map(String::as_str).unwrap_or("");
    let response = match method {
        "GetAccounts" if policy.get_accounts => {
            let rows: Vec<_> = accounts.read().unwrap().iter().map(|a| json!({"username":a.username(),"userId":a.user_id(),"alias":a.alias(),"description":a.description()})).collect();
            json!({"success":true,"accounts":rows})
        }
        "GetCookie" if policy.get_cookie => {
            let cookie = accounts
                .read()
                .unwrap()
                .iter()
                .find(|a| a.username().eq_ignore_ascii_case(username))
                .map(|a| a.cookie());
            json!({"success":cookie.is_some(),"cookie":cookie})
        }
        "LaunchAccount" | "FollowUser" if policy.launch => {
            let place_id = query
                .get("placeid")
                .and_then(|s| s.parse::<u64>().ok())
                .unwrap_or(0);
            if place_id == 0
                || !accounts
                    .read()
                    .unwrap()
                    .iter()
                    .any(|a| a.username().eq_ignore_ascii_case(username))
            {
                json!({"success":false,"error":"Invalid account or PlaceId"})
            } else {
                let result = commands.send(WebCommand::Launch {
                    username: username.into(),
                    place_id,
                    job_id: query.get("jobid").cloned().unwrap_or_default(),
                    follow: method == "FollowUser",
                });
                json!({"success":result.is_ok()})
            }
        }
        "SetAlias" | "SetDescription" if policy.edit => {
            let field = if method == "SetAlias" {
                "Alias"
            } else {
                "Description"
            };
            let value = query.get("value").cloned().unwrap_or_default();
            if value.len() > if field == "Alias" { 50 } else { 5000 }
                || !accounts
                    .read()
                    .unwrap()
                    .iter()
                    .any(|a| a.username().eq_ignore_ascii_case(username))
            {
                json!({"success":false,"error":"Invalid account or value"})
            } else {
                let result = commands.send(WebCommand::SetField {
                    username: username.into(),
                    field: field.into(),
                    value,
                });
                json!({"success":result.is_ok()})
            }
        }
        _ => json!({"success":false,"error":"Method disabled or unknown"}),
    };
    reply(&mut stream, 200, response);
}

fn constant_time_equal(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0_u8;
    for (x, y) in a.iter().zip(b) {
        diff |= x ^ y;
    }
    diff == 0
}

fn reply(stream: &mut TcpStream, status: u16, value: serde_json::Value) {
    let body = value.to_string();
    let reason = match status {
        200 => "OK",
        401 => "Unauthorized",
        _ => "Not Found",
    };
    let header = format!(
        "HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n",
        body.len()
    );
    let _ = stream.write_all(header.as_bytes());
    let _ = stream.write_all(body.as_bytes());
}
