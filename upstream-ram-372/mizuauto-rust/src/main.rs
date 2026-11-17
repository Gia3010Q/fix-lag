#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod account_ops;
mod account_store;
mod browser;
mod client_settings;
mod config;
mod media;
mod optimizer;
mod optimizer_state;
mod platform;
mod ui;
mod web_server;

fn main() -> eframe::Result {
    let image = image::load_from_memory(&config::logo_bytes())
        .expect("embedded Mizuauto logo")
        .to_rgba8();
    let (width, height) = (image.width(), image.height());
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_inner_size([800.0, 420.0])
            .with_min_inner_size([760.0, 350.0])
            .with_icon(eframe::egui::IconData {
                rgba: image.into_raw(),
                width,
                height,
            }),
        ..Default::default()
    };
    eframe::run_native(
        "Mizuauto · Tài khoản Roblox",
        options,
        Box::new(|cc| Ok(Box::new(ui::MizuApp::new(cc)))),
    )
}
