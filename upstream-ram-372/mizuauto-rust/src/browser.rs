use crate::account_store::AccountRecord;
use eframe::egui;
use wry::{
    WebView, WebViewBuilder, WebViewBuilderExtWindows, dpi, raw_window_handle::HasWindowHandle,
};

pub struct Browser {
    webview: WebView,
}

fn bounds(rect: egui::Rect) -> wry::Rect {
    wry::Rect {
        position: dpi::LogicalPosition::new(rect.left() as f64, rect.top() as f64).into(),
        size: dpi::LogicalSize::new(rect.width() as f64, rect.height() as f64).into(),
    }
}

impl Browser {
    pub fn new<W: HasWindowHandle>(
        window: &W,
        rect: egui::Rect,
        account: Option<&AccountRecord>,
        context: &mut wry::WebContext,
    ) -> Result<Self, String> {
        let profile = account
            .map(|a| format!("MizuAccount{}", a.user_id()))
            .unwrap_or_else(|| "MizuLogin".to_owned());
        let webview = WebViewBuilder::new_with_web_context(context)
            .with_bounds(bounds(rect))
            .with_profile_name(profile)
            .build_as_child(window)
            .map_err(|_| "Không tạo được trình duyệt WebView2".to_owned())?;
        if let Some(account) = account {
            let cookie = account.cookie();
            let value = cookie
                .trim()
                .strip_prefix(".ROBLOSECURITY=")
                .unwrap_or(cookie.trim());
            let cookie = wry::cookie::Cookie::build((".ROBLOSECURITY", value.to_owned()))
                .domain(".roblox.com")
                .path("/")
                .secure(true)
                .http_only(true)
                .build();
            webview
                .set_cookie(&cookie)
                .map_err(|_| "Không cài được phiên đăng nhập".to_owned())?;
        }
        let page = if account.is_some() {
            "https://www.roblox.com/home"
        } else {
            "https://www.roblox.com/login"
        };
        webview
            .load_url(page)
            .map_err(|_| "Không mở được trang Roblox".to_owned())?;
        Ok(Self { webview })
    }

    pub fn set_bounds(&self, rect: egui::Rect) {
        let _ = self.webview.set_bounds(bounds(rect));
    }

    pub fn cookie(&self) -> Result<Option<String>, String> {
        let cookies = self
            .webview
            .cookies_for_url("https://www.roblox.com/")
            .map_err(|_| "Không đọc được phiên đăng nhập".to_owned())?;
        Ok(cookies
            .into_iter()
            .find(|c| c.name() == ".ROBLOSECURITY")
            .map(|c| c.value().to_owned()))
    }
}
