use crate::{
    account_ops,
    account_store::{AccountRecord, AccountStore, LoadResult},
    browser::Browser,
    client_settings::LaunchConfig,
    config::{IniDoc, OptimizerState, Paths, Theme},
    media::{MediaEvent, MediaWorker},
    optimizer::Optimizer,
    platform,
    web_server::{Policy, WebCommand, WebServer},
};
use eframe::egui::{self, Color32, RichText, TextureHandle};
use std::{
    collections::{BTreeSet, HashMap},
    sync::{
        Arc, RwLock,
        mpsc::{self, Receiver, Sender},
    },
    thread,
    time::{Duration, Instant},
};

const BG: Color32 = Color32::from_rgb(23, 20, 33);
const PANEL: Color32 = Color32::from_rgb(36, 29, 50);
const PINK: Color32 = Color32::from_rgb(241, 118, 186);
const PURPLE: Color32 = Color32::from_rgb(174, 141, 198);
const GLASS: Color32 = PANEL;
const GLASS_BORDER: Color32 = Color32::from_rgb(115, 90, 145);

enum Event {
    Added(Result<AccountRecord, String>),
    Launched(String, Result<(), String>),
    Shuffled(Result<String, String>),
    Presence(Result<HashMap<u64, u8>, String>),
}
struct PendingLaunch {
    accounts: Vec<AccountRecord>,
    place_id: u64,
    job_id: String,
    follow: bool,
    deadline: Instant,
}

pub struct MizuApp {
    paths: Option<Paths>,
    accounts: Option<AccountStore>,
    settings: IniDoc,
    theme: Option<Theme>,
    optimizer: Optimizer,
    mutex: Option<platform::RobloxMutex>,
    events: Sender<Event>,
    receiver: Receiver<Event>,
    web_accounts: Arc<RwLock<Vec<AccountRecord>>>,
    web_commands: Sender<WebCommand>,
    web_receiver: Receiver<WebCommand>,
    web_server: Option<WebServer>,
    selected: BTreeSet<usize>,
    logo: TextureHandle,
    background: Option<TextureHandle>,
    background_loaded: String,
    media: Option<MediaWorker>,
    show_optimizer: bool,
    show_settings: bool,
    show_theme: bool,
    show_add: bool,
    show_password: bool,
    show_control: bool,
    show_browser: bool,
    browser: Option<Browser>,
    browser_request: Option<AccountRecord>,
    web_context: wry::WebContext,
    settings_tab: usize,
    cookie_input: String,
    password_input: String,
    place_id: String,
    job_id: String,
    follow_id: String,
    alias_input: String,
    description_input: String,
    group_input: String,
    field_key: String,
    field_value: String,
    interval_input: f64,
    status: String,
    toast_until: Instant,
    hide_usernames: bool,
    multi_backoff: Duration,
    next_mutex_try: Instant,
    pending_launch: Option<PendingLaunch>,
    presence: HashMap<u64, u8>,
    next_presence: Instant,
}

impl MizuApp {
    pub fn new(cc: &eframe::CreationContext<'_>) -> Self {
        let mut visuals = egui::Visuals::dark();
        visuals.panel_fill = BG;
        visuals.window_fill = BG;
        visuals.extreme_bg_color = Color32::from_rgb(43, 36, 58);
        visuals.selection.bg_fill = PURPLE.gamma_multiply(0.45);
        visuals.widgets.active.bg_fill = PURPLE;
        visuals.widgets.hovered.bg_fill = PINK.gamma_multiply(0.7);
        visuals.widgets.inactive.bg_fill = Color32::from_rgb(77, 60, 99);
        visuals.widgets.inactive.corner_radius = egui::CornerRadius::ZERO;
        visuals.widgets.hovered.corner_radius = egui::CornerRadius::ZERO;
        visuals.widgets.active.corner_radius = egui::CornerRadius::ZERO;
        visuals.window_corner_radius = egui::CornerRadius::ZERO;
        cc.egui_ctx.set_visuals(visuals);
        let logo = texture(&cc.egui_ctx, "mizu-logo", &crate::config::logo_bytes());
        let paths = Paths::discover().ok();
        let settings = paths
            .as_ref()
            .and_then(|p| IniDoc::load(&p.settings).ok())
            .unwrap_or_default();
        if settings.bool("General", "RunOnStartup", false) {
            if let Ok(exe) = std::env::current_exe() {
                let _ = platform::set_startup(true, &exe);
            }
        }
        let theme = paths.as_ref().and_then(|p| Theme::load(&p.theme).ok());
        let state = paths
            .as_ref()
            .map(|p| OptimizerState::load(&p.state))
            .unwrap_or_default();
        let interval_input = state.interval_seconds;
        let (events, receiver) = mpsc::channel();
        let closed = platform::terminate_roblox_at_startup();
        let (accounts, show_password, status) = if let Some(paths) = &paths {
            match AccountStore::load(&paths.account_data, None) {
                Ok(LoadResult::Ready(data)) => (
                    Some(data),
                    false,
                    format!("Đã đóng {closed} phiên Roblox để tránh xung đột"),
                ),
                Ok(LoadResult::PasswordRequired) => (
                    None,
                    true,
                    format!("Đã đóng {closed} phiên Roblox · cần mật khẩu dữ liệu"),
                ),
                Err(error) => (
                    None,
                    false,
                    format!("Đã đóng {closed} phiên Roblox · không đọc được dữ liệu: {error}"),
                ),
            }
        } else {
            (None, false, "Không xác định được thư mục ứng dụng".into())
        };
        let web_accounts = Arc::new(RwLock::new(
            accounts
                .as_ref()
                .map(|s| s.records.clone())
                .unwrap_or_default(),
        ));
        let (web_commands, web_receiver) = mpsc::channel();
        let web_server = Policy::from_settings(&settings)
            .and_then(|p| WebServer::start(p, web_accounts.clone(), web_commands.clone()).ok());
        let web_data = paths
            .as_ref()
            .and_then(|p| p.state.parent())
            .map(|p| p.join("WebView2"));
        Self {
            paths,
            accounts,
            settings,
            theme,
            optimizer: Optimizer::start(state),
            mutex: None,
            events,
            receiver,
            web_accounts,
            web_commands,
            web_receiver,
            web_server,
            selected: BTreeSet::new(),
            logo,
            background: None,
            background_loaded: String::new(),
            media: None,
            show_optimizer: false,
            show_settings: false,
            show_theme: false,
            show_add: false,
            show_password,
            show_control: false,
            show_browser: false,
            browser: None,
            browser_request: None,
            web_context: wry::WebContext::new(web_data),
            settings_tab: 0,
            cookie_input: String::new(),
            password_input: String::new(),
            place_id: "5315046213".into(),
            job_id: String::new(),
            follow_id: String::new(),
            alias_input: String::new(),
            description_input: String::new(),
            group_input: String::new(),
            field_key: String::new(),
            field_value: String::new(),
            interval_input,
            status,
            toast_until: Instant::now() + Duration::from_secs(5),
            hide_usernames: false,
            multi_backoff: Duration::from_millis(100),
            next_mutex_try: Instant::now(),
            pending_launch: None,
            presence: HashMap::new(),
            next_presence: Instant::now(),
        }
    }
    fn toast(&mut self, message: impl Into<String>) {
        self.status = message.into();
        self.toast_until = Instant::now() + Duration::from_secs(5);
    }
    fn events(&mut self) {
        while let Ok(event) = self.receiver.try_recv() {
            match event {
                Event::Added(Ok(account)) => {
                    let name = account.username();
                    if let Some(store) = &mut self.accounts {
                        store.add(account);
                        let result = store.save();
                        self.sync_web_accounts();
                        self.toast(
                            result
                                .map(|_| format!("Đã thêm {name}"))
                                .unwrap_or_else(|e| format!("Không lưu được: {e}")),
                        );
                    }
                }
                Event::Added(Err(error)) => self.toast(error),
                Event::Launched(name, Ok(())) => self.toast(format!("Đã gửi lệnh mở {name}")),
                Event::Launched(name, Err(error)) => {
                    self.log_error(&name, &error);
                    self.toast(format!("{name}: {error}"));
                }
                Event::Shuffled(Ok(job)) => {
                    self.job_id = job;
                    self.toast("Đã chọn server còn chỗ");
                }
                Event::Shuffled(Err(error)) => self.toast(error),
                Event::Presence(Ok(status)) => self.presence = status,
                Event::Presence(Err(_)) => {}
            }
        }
        while let Ok(command) = self.web_receiver.try_recv() {
            match command {
                WebCommand::Launch {
                    username,
                    place_id,
                    job_id,
                    follow,
                } => {
                    let account = self
                        .accounts
                        .as_ref()
                        .and_then(|s| {
                            s.records
                                .iter()
                                .find(|a| a.username().eq_ignore_ascii_case(&username))
                        })
                        .cloned();
                    if let Some(account) = account {
                        let sender = self.events.clone();
                        let config = LaunchConfig::from_settings(&self.settings);
                        thread::spawn(move || {
                            let result =
                                account_ops::launch(&account, place_id, &job_id, follow, &config)
                                    .map_err(|e| e.to_string());
                            let _ = sender.send(Event::Launched(username, result));
                        });
                    }
                }
                WebCommand::SetField {
                    username,
                    field,
                    value,
                } => {
                    if let Some(store) = &mut self.accounts {
                        if let Some(account) = store
                            .records
                            .iter_mut()
                            .find(|a| a.username().eq_ignore_ascii_case(&username))
                        {
                            account.set_text(&field, value);
                            let _ = store.save();
                            self.sync_web_accounts();
                        }
                    }
                }
            }
        }
    }
    fn sync_web_accounts(&self) {
        if let Some(store) = &self.accounts {
            *self.web_accounts.write().unwrap() = store.records.clone();
        }
    }
    fn restart_webserver(&mut self) {
        self.web_server = None;
        if let Some(policy) = Policy::from_settings(&self.settings) {
            match WebServer::start(policy, self.web_accounts.clone(), self.web_commands.clone()) {
                Ok(server) => self.web_server = Some(server),
                Err(error) => self.toast(format!("Web server không khởi động: {error}")),
            }
        }
    }
    fn log_error(&self, name: &str, error: &str) {
        if let Some(paths) = &self.paths {
            use std::io::Write;
            if let Ok(mut file) = std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&paths.errors)
            {
                let _ = writeln!(
                    file,
                    "{} | {} | {}",
                    chrono::Local::now().format("%F %T"),
                    name,
                    error
                );
            }
        }
    }
    fn selected_account(&self) -> Option<&AccountRecord> {
        self.selected
            .iter()
            .next()
            .and_then(|index| self.accounts.as_ref()?.records.get(*index))
    }
    fn select(&mut self, index: usize, ctrl: bool) {
        if ctrl {
            if !self.selected.insert(index) {
                self.selected.remove(&index);
            }
        } else {
            self.selected.clear();
            self.selected.insert(index);
        }
        if let Some((alias, description, group, place, job)) =
            self.selected_account().map(|account| {
                (
                    account.alias(),
                    account.description(),
                    account.text("Group"),
                    account.custom("SavedPlaceId"),
                    account.custom("SavedJobId"),
                )
            })
        {
            self.alias_input = alias;
            self.description_input = description;
            self.group_input = group;
            if !place.is_empty() {
                self.place_id = place;
            }
            if !job.is_empty() {
                self.job_id = job;
            }
        }
    }
    fn save_field(&mut self, key: &str, value: String) {
        if let Some(store) = &mut self.accounts {
            for index in &self.selected {
                if let Some(account) = store.records.get_mut(*index) {
                    account.set_text(key, value.clone());
                }
            }
            let result = store.save();
            self.sync_web_accounts();
            self.toast(
                result
                    .map(|_| "Đã lưu".to_owned())
                    .unwrap_or_else(|e| format!("Không lưu được: {e}")),
            );
        }
    }
    fn remove_selected(&mut self) {
        if let Some(store) = &mut self.accounts {
            let chosen: Vec<usize> = self.selected.iter().copied().collect();
            store.remove_indices(&chosen);
            self.selected.clear();
            let result = store.save();
            self.sync_web_accounts();
            self.toast(
                result
                    .map(|_| "Đã xóa tài khoản".to_owned())
                    .unwrap_or_else(|e| format!("Không lưu được: {e}")),
            );
        }
    }
    fn save_server_for_selected(&mut self) {
        if let Some(store) = &mut self.accounts {
            for index in &self.selected {
                if let Some(account) = store.records.get_mut(*index) {
                    account.set_custom("SavedPlaceId", self.place_id.clone());
                    account.set_custom("SavedJobId", self.job_id.clone());
                }
            }
            let result = store.save();
            self.sync_web_accounts();
            self.toast(
                result
                    .map(|_| "Đã lưu Place/Job cho tài khoản".to_owned())
                    .unwrap_or_else(|e| format!("Không lưu được: {e}")),
            );
        }
    }
    fn save_custom_field(&mut self) {
        let key = self.field_key.trim().to_owned();
        if key.is_empty() || key.len() > 100 {
            self.toast("Tên trường không hợp lệ");
            return;
        }
        if let Some(store) = &mut self.accounts {
            for index in &self.selected {
                if let Some(account) = store.records.get_mut(*index) {
                    account.set_custom(&key, self.field_value.clone());
                }
            }
            let result = store.save();
            self.sync_web_accounts();
            self.toast(
                result
                    .map(|_| "Đã lưu trường tùy chỉnh".to_owned())
                    .unwrap_or_else(|e| format!("Không lưu được: {e}")),
            );
        }
    }
    fn clear_saved_server(&mut self) {
        if let Some(store) = &mut self.accounts {
            for index in &self.selected {
                if let Some(account) = store.records.get_mut(*index) {
                    account.remove_custom("SavedPlaceId");
                    account.remove_custom("SavedJobId");
                }
            }
            let result = store.save();
            self.sync_web_accounts();
            self.toast(
                result
                    .map(|_| "Đã xóa Place/Job đã lưu".to_owned())
                    .unwrap_or_else(|e| format!("Không lưu được: {e}")),
            );
        }
    }
    fn add_cookie(&mut self) {
        let cookie = self.cookie_input.trim().to_owned();
        if cookie.is_empty() {
            self.toast("Nhập cookie Roblox");
            return;
        }
        self.cookie_input.clear();
        self.show_add = false;
        let sender = self.events.clone();
        thread::spawn(move || {
            let result = account_ops::validate_cookie(&cookie).map_err(|e| e.to_string());
            let _ = sender.send(Event::Added(result));
        });
        self.toast("Đang kiểm tra tài khoản…");
    }
    fn open_browser(&mut self, account: Option<AccountRecord>) {
        self.browser = None;
        self.browser_request = account;
        self.show_browser = true;
    }
    fn shuffle_server(&mut self) {
        let place_id = match self.place_id.trim().parse::<u64>() {
            Ok(id) if id > 0 => id,
            _ => {
                self.toast("Place ID không hợp lệ");
                return;
            }
        };
        let lowest = self
            .settings
            .bool("General", "ShuffleChoosesLowestServer", false);
        let pages = self.settings.number("General", "ShufflePageCount", 3_usize);
        let sender = self.events.clone();
        thread::spawn(move || {
            let result =
                account_ops::random_job(place_id, lowest, pages).map_err(|e| e.to_string());
            let _ = sender.send(Event::Shuffled(result));
        });
        self.toast("Đang tìm server…");
    }
    fn refresh_presence(&mut self) {
        if !self.settings.bool("General", "ShowPresence", false) {
            self.presence.clear();
            return;
        }
        if Instant::now() < self.next_presence {
            return;
        }
        let minutes = self
            .settings
            .number("General", "PresenceUpdateRate", 30_u64)
            .clamp(1, 120);
        self.next_presence = Instant::now() + Duration::from_secs(minutes * 60);
        let ids: Vec<u64> = self
            .accounts
            .as_ref()
            .map(|s| {
                s.records
                    .iter()
                    .map(AccountRecord::user_id)
                    .filter(|id| *id > 0)
                    .collect()
            })
            .unwrap_or_default();
        let sender = self.events.clone();
        thread::spawn(move || {
            let result = account_ops::fetch_presence(&ids).map_err(|e| e.to_string());
            let _ = sender.send(Event::Presence(result));
        });
    }
    fn launch_selected(&mut self, follow: bool) {
        let source = if follow {
            &self.follow_id
        } else {
            &self.place_id
        };
        let place_id = match source.trim().parse::<u64>() {
            Ok(id) if id > 0 => id,
            _ => {
                self.toast(if follow {
                    "Follow User ID không hợp lệ"
                } else {
                    "Place ID không hợp lệ"
                });
                return;
            }
        };
        let Some(store) = &self.accounts else {
            return;
        };
        let accounts: Vec<_> = self
            .selected
            .iter()
            .filter_map(|i| store.records.get(*i).cloned())
            .collect();
        if accounts.is_empty() {
            self.toast("Chọn tài khoản trước");
            return;
        }
        if accounts.len() > 1 && !self.settings.bool("General", "EnableMultiRbx", false) {
            self.toast("Bật Multi Roblox trong Settings");
            return;
        }
        let batch = PendingLaunch {
            accounts,
            place_id,
            job_id: self.job_id.clone(),
            follow,
            deadline: Instant::now() + Duration::from_secs(5),
        };
        if batch.accounts.len() > 1
            && !self
                .mutex
                .as_ref()
                .is_some_and(platform::RobloxMutex::is_owned)
        {
            self.pending_launch = Some(batch);
            self.next_mutex_try = Instant::now();
            self.toast("Đang chờ Multi Roblox sẵn sàng…");
            return;
        }
        self.start_launch_batch(batch);
    }
    fn start_launch_batch(&mut self, batch: PendingLaunch) {
        let delay = self
            .settings
            .number("General", "AccountJoinDelay", 1.0_f64)
            .clamp(0.0, 30.0);
        let stagger = self.settings.bool("General", "AsyncJoin", false);
        let mut config = LaunchConfig::from_settings(&self.settings);
        config.require_new_process = batch.accounts.len() > 1;
        for (index, account) in batch.accounts.into_iter().enumerate() {
            let sender = self.events.clone();
            let job = batch.job_id.clone();
            let config = config.clone();
            thread::spawn(move || {
                if stagger && index > 0 {
                    thread::sleep(Duration::from_secs_f64(delay * index as f64));
                }
                let name = account.username();
                let result =
                    account_ops::launch(&account, batch.place_id, &job, batch.follow, &config)
                        .map_err(|e| e.to_string());
                let _ = sender.send(Event::Launched(name, result));
            });
        }
        self.toast("Đang mở tài khoản đã chọn…");
    }
    fn update_mutex(&mut self) {
        if !self.settings.bool("General", "EnableMultiRbx", false) {
            self.mutex = None;
        } else if Instant::now() >= self.next_mutex_try {
            if self.mutex.is_none() {
                self.mutex = platform::RobloxMutex::create().ok();
            }
            if let Some(mutex) = &mut self.mutex {
                if mutex.try_acquire() {
                    self.multi_backoff = Duration::from_millis(100);
                } else {
                    self.multi_backoff = (self.multi_backoff * 2).min(Duration::from_secs(5));
                }
                self.next_mutex_try = Instant::now() + self.multi_backoff;
            } else {
                self.multi_backoff = (self.multi_backoff * 2).min(Duration::from_secs(5));
                self.next_mutex_try = Instant::now() + self.multi_backoff;
            }
        }
        if self.pending_launch.is_some()
            && self
                .mutex
                .as_ref()
                .is_some_and(platform::RobloxMutex::is_owned)
        {
            if let Some(batch) = self.pending_launch.take() {
                self.start_launch_batch(batch);
            }
        } else if self
            .pending_launch
            .as_ref()
            .is_some_and(|batch| Instant::now() >= batch.deadline)
        {
            self.pending_launch = None;
            self.toast("Multi Roblox chưa sẵn sàng sau 5 giây");
        }
    }
    fn load_background(&mut self, ctx: &egui::Context) {
        let Some(theme) = &self.theme else {
            return;
        };
        if theme.background != self.background_loaded {
            self.background_loaded = theme.background.clone();
            self.background = None;
            self.media = None;
            if !theme.background.is_empty() {
                self.media = Some(MediaWorker::start(theme.background.clone().into()));
            }
        }
        if let Some(worker) = &self.media {
            worker.set_paused(ctx.input(|i| i.viewport().minimized.unwrap_or(false)));
        }
        let event = self
            .media
            .as_ref()
            .and_then(|worker| worker.receiver.try_recv().ok());
        match event {
            Some(MediaEvent::Frame(frame)) => {
                let image = egui::ColorImage::from_rgba_unmultiplied(
                    [frame.width, frame.height],
                    &frame.pixels,
                );
                if let Some(texture) = &mut self.background {
                    texture.set(image, Default::default());
                } else {
                    self.background =
                        Some(ctx.load_texture("theme-background", image, Default::default()));
                }
            }
            Some(MediaEvent::Error(error)) => self.toast(error),
            None => {}
        }
    }
    fn background_ui(&self, ui: &mut egui::Ui) {
        if let (Some(texture), Some(theme)) = (&self.background, &self.theme) {
            let rect = ui.max_rect();
            let uv_all = egui::Rect::from_min_max(egui::pos2(0.0, 0.0), egui::pos2(1.0, 1.0));
            let source = texture.size_vec2() * (theme.scale as f32 / 100.0);
            if source.x <= 0.0 || source.y <= 0.0 {
                return;
            }
            let painter = ui.painter().with_clip_rect(rect);
            match theme.mode.as_str() {
                "Fit" => {
                    let size = source * (rect.width() / source.x).min(rect.height() / source.y);
                    painter.image(
                        texture.id(),
                        egui::Rect::from_center_size(rect.center(), size),
                        uv_all,
                        Color32::WHITE,
                    );
                }
                "Fill" => {
                    let base = texture.size_vec2();
                    let factor = (rect.width() / base.x).max(rect.height() / base.y)
                        * (theme.scale as f32 / 100.0);
                    let size = base * factor;
                    if size.x < rect.width() || size.y < rect.height() {
                        painter.image(
                            texture.id(),
                            egui::Rect::from_center_size(rect.center(), size),
                            uv_all,
                            Color32::WHITE,
                        );
                    } else {
                        let crop_x = ((size.x - rect.width()) / size.x / 2.0).max(0.0);
                        let crop_y = ((size.y - rect.height()) / size.y / 2.0).max(0.0);
                        let uv = egui::Rect::from_min_max(
                            egui::pos2(crop_x, crop_y),
                            egui::pos2(1.0 - crop_x, 1.0 - crop_y),
                        );
                        painter.image(texture.id(), rect, uv, Color32::WHITE);
                    }
                }
                "Center" => {
                    painter.image(
                        texture.id(),
                        egui::Rect::from_center_size(rect.center(), source),
                        uv_all,
                        Color32::WHITE,
                    );
                }
                "Tile" => {
                    let mut y = rect.top();
                    let mut count = 0;
                    while y < rect.bottom() && count < 1000 {
                        let mut x = rect.left();
                        while x < rect.right() && count < 1000 {
                            painter.image(
                                texture.id(),
                                egui::Rect::from_min_size(egui::pos2(x, y), source),
                                uv_all,
                                Color32::WHITE,
                            );
                            x += source.x;
                            count += 1;
                        }
                        y += source.y;
                    }
                }
                _ => {
                    painter.image(texture.id(), rect, uv_all, Color32::WHITE);
                }
            }
            let alpha = (theme.tint as u16 * 255 / 100) as u8;
            painter.rect_filled(
                rect,
                0.0,
                Color32::from_rgba_unmultiplied(13, 13, 26, alpha),
            );
        }
    }
}

impl eframe::App for MizuApp {
    fn logic(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if let Some(worker) = &self.media {
            worker.set_paused(ctx.input(|i| i.viewport().minimized.unwrap_or(false)));
        }
        ctx.request_repaint_after(Duration::from_millis(250));
    }
    fn ui(&mut self, ui: &mut egui::Ui, frame: &mut eframe::Frame) {
        self.events();
        self.update_mutex();
        self.refresh_presence();
        self.load_background(ui.ctx());
        self.background_ui(ui);
        self.header(ui);
        ui.add_space(3.0);
        self.content(ui);
        ui.add_space(3.0);
        self.footer(ui);
        self.dialogs(ui.ctx());
        self.browser_dialog(ui.ctx(), frame);
        let interval = if self.media.as_ref().is_some_and(|m| m.animated) {
            16
        } else if self.show_optimizer {
            100
        } else {
            500
        };
        ui.ctx()
            .request_repaint_after(Duration::from_millis(interval));
    }
}

impl MizuApp {
    fn header(&mut self, ui: &mut egui::Ui) {
        ui.allocate_ui_with_layout(
            egui::vec2(ui.available_width(), 27.0),
            egui::Layout::right_to_left(egui::Align::Center),
            |ui| {
                if ui
                    .add(egui::Button::image(
                        egui::Image::new(&self.logo).fit_to_exact_size(egui::vec2(20.0, 20.0)),
                    ))
                    .on_hover_text("Roblox")
                    .clicked()
                {
                    self.show_optimizer = true;
                }
                if ui.button("⚙").on_hover_text("Settings").clicked() {
                    self.show_settings = true;
                }
            },
        );
    }
    fn content(&mut self, ui: &mut egui::Ui) {
        let height = (ui.available_height() - 49.0).max(215.0);
        let left_width = (ui.available_width() * 0.62).max(420.0);
        let right_width = (ui.available_width() - left_width - 8.0).max(270.0);
        ui.horizontal(|ui| {
            ui.allocate_ui_with_layout(
                egui::vec2(left_width, height),
                egui::Layout::top_down(egui::Align::LEFT),
                |left| {
                    egui::Frame::new()
                        .fill(GLASS)
                        .stroke(egui::Stroke::new(1.0, GLASS_BORDER))
                        .corner_radius(0)
                        .inner_margin(3)
                        .show(left, |ui| {
                            ui.set_min_height(height - 6.0);
                            account_header(ui);
                            ui.separator();
                            let rows: Vec<_> = self
                                .accounts
                                .as_ref()
                                .map(|s| {
                                    s.records
                                        .iter()
                                        .map(|a| {
                                            (a.username(), a.alias(), a.description(), a.user_id())
                                        })
                                        .collect()
                                })
                                .unwrap_or_default();
                            egui::ScrollArea::vertical()
                                .max_height((height - 35.0).max(100.0))
                                .show(ui, |ui| {
                                    for (index, (username, alias, description, user_id)) in
                                        rows.into_iter().enumerate()
                                    {
                                        let caption = if self.hide_usernames {
                                            "••••••".to_owned()
                                        } else {
                                            username.clone()
                                        };
                                        let response = account_row(
                                            ui,
                                            self.selected.contains(&index),
                                            &caption,
                                            &alias,
                                            description.lines().next().unwrap_or(""),
                                            self.presence.get(&user_id).copied(),
                                        );
                                        if response.clicked() {
                                            self.select(index, ui.input(|i| i.modifiers.ctrl));
                                        }
                                        response.context_menu(|ui| {
                                            if ui.button("Launch").clicked() {
                                                self.select(index, false);
                                                self.launch_selected(false);
                                                ui.close();
                                            }
                                            if ui.button("Copy Username").clicked() {
                                                ui.ctx().copy_text(username.clone());
                                                ui.close();
                                            }
                                            if ui.button("Copy Cookie").clicked() {
                                                if let Some(a) = self
                                                    .accounts
                                                    .as_ref()
                                                    .and_then(|s| s.records.get(index))
                                                {
                                                    ui.ctx().copy_text(a.cookie());
                                                }
                                                ui.close();
                                            }
                                            if ui.button("Set Alias").clicked() {
                                                self.select(index, false);
                                                self.toast("Sửa Alias ở khung bên phải");
                                                ui.close();
                                            }
                                            if ui.button("Set Description").clicked() {
                                                self.select(index, false);
                                                self.toast("Sửa Description ở khung bên phải");
                                                ui.close();
                                            }
                                            if ui.button("Open Profile").clicked() {
                                                if let Some(a) = self
                                                    .accounts
                                                    .as_ref()
                                                    .and_then(|s| s.records.get(index))
                                                {
                                                    let _ = platform::open_url(&format!(
                                                        "https://www.roblox.com/users/{}/profile",
                                                        a.user_id()
                                                    ));
                                                }
                                                ui.close();
                                            }
                                            if ui.button("Remove").clicked() {
                                                self.select(index, false);
                                                self.remove_selected();
                                                ui.close();
                                            }
                                        });
                                    }
                                });
                        });
                },
            );
            ui.allocate_ui_with_layout(
                egui::vec2(right_width, height),
                egui::Layout::top_down(egui::Align::LEFT),
                |right| {
                    egui::ScrollArea::vertical()
                        .max_height(height)
                        .show(right, |right| {
                            egui::Frame::new()
                                .fill(Color32::TRANSPARENT)
                                .corner_radius(0)
                                .inner_margin(2)
                                .show(right, |ui| {
                                    ui.spacing_mut().item_spacing.y = 3.0;
                                    ui.label("Current Place");
                                    ui.horizontal(|ui| {
                                        ui.label("Place ID");
                                        ui.text_edit_singleline(&mut self.place_id);
                                    });
                                    ui.horizontal(|ui| {
                                        ui.label("Job ID");
                                        ui.text_edit_singleline(&mut self.job_id);
                                        if ui
                                            .button("⤨")
                                            .on_hover_text("Chọn server ngẫu nhiên")
                                            .clicked()
                                        {
                                            self.shuffle_server();
                                        }
                                    });
                                    ui.horizontal(|ui| {
                                        if primary(ui, "Join Server").clicked() {
                                            self.launch_selected(false);
                                        }
                                        ui.menu_button("Utilities", |ui| {
                                            if ui.button("Mở trang game").clicked() {
                                                let _ = platform::open_url(&format!(
                                                    "https://www.roblox.com/games/{}",
                                                    self.place_id
                                                ));
                                                ui.close();
                                            }
                                        });
                                    });
                                    ui.separator();
                                    let username = self
                                        .selected_account()
                                        .map(|a| a.username())
                                        .unwrap_or_default();
                                    ui.label(format!(
                                        "Username: {}",
                                        if self.hide_usernames && !username.is_empty() {
                                            "••••••"
                                        } else {
                                            &username
                                        }
                                    ));
                                    ui.label("Alias");
                                    ui.text_edit_singleline(&mut self.alias_input);
                                    ui.label("Description");
                                    ui.add(
                                        egui::TextEdit::multiline(&mut self.description_input)
                                            .desired_rows(3),
                                    );
                                    ui.horizontal(|ui| {
                                        ui.label("Follow User ID");
                                        ui.text_edit_singleline(&mut self.follow_id);
                                    });
                                    ui.horizontal(|ui| {
                                        if ui.button("Follow").clicked() {
                                            self.launch_selected(true);
                                        }
                                        if ui.button("Set Alias").clicked() {
                                            self.save_field("Alias", self.alias_input.clone());
                                        }
                                        if ui.button("Set Description").clicked() {
                                            self.save_field(
                                                "Description",
                                                self.description_input.clone(),
                                            );
                                        }
                                    });
                                    ui.horizontal(|ui| {
                                        ui.menu_button("Account Utilities", |ui| {
                                            if ui.button("Open Profile").clicked() {
                                                if let Some(a) = self.selected_account() {
                                                    let _ = platform::open_url(&format!(
                                                        "https://www.roblox.com/users/{}/profile",
                                                        a.user_id()
                                                    ));
                                                }
                                                ui.close();
                                            }
                                            if ui.button("Copy Username").clicked() {
                                                if let Some(a) = self.selected_account() {
                                                    ui.ctx().copy_text(a.username());
                                                }
                                                ui.close();
                                            }
                                            if ui.button("Copy Cookie").clicked() {
                                                if let Some(a) = self.selected_account() {
                                                    ui.ctx().copy_text(a.cookie());
                                                }
                                                ui.close();
                                            }
                                            if ui.button("Save Current Place/Job").clicked() {
                                                self.save_server_for_selected();
                                                ui.close();
                                            }
                                        });
                                    });
                                });
                        });
                },
            );
        });
    }
    fn footer(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            if ui.button("Add Account ▼").clicked() {
                self.show_add = true;
            }
            if ui.button("Remove").clicked() {
                self.remove_selected();
            }
            ui.checkbox(&mut self.hide_usernames, "Hide Usernames");
            ui.menu_button("Open Browser ▼", |ui| {
                if ui.button("Tài khoản đã chọn").clicked() {
                    self.open_browser(self.selected_account().cloned());
                    ui.close();
                }
                if ui.button("Đăng nhập tài khoản mới").clicked() {
                    self.open_browser(None);
                    ui.close();
                }
            });
            if ui.button("Discord").clicked() {
                let _ = platform::open_url("https://discord.com/users/598301893877432341");
            }
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.button("Account Control").clicked() {
                    self.show_control = true;
                }
                if ui.button("Edit Theme").clicked() {
                    self.show_theme = true;
                }
            });
        });
        if Instant::now() < self.toast_until {
            ui.label(RichText::new(&self.status).color(PURPLE));
        }
    }
    fn dialogs(&mut self, ctx: &egui::Context) {
        self.password_dialog(ctx);
        self.add_dialog(ctx);
        self.optimizer_dialog(ctx);
        self.settings_dialog(ctx);
        self.theme_dialog(ctx);
        self.control_dialog(ctx);
    }
    fn password_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_password {
            return;
        }
        let mut open = self.show_password;
        egui::Window::new("Mật khẩu dữ liệu")
            .open(&mut open)
            .collapsible(false)
            .show(ctx, |ui| {
                ui.label("Nhập mật khẩu AccountData.json cũ");
                ui.add(egui::TextEdit::singleline(&mut self.password_input).password(true));
                if primary(ui, "Mở dữ liệu").clicked() {
                    if let Some(paths) = &self.paths {
                        match AccountStore::load(&paths.account_data, Some(&self.password_input)) {
                            Ok(LoadResult::Ready(data)) => {
                                self.accounts = Some(data);
                                self.sync_web_accounts();
                                self.show_password = false;
                                self.password_input.clear();
                                self.toast("Đã mở dữ liệu");
                            }
                            Ok(LoadResult::PasswordRequired) => self.toast("Cần mật khẩu"),
                            Err(e) => self.toast(format!("Không mở được: {e}")),
                        }
                    }
                }
            });
        if self.show_password {
            self.show_password = open;
        }
    }
    fn add_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_add {
            return;
        }
        let mut open = self.show_add;
        egui::Window::new("Add Account")
            .open(&mut open)
            .show(ctx, |ui| {
                ui.label("Dán cookie .ROBLOSECURITY");
                ui.add(
                    egui::TextEdit::multiline(&mut self.cookie_input)
                        .password(true)
                        .desired_rows(3),
                );
                if primary(ui, "Thêm bằng cookie").clicked() {
                    self.add_cookie();
                }
                if ui.button("Đăng nhập trên Roblox").clicked() {
                    self.show_add = false;
                    self.open_browser(None);
                }
            });
        if self.show_add {
            self.show_add = open;
        }
    }
    fn optimizer_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_optimizer {
            return;
        }
        let mut open = self.show_optimizer;
        egui::Window::new("Roblox")
            .open(&mut open)
            .default_size([660.0, 430.0])
            .show(ctx, |ui| {
                ui.heading("Roblox");
                ui.add_space(4.0);
                egui::ScrollArea::vertical()
                    .max_height(225.0)
                    .show(ui, |ui| {
                        egui::Grid::new("session-table")
                            .num_columns(4)
                            .striped(true)
                            .spacing([20.0, 8.0])
                            .show(ui, |ui| {
                                for heading in ["PID", "RAM", "CPU", "THỜI GIAN"] {
                                    ui.strong(heading);
                                }
                                ui.end_row();
                                let view = self.optimizer.snapshot.lock().expect("optimizer view");
                                for session in &view.sessions {
                                    if let Some(p) = &session.process {
                                        let pid_label = if session.error.is_empty() {
                                            p.pid.to_string()
                                        } else {
                                            format!("{} ⚠", p.pid)
                                        };
                                        ui.label(pid_label).on_hover_text(&session.error);
                                        ui.label(format!(
                                            "{:.0} MB",
                                            p.working_set as f64 / 1048576.0
                                        ));
                                        ui.label(format!("{:.1}%", session.cpu_percent));
                                        let age = chrono::Utc::now().timestamp().saturating_sub(
                                            (p.started_ticks / 10_000_000)
                                                .saturating_sub(11_644_473_600)
                                                as i64,
                                        );
                                        ui.label(format!("{}g {}p", age / 3600, (age % 3600) / 60));
                                        ui.end_row();
                                    }
                                }
                            });
                    });
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    ui.label("Chu kỳ");
                    ui.add(
                        egui::DragValue::new(&mut self.interval_input)
                            .range(0.1..=3600.0)
                            .speed(0.1)
                            .fixed_decimals(1),
                    );
                    ui.label("giây");
                    if primary(ui, "Áp dụng").clicked() {
                        self.optimizer
                            .state
                            .lock()
                            .expect("optimizer state")
                            .interval_seconds = self.interval_input.clamp(0.1, 3600.0);
                        self.save_optimizer();
                    }
                    let enabled = self
                        .optimizer
                        .state
                        .lock()
                        .expect("optimizer state")
                        .auto_enabled;
                    if ui
                        .button(if enabled { "Dừng Auto" } else { "Bật Auto" })
                        .clicked()
                    {
                        self.optimizer
                            .state
                            .lock()
                            .expect("optimizer state")
                            .auto_enabled = !enabled;
                        self.save_optimizer();
                    }
                });
                ui.horizontal(|ui| {
                    ui.label("CPU");
                    let mut threads = self
                        .optimizer
                        .state
                        .lock()
                        .expect("optimizer state")
                        .cpu_threads;
                    egui::ComboBox::from_id_salt("cpu-mode")
                        .selected_text(match threads {
                            1 => "1 luồng",
                            2 => "2 luồng",
                            _ => "Không giới hạn",
                        })
                        .show_ui(ui, |ui| {
                            ui.selectable_value(&mut threads, 0, "Không giới hạn");
                            ui.selectable_value(&mut threads, 1, "1 luồng");
                            ui.selectable_value(&mut threads, 2, "2 luồng");
                        });
                    if threads
                        != self
                            .optimizer
                            .state
                            .lock()
                            .expect("optimizer state")
                            .cpu_threads
                    {
                        self.optimizer
                            .state
                            .lock()
                            .expect("optimizer state")
                            .cpu_threads = threads;
                        self.save_optimizer();
                    }
                });
                ui.horizontal(|ui| {
                    ui.label("RAM").on_hover_text(
                        "Giới hạn working set, không giới hạn tổng bộ nhớ Roblox cấp phát",
                    );
                    let mut limit = self
                        .optimizer
                        .state
                        .lock()
                        .expect("optimizer state")
                        .ram_limit_mb;
                    egui::ComboBox::from_id_salt("ram-limit")
                        .selected_text(if limit == 0 {
                            "Thu hồi tối đa".to_owned()
                        } else if limit == 3000 {
                            "3000 MB (≈3 GB)".to_owned()
                        } else {
                            format!("{limit} MB")
                        })
                        .show_ui(ui, |ui| {
                            ui.selectable_value(&mut limit, 0, "Thu hồi tối đa");
                            for step in 1..=30_u16 {
                                let mb = step * 100;
                                let label = if mb == 3000 {
                                    "3000 MB (≈3 GB)".to_owned()
                                } else {
                                    format!("{mb} MB")
                                };
                                ui.selectable_value(&mut limit, mb, label);
                            }
                        });
                    if limit
                        != self
                            .optimizer
                            .state
                            .lock()
                            .expect("optimizer state")
                            .ram_limit_mb
                    {
                        self.optimizer
                            .state
                            .lock()
                            .expect("optimizer state")
                            .ram_limit_mb = limit;
                        self.save_optimizer();
                    }
                });
                let view = self.optimizer.snapshot.lock().expect("optimizer view");
                if self
                    .optimizer
                    .state
                    .lock()
                    .expect("optimizer state")
                    .ram_limit_mb
                    == 0
                {
                    ui.label(format!(
                        "Theo dõi {} phiên · lượt cuối {:.0} MB lúc {}",
                        view.sessions.len(),
                        view.last_trimmed_mb,
                        view.last_trim_at
                    ));
                } else {
                    let errors = view.sessions.iter().filter(|s| !s.error.is_empty()).count();
                    ui.label(format!(
                        "Theo dõi {} phiên · giới hạn RAM · {} lỗi",
                        view.sessions.len(),
                        errors
                    ));
                }
            });
        self.show_optimizer = open;
    }
    fn save_optimizer(&mut self) {
        if let Some(paths) = &self.paths {
            let state = self
                .optimizer
                .state
                .lock()
                .expect("optimizer state")
                .clone();
            if let Err(error) = state.save(&paths.state) {
                self.toast(format!("Không lưu được tối ưu: {error}"));
            }
        }
    }
    fn settings_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_settings {
            return;
        }
        let mut open = self.show_settings;
        egui::Window::new("Settings")
            .open(&mut open)
            .default_size([550.0, 500.0])
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    let mut tabs = Vec::new();
                    for (id, title) in ["General", "Developer", "Miscellaneous"].iter().enumerate()
                    {
                        let response = ui.selectable_label(self.settings_tab == id, *title);
                        if response.clicked() {
                            self.settings_tab = id;
                        }
                        tabs.push(response.rect);
                    }
                    if let Some(active) = tabs.get(self.settings_tab) {
                        let x = ui.ctx().animate_value_with_time(
                            ui.scope_id().with("tab-line"),
                            active.left(),
                            0.2,
                        );
                        let width = ui.ctx().animate_value_with_time(
                            ui.scope_id().with("tab-width"),
                            active.width(),
                            0.2,
                        );
                        ui.painter().line_segment(
                            [
                                egui::pos2(x, active.bottom() + 3.0),
                                egui::pos2(x + width, active.bottom() + 3.0),
                            ],
                            egui::Stroke::new(2.0, PINK),
                        );
                    }
                });
                ui.separator();
                match self.settings_tab {
                    0 => {
                        for (key, title, default) in [
                            ("CheckForUpdates", "Check for Updates", true),
                            ("AsyncJoin", "Async Launching", false),
                            ("SavePasswords", "Save Passwords", false),
                            ("DisableAgingAlert", "Disable Aging Alert", false),
                            ("HideRbxAlert", "Hide Multi Roblox Alert", false),
                            ("RunOnStartup", "Run on Windows Startup", false),
                            (
                                "ShuffleChoosesLowestServer",
                                "Shuffle Chooses Lowest Server",
                                false,
                            ),
                            ("EnableMultiRbx", "Multi Roblox", false),
                            ("AutoCookieRefresh", "Auto Refresh Cookies", false),
                        ] {
                            self.setting_bool(ui, "General", key, title, default);
                        }
                        self.setting_num(
                            ui,
                            "General",
                            "AccountJoinDelay",
                            "Launch Delay (giây)",
                            1.0,
                            0.0..=30.0,
                        );
                        self.setting_text(
                            ui,
                            "General",
                            "ServerRegionFormat",
                            "Region Format",
                            "<city>, <countryCode>",
                        );
                        self.setting_num(
                            ui,
                            "General",
                            "MaxRecentGames",
                            "Max Recent Games",
                            10.0,
                            1.0..=100.0,
                        );
                    }
                    1 => {
                        self.setting_bool(
                            ui,
                            "Developer",
                            "DevMode",
                            "Enable Developer Mode",
                            false,
                        );
                        self.setting_bool(
                            ui,
                            "Developer",
                            "EnableWebServer",
                            "Enable Web Server",
                            false,
                        );
                        self.setting_num(
                            ui,
                            "WebServer",
                            "WebServerPort",
                            "Port",
                            7963.0,
                            1.0..=65535.0,
                        );
                        for (key, title, default) in [
                            (
                                "EveryRequestRequiresPassword",
                                "Every Request Requires Password",
                                true,
                            ),
                            ("AllowGetCookie", "Allow GetCookie Method", false),
                            ("AllowGetAccounts", "Allow GetAccounts Method", false),
                            ("AllowLaunchAccount", "Allow LaunchAccount Method", false),
                            (
                                "AllowAccountEditing",
                                "Allow Account Modification Methods",
                                false,
                            ),
                            (
                                "AllowExternalConnections",
                                "Allow External Connections",
                                false,
                            ),
                        ] {
                            self.setting_bool(ui, "WebServer", key, title, default);
                        }
                        self.setting_bool(
                            ui,
                            "General",
                            "DisableImages",
                            "Disable Image Loading",
                            false,
                        );
                        let mut password = self
                            .settings
                            .get("WebServer", "Password")
                            .unwrap_or("")
                            .to_owned();
                        ui.horizontal(|ui| {
                            ui.label("Webserver Password");
                            if ui
                                .add(egui::TextEdit::singleline(&mut password).password(true))
                                .changed()
                            {
                                self.settings.set("WebServer", "Password", &password);
                            }
                        });
                    }
                    _ => {
                        self.setting_bool(
                            ui,
                            "General",
                            "ShowPresence",
                            "Show Account Presence",
                            false,
                        );
                        self.setting_num(
                            ui,
                            "General",
                            "PresenceUpdateRate",
                            "Refresh (phút)",
                            30.0,
                            1.0..=120.0,
                        );
                        self.setting_bool(ui, "General", "UnlockFPS", "Unlock FPS", false);
                        self.setting_num(
                            ui,
                            "General",
                            "MaxFPSValue",
                            "Max FPS",
                            120.0,
                            30.0..=1000.0,
                        );
                        self.custom_settings_ui(ui);
                        if ui.button("Force Update").clicked() {
                            self.toast("Cập nhật tự động chưa được triển khai");
                        }
                    }
                }
                ui.separator();
                if primary(ui, "Lưu Settings").clicked() {
                    if let Some(paths) = &self.paths {
                        let result = self.settings.save(&paths.settings).and_then(|_| {
                            std::env::current_exe().and_then(|exe| {
                                platform::set_startup(
                                    self.settings.bool("General", "RunOnStartup", false),
                                    &exe,
                                )
                            })
                        });
                        self.restart_webserver();
                        self.toast(
                            result
                                .map(|_| "Đã lưu Settings".to_owned())
                                .unwrap_or_else(|e| format!("Không lưu được: {e}")),
                        );
                    }
                }
                if ui.button("Edit Theme").clicked() {
                    self.show_theme = true;
                }
            });
        self.show_settings = open;
    }
    fn setting_bool(
        &mut self,
        ui: &mut egui::Ui,
        section: &str,
        key: &str,
        title: &str,
        fallback: bool,
    ) {
        let mut checked = self.settings.bool(section, key, fallback);
        if ui.checkbox(&mut checked, title).changed() {
            self.settings.set_bool(section, key, checked);
        }
    }
    fn setting_num(
        &mut self,
        ui: &mut egui::Ui,
        section: &str,
        key: &str,
        title: &str,
        fallback: f64,
        range: std::ops::RangeInclusive<f64>,
    ) {
        let mut number = self.settings.number(section, key, fallback);
        ui.horizontal(|ui| {
            ui.label(title);
            if ui
                .add(egui::DragValue::new(&mut number).range(range))
                .changed()
            {
                self.settings.set(section, key, number.to_string());
            }
        });
    }
    fn setting_text(
        &mut self,
        ui: &mut egui::Ui,
        section: &str,
        key: &str,
        title: &str,
        fallback: &str,
    ) {
        let mut text = self
            .settings
            .get(section, key)
            .unwrap_or(fallback)
            .to_owned();
        ui.horizontal(|ui| {
            ui.label(title);
            if ui.text_edit_singleline(&mut text).changed() {
                self.settings.set(section, key, &text);
            }
        });
    }
    fn custom_settings_ui(&mut self, ui: &mut egui::Ui) {
        let current = self
            .settings
            .get("General", "CustomClientSettings")
            .unwrap_or("")
            .to_owned();
        let mut enabled = !current.is_empty() && current != "false" && current != "true";
        if ui
            .checkbox(&mut enabled, "Use Custom ClientAppSettings")
            .changed()
        {
            if enabled {
                let remembered = self
                    .settings
                    .get("General", "SavedCustomClientSettingsPath")
                    .unwrap_or("")
                    .to_owned();
                if std::path::Path::new(&remembered).is_file() {
                    self.settings
                        .set("General", "CustomClientSettings", remembered);
                } else if let Some(path) = rfd::FileDialog::new()
                    .add_filter("JSON", &["json"])
                    .pick_file()
                {
                    self.settings
                        .set("General", "CustomClientSettings", path.to_string_lossy());
                }
            } else {
                if !current.is_empty() {
                    self.settings
                        .set("General", "SavedCustomClientSettingsPath", &current);
                }
                self.settings.set("General", "CustomClientSettings", "");
            }
        }
        if enabled {
            ui.horizontal(|ui| {
                ui.label(
                    self.settings
                        .get("General", "CustomClientSettings")
                        .unwrap_or("Chưa chọn file"),
                );
                if ui.button("Chọn file…").clicked() {
                    if let Some(path) = rfd::FileDialog::new()
                        .add_filter("JSON", &["json"])
                        .pick_file()
                    {
                        self.settings.set(
                            "General",
                            "CustomClientSettings",
                            path.to_string_lossy(),
                        );
                    }
                }
            });
        }
    }
    fn theme_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_theme {
            return;
        }
        let mut open = self.show_theme;
        egui::Window::new("Edit Theme")
            .open(&mut open)
            .show(ctx, |ui| {
                if let Some(theme) = &mut self.theme {
                    ui.horizontal(|ui| {
                        ui.label("Ảnh nền");
                        ui.text_edit_singleline(&mut theme.background);
                        if ui.button("Chọn…").clicked() {
                            if let Some(path) = rfd::FileDialog::new()
                                .add_filter(
                                    "Ảnh hoặc video",
                                    &["png", "jpg", "jpeg", "webp", "gif", "mp4", "webm"],
                                )
                                .pick_file()
                            {
                                theme.background = path.to_string_lossy().into_owned();
                            }
                        }
                    });
                    egui::ComboBox::from_id_salt("background-fit")
                        .selected_text(&theme.mode)
                        .show_ui(ui, |ui| {
                            for mode in ["Fill", "Fit", "Stretch", "Center", "Tile"] {
                                ui.selectable_value(&mut theme.mode, mode.to_owned(), mode);
                            }
                        });
                    ui.add(egui::Slider::new(&mut theme.scale, 25..=300).text("Tỷ lệ %"));
                    ui.add(egui::Slider::new(&mut theme.tint, 0..=90).text("Độ tối %"));
                    if primary(ui, "Lưu Theme").clicked() {
                        let result = theme.save();
                        if result.is_ok() {
                            self.background_loaded.clear();
                        }
                        self.toast(
                            result
                                .map(|_| "Đã lưu Theme".to_owned())
                                .unwrap_or_else(|e| format!("Không lưu được: {e}")),
                        );
                    }
                }
            });
        self.show_theme = open;
    }
    fn control_dialog(&mut self, ctx: &egui::Context) {
        if !self.show_control {
            return;
        }
        let mut open = self.show_control;
        egui::Window::new("Account Control")
            .open(&mut open)
            .show(ctx, |ui| {
                ui.label(format!("{} tài khoản được chọn", self.selected.len()));
                ui.horizontal(|ui| {
                    ui.label("Group");
                    ui.text_edit_singleline(&mut self.group_input);
                    if ui.button("Set Group").clicked() {
                        self.save_field("Group", self.group_input.clone());
                    }
                });
                ui.separator();
                ui.label("Custom Field");
                ui.horizontal(|ui| {
                    ui.label("Key");
                    ui.text_edit_singleline(&mut self.field_key);
                });
                ui.horizontal(|ui| {
                    ui.label("Value");
                    ui.text_edit_singleline(&mut self.field_value);
                });
                if ui.button("Set Field").clicked() {
                    self.save_custom_field();
                }
                if ui.button("Clear saved Place/Job").clicked() {
                    self.clear_saved_server();
                }
            });
        self.show_control = open;
    }

    fn browser_dialog(&mut self, ctx: &egui::Context, frame: &mut eframe::Frame) {
        if !self.show_browser {
            return;
        }
        let mut open = self.show_browser;
        let mut browser_rect = None;
        let mut save_session = false;
        egui::Window::new("Roblox Browser")
            .open(&mut open)
            .collapsible(false)
            .default_size([720.0, 480.0])
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    if primary(ui, "Lưu tài khoản").clicked() {
                        save_session = true;
                    }
                    ui.label("Đăng nhập Roblox trong khung dưới, rồi nhấn Lưu tài khoản");
                });
                ui.add_space(6.0);
                let size = egui::vec2(
                    ui.available_width().max(420.0),
                    ui.available_height().max(360.0),
                );
                browser_rect = Some(ui.allocate_exact_size(size, egui::Sense::hover()).0);
            });
        self.show_browser = open;
        if !open {
            self.browser = None;
            self.browser_request = None;
            return;
        }
        if let Some(rect) = browser_rect {
            if let Some(browser) = &self.browser {
                browser.set_bounds(rect);
            } else if let Some(window) = frame.winit_window() {
                match Browser::new(
                    window.as_ref(),
                    rect,
                    self.browser_request.as_ref(),
                    &mut self.web_context,
                ) {
                    Ok(browser) => {
                        self.browser = Some(browser);
                        self.browser_request = None;
                    }
                    Err(error) => {
                        self.show_browser = false;
                        self.browser_request = None;
                        self.toast(error);
                    }
                }
            }
        }
        if save_session {
            match self.browser.as_ref().map(Browser::cookie) {
                Some(Ok(Some(cookie))) => {
                    let sender = self.events.clone();
                    thread::spawn(move || {
                        let result =
                            account_ops::validate_cookie(&cookie).map_err(|e| e.to_string());
                        let _ = sender.send(Event::Added(result));
                    });
                    self.toast("Đang lưu phiên đăng nhập…");
                }
                Some(Ok(None)) => self.toast("Chưa thấy phiên đăng nhập Roblox"),
                Some(Err(error)) => self.toast(error),
                None => self.toast("Trình duyệt chưa sẵn sàng"),
            }
        }
    }
}

fn texture(ctx: &egui::Context, name: &str, bytes: &[u8]) -> TextureHandle {
    let image = image::load_from_memory(bytes)
        .expect("embedded image")
        .to_rgba8();
    ctx.load_texture(
        name,
        egui::ColorImage::from_rgba_unmultiplied(
            [image.width() as usize, image.height() as usize],
            &image,
        ),
        Default::default(),
    )
}
fn primary(ui: &mut egui::Ui, label: &str) -> egui::Response {
    ui.add(
        egui::Button::new(label)
            .fill(Color32::from_rgb(77, 60, 99))
            .stroke(egui::Stroke::new(1.0, PURPLE))
            .corner_radius(0),
    )
}

fn account_header(ui: &mut egui::Ui) {
    let (rect, _) = ui.allocate_exact_size(
        egui::vec2(ui.available_width().max(100.0), 24.0),
        egui::Sense::hover(),
    );
    let widths = [0.36, 0.27, 0.37];
    let mut left = rect.left();
    for (label, fraction) in ["Username", "Alias", "Description"].into_iter().zip(widths) {
        ui.painter().text(
            egui::pos2(left + 10.0, rect.center().y),
            egui::Align2::LEFT_CENTER,
            label,
            egui::FontId::proportional(13.0),
            Color32::from_rgb(255, 242, 250),
        );
        left += rect.width() * fraction;
    }
}
fn account_row(
    ui: &mut egui::Ui,
    selected: bool,
    username: &str,
    alias: &str,
    description: &str,
    presence: Option<u8>,
) -> egui::Response {
    let size = egui::vec2(ui.available_width().max(100.0), 24.0);
    let (rect, response) = ui.allocate_exact_size(size, egui::Sense::click());
    let hover = if response.hovered() { 1.0 } else { 0.0 };
    if selected {
        ui.painter()
            .rect_filled(rect, 0, Color32::from_rgb(103, 86, 158));
    } else if hover > 0.0 {
        ui.painter()
            .rect_filled(rect, 0, Color32::from_rgb(57, 47, 73));
    }
    if let Some(kind) = presence {
        let color = match kind {
            1 => Color32::from_rgb(60, 190, 100),
            2 => Color32::from_rgb(70, 150, 255),
            3 => PURPLE,
            _ => Color32::GRAY,
        };
        ui.painter()
            .circle_filled(egui::pos2(rect.left() + 7.0, rect.center().y), 3.0, color);
    }
    let widths = [0.36, 0.27, 0.37];
    let mut left = rect.left();
    for (text, fraction) in [username, alias, description].into_iter().zip(widths) {
        let clip = egui::Rect::from_min_max(
            egui::pos2(left, rect.top()),
            egui::pos2(left + rect.width() * fraction - 5.0, rect.bottom()),
        );
        let padding = if left == rect.left() { 16.0 } else { 10.0 };
        ui.painter().with_clip_rect(clip).text(
            egui::pos2(left + padding, rect.center().y),
            egui::Align2::LEFT_CENTER,
            text,
            egui::FontId::proportional(13.0),
            Color32::WHITE,
        );
        left += rect.width() * fraction;
    }
    response
}
