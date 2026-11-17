use crate::{config::IniDoc, platform};
use serde_json::{Map, Value};
use std::{
    fs, io,
    path::PathBuf,
    sync::{Mutex, OnceLock},
};

static PATCH_LOCK: OnceLock<Mutex<()>> = OnceLock::new();

#[derive(Clone)]
pub struct LaunchConfig {
    pub unlock_fps: bool,
    pub max_fps: u32,
    pub custom_file: Option<PathBuf>,
    pub require_new_process: bool,
}

impl LaunchConfig {
    pub fn from_settings(settings: &IniDoc) -> Self {
        let custom_file = settings
            .get("General", "CustomClientSettings")
            .filter(|s| !s.is_empty() && *s != "false" && *s != "true")
            .map(PathBuf::from);
        Self {
            unlock_fps: settings.bool("General", "UnlockFPS", false),
            max_fps: settings
                .number("General", "MaxFPSValue", 120_u32)
                .clamp(30, 1000),
            custom_file,
            require_new_process: false,
        }
    }
}

pub fn patch(config: &LaunchConfig) -> io::Result<()> {
    if !config.unlock_fps && config.custom_file.is_none() {
        return Ok(());
    }
    let _guard = PATCH_LOCK
        .get_or_init(|| Mutex::new(()))
        .lock()
        .expect("client settings lock");
    let versions = [
        std::env::var_os("LOCALAPPDATA").map(|p| PathBuf::from(p).join("Roblox").join("Versions")),
        Some(PathBuf::from(r"C:\Program Files (x86)\Roblox\Versions")),
    ];
    let folder = versions
        .into_iter()
        .flatten()
        .filter_map(|root| fs::read_dir(root).ok())
        .flatten()
        .filter_map(Result::ok)
        .filter_map(|entry| {
            let path = entry.path();
            if !path.file_name()?.to_string_lossy().starts_with("version-")
                || !path.join("RobloxPlayerLauncher.exe").is_file()
            {
                return None;
            }
            Some((entry.metadata().ok()?.modified().ok()?, path))
        })
        .max_by_key(|(modified, _)| *modified)
        .map(|(_, path)| path)
        .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "Không tìm được bản cài Roblox"))?;
    let target = folder.join("ClientSettings").join("ClientAppSettings.json");
    let contents = if let Some(custom) = &config.custom_file {
        let bytes = fs::read(custom)?;
        if !serde_json::from_slice::<Value>(&bytes).is_ok_and(|value| value.is_object()) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Custom ClientAppSettings phải là JSON object",
            ));
        }
        bytes
    } else {
        let mut doc: Map<String, Value> = if target.exists() {
            serde_json::from_slice(&fs::read(&target)?).map_err(|_| {
                io::Error::new(
                    io::ErrorKind::InvalidData,
                    "ClientAppSettings.json hiện có không hợp lệ",
                )
            })?
        } else {
            Map::new()
        };
        doc.insert(
            "DFIntTaskSchedulerTargetFps".into(),
            Value::Number(config.max_fps.into()),
        );
        serde_json::to_vec(&doc).map_err(io::Error::other)?
    };
    if target.exists() {
        let backup = target.with_extension("json.mizuauto.backup");
        if !backup.exists() {
            fs::copy(&target, backup)?;
        }
    }
    platform::atomic_write(&target, &contents)
}
