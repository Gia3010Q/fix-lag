use crate::{
    account_store::AccountRecord,
    client_settings::{self, LaunchConfig},
    platform,
};
use percent_encoding::{NON_ALPHANUMERIC, utf8_percent_encode};
use reqwest::{
    blocking::Client,
    header::{COOKIE, HeaderValue},
};
use serde::Deserialize;
use std::{
    collections::{HashMap, HashSet},
    io,
    sync::{Mutex, OnceLock},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const TICKET_URL: &str = "https://auth.roblox.com/v1/authentication-ticket/";
static LAUNCH_LOCK: OnceLock<Mutex<()>> = OnceLock::new();

#[derive(Deserialize)]
struct UserInfo {
    id: u64,
    name: String,
}
#[derive(Deserialize)]
struct ServerPage {
    data: Vec<Server>,
    #[serde(rename = "nextPageCursor")]
    next: Option<String>,
}
#[derive(Deserialize)]
struct Server {
    id: String,
    playing: u32,
    #[serde(rename = "maxPlayers")]
    max_players: u32,
}
#[derive(Deserialize)]
struct PresencePage {
    #[serde(rename = "userPresences")]
    users: Vec<Presence>,
}
#[derive(Deserialize)]
struct Presence {
    #[serde(rename = "userId")]
    user_id: u64,
    #[serde(rename = "userPresenceType")]
    kind: u8,
}

fn client() -> io::Result<Client> {
    Client::builder()
        .timeout(Duration::from_secs(15))
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|_| io::Error::other("Cannot initialize HTTPS client"))
}

fn cookie_header(cookie: &str) -> io::Result<HeaderValue> {
    let value = cookie
        .trim()
        .strip_prefix(".ROBLOSECURITY=")
        .unwrap_or(cookie.trim());
    HeaderValue::from_str(&format!(".ROBLOSECURITY={value}"))
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "Invalid cookie format"))
}

pub fn validate_cookie(cookie: &str) -> io::Result<AccountRecord> {
    let response = client()?
        .get("https://users.roblox.com/v1/users/authenticated")
        .header(COOKIE, cookie_header(cookie)?)
        .send()
        .map_err(|_| io::Error::other("Roblox account verification failed"))?;
    if !response.status().is_success() {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Cookie was rejected by Roblox",
        ));
    }
    let user: UserInfo = response
        .json()
        .map_err(|_| io::Error::other("Roblox account response was invalid"))?;
    Ok(AccountRecord::from_login(
        &user.name,
        user.id,
        cookie.trim(),
    ))
}

pub fn random_job(place_id: u64, lowest: bool, page_count: usize) -> io::Result<String> {
    let client = client()?;
    let mut cursor = String::new();
    let mut servers = Vec::new();
    for _ in 0..page_count.clamp(1, 10) {
        let mut url = format!(
            "https://games.roblox.com/v1/games/{place_id}/servers/public?sortOrder=Asc&limit=100"
        );
        if !cursor.is_empty() {
            url.push_str("&cursor=");
            url.push_str(&utf8_percent_encode(&cursor, NON_ALPHANUMERIC).to_string());
        }
        let response = client
            .get(url)
            .send()
            .map_err(|_| io::Error::other("Không lấy được danh sách server"))?;
        if !response.status().is_success() {
            return Err(io::Error::other("Roblox từ chối danh sách server"));
        }
        let page: ServerPage = response
            .json()
            .map_err(|_| io::Error::other("Danh sách server không hợp lệ"))?;
        servers.extend(
            page.data
                .into_iter()
                .filter(|server| server.playing < server.max_players),
        );
        cursor = page.next.unwrap_or_default();
        if cursor.is_empty() {
            break;
        }
    }
    if servers.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "Không tìm thấy server còn chỗ",
        ));
    }
    let index = if lowest {
        servers
            .iter()
            .enumerate()
            .min_by_key(|(_, server)| server.playing)
            .map(|(index, _)| index)
            .unwrap_or(0)
    } else {
        rand::random_range(0..servers.len())
    };
    Ok(servers.swap_remove(index).id)
}

pub fn fetch_presence(ids: &[u64]) -> io::Result<HashMap<u64, u8>> {
    if ids.is_empty() {
        return Ok(HashMap::new());
    }
    let response = client()?
        .post("https://presence.roblox.com/v1/presence/users")
        .json(&serde_json::json!({"userIds":ids}))
        .send()
        .map_err(|_| io::Error::other("Không lấy được trạng thái tài khoản"))?;
    if !response.status().is_success() {
        return Err(io::Error::other("Roblox không trả trạng thái tài khoản"));
    }
    let page: PresencePage = response
        .json()
        .map_err(|_| io::Error::other("Trạng thái tài khoản không hợp lệ"))?;
    Ok(page
        .users
        .into_iter()
        .map(|user| (user.user_id, user.kind))
        .collect())
}

pub fn launch(
    account: &AccountRecord,
    place_id: u64,
    job_id: &str,
    follow: bool,
    config: &LaunchConfig,
) -> io::Result<()> {
    client_settings::patch(config)?;
    let _guard = LAUNCH_LOCK
        .get_or_init(|| Mutex::new(()))
        .lock()
        .expect("Roblox launch lock");
    if !config.require_new_process {
        return launch_once(account, place_id, job_id, follow);
    }
    let before: HashSet<(u32, u64)> = platform::roblox_processes()?
        .into_iter()
        .map(|p| (p.pid, p.started_ticks))
        .collect();
    for _ in 0..2 {
        launch_once(account, place_id, job_id, follow)?;
        let until = Instant::now() + Duration::from_secs(8);
        while Instant::now() < until {
            if platform::roblox_processes()
                .unwrap_or_default()
                .into_iter()
                .any(|p| !before.contains(&(p.pid, p.started_ticks)))
            {
                return Ok(());
            }
            thread::sleep(Duration::from_millis(250));
        }
    }
    Err(io::Error::new(
        io::ErrorKind::TimedOut,
        "Windows nhận lệnh nhưng chưa thấy phiên Roblox mới",
    ))
}

fn launch_once(
    account: &AccountRecord,
    place_id: u64,
    job_id: &str,
    follow: bool,
) -> io::Result<()> {
    let cookie = cookie_header(&account.cookie())?;
    let client = client()?;
    let preflight = client
        .post(TICKET_URL)
        .header(COOKIE, cookie.clone())
        .header("Referer", "https://www.roblox.com/")
        .send()
        .map_err(|_| io::Error::other("Roblox ticket preflight failed"))?;
    let csrf = preflight
        .headers()
        .get("x-csrf-token")
        .and_then(|header| header.to_str().ok())
        .ok_or_else(|| io::Error::other("Roblox did not return a CSRF token"))?;
    let response = client
        .post(TICKET_URL)
        .header(COOKIE, cookie)
        .header("Referer", "https://www.roblox.com/")
        .header("X-CSRF-TOKEN", csrf)
        .header("RBXAuthenticationNegotiation", "1")
        .send()
        .map_err(|_| io::Error::other("Roblox ticket request failed"))?;
    if !response.status().is_success() {
        return Err(io::Error::other(format!(
            "Roblox rejected the launch ticket ({})",
            response.status()
        )));
    }
    let ticket = response
        .headers()
        .get("rbx-authentication-ticket")
        .and_then(|header| header.to_str().ok())
        .ok_or_else(|| io::Error::other("Roblox launch ticket was missing"))?;
    let tracker: u64 = rand::random_range(100_000_000_000..999_999_999_999);
    let endpoint = if follow {
        format!(
            "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={place_id}"
        )
    } else if job_id.trim().is_empty() {
        format!(
            "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId={tracker}&placeId={place_id}&isPlayTogetherGame=false"
        )
    } else {
        format!(
            "https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId={tracker}&placeId={place_id}&gameId={}&isPlayTogetherGame=false",
            utf8_percent_encode(job_id.trim(), NON_ALPHANUMERIC)
        )
    };
    let launch_time = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis();
    let uri = format!(
        "roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{launch_time}+placelauncherurl:{}+browsertrackerid:{tracker}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp",
        utf8_percent_encode(&endpoint, NON_ALPHANUMERIC)
    );
    platform::open_url(&uri)
        .map_err(|_| io::Error::other("Windows could not open the Roblox protocol"))
}
