use std::{
    fs, io,
    path::{Path, PathBuf},
};

pub struct Paths {
    pub root: PathBuf,
    pub account_data: PathBuf,
    pub settings: PathBuf,
    pub theme: PathBuf,
    pub state: PathBuf,
    pub errors: PathBuf,
}

impl Paths {
    pub fn discover() -> io::Result<Self> {
        let root = std::env::current_exe()?
            .parent()
            .ok_or_else(|| io::Error::other("Missing executable directory"))?
            .to_path_buf();
        let local = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(|| root.clone());
        Ok(Self {
            account_data: root.join("AccountData.json"),
            settings: root.join("RAMSettings.ini"),
            theme: root.join("RAMTheme.ini"),
            errors: root.join("mizuauto_errors.log"),
            state: local.join("Mizuauto").join("state.json"),
            root,
        })
    }
}

pub fn logo_bytes() -> Vec<u8> {
    let mut locations = vec![PathBuf::from(r"D:\Downloads\mizulogo.png")];
    if let Ok(paths) = Paths::discover() {
        locations.push(paths.root.join("assets").join("mizulogo.png"));
    }
    for path in locations {
        if let Ok(bytes) = fs::read(path) {
            if image::load_from_memory(&bytes).is_ok() {
                return bytes;
            }
        }
    }
    include_bytes!("../resources/mizulogo.png").to_vec()
}

#[derive(Clone, Default)]
pub struct IniDoc {
    lines: Vec<String>,
}

impl IniDoc {
    pub fn load(path: &Path) -> io::Result<Self> {
        match fs::read(path) {
            Ok(bytes) => Ok(Self {
                lines: String::from_utf8_lossy(&bytes)
                    .lines()
                    .map(str::to_owned)
                    .collect(),
            }),
            Err(err) if err.kind() == io::ErrorKind::NotFound => Ok(Self::default()),
            Err(err) => Err(err),
        }
    }

    pub fn get(&self, section: &str, key: &str) -> Option<&str> {
        let mut active = false;
        for line in &self.lines {
            let line = line.trim().trim_start_matches('\u{feff}');
            if line.starts_with('[') && line.ends_with(']') {
                active = line[1..line.len() - 1].eq_ignore_ascii_case(section);
            } else if active && !line.starts_with('#') && !line.starts_with(';') {
                if let Some((name, value)) = line.split_once('=') {
                    if name.trim().eq_ignore_ascii_case(key) {
                        return Some(value.trim());
                    }
                }
            }
        }
        None
    }

    pub fn bool(&self, section: &str, key: &str, fallback: bool) -> bool {
        self.get(section, key)
            .map(|s| s.eq_ignore_ascii_case("true") || s == "1")
            .unwrap_or(fallback)
    }

    pub fn number<T: std::str::FromStr>(&self, section: &str, key: &str, fallback: T) -> T {
        self.get(section, key)
            .and_then(|s| s.parse().ok())
            .unwrap_or(fallback)
    }

    pub fn set(&mut self, section: &str, key: &str, value: impl AsRef<str>) {
        let replacement = format!("{key}={}", value.as_ref());
        let mut active = false;
        let mut section_start = None;
        let mut section_end = self.lines.len();
        for (index, line) in self.lines.iter().enumerate() {
            let trimmed = line.trim().trim_start_matches('\u{feff}');
            if trimmed.starts_with('[') && trimmed.ends_with(']') {
                if active {
                    section_end = index;
                    break;
                }
                active = trimmed[1..trimmed.len() - 1].eq_ignore_ascii_case(section);
                if active {
                    section_start = Some(index);
                }
            } else if active && let Some((name, _)) = trimmed.split_once('=') {
                if name.trim().eq_ignore_ascii_case(key) {
                    self.lines[index] = replacement;
                    return;
                }
            }
        }
        if section_start.is_some() {
            self.lines.insert(section_end, replacement);
        } else {
            if !self.lines.is_empty() {
                self.lines.push(String::new());
            }
            self.lines.push(format!("[{section}]"));
            self.lines.push(replacement);
        }
    }

    pub fn set_bool(&mut self, section: &str, key: &str, value: bool) {
        self.set(section, key, if value { "true" } else { "false" });
    }

    pub fn save(&self, path: &Path) -> io::Result<()> {
        crate::platform::atomic_write(path, format!("{}\n", self.lines.join("\n")).as_bytes())
    }
}

pub use crate::optimizer_state::OptimizerState;

#[derive(Clone)]
pub struct Theme {
    pub path: PathBuf,
    pub doc: IniDoc,
    pub background: String,
    pub mode: String,
    pub scale: u16,
    pub tint: u8,
}

impl Theme {
    pub fn load(path: &Path) -> io::Result<Self> {
        let doc = IniDoc::load(path)?;
        Ok(Self {
            path: path.to_path_buf(),
            background: doc
                .get("mizuauto", "BackgroundPath")
                .unwrap_or("")
                .to_owned(),
            mode: doc
                .get("mizuauto", "BackgroundMode")
                .unwrap_or("Fill")
                .to_owned(),
            scale: doc
                .number("mizuauto", "BackgroundScale", 100_u16)
                .clamp(25, 300),
            tint: doc.number("mizuauto", "BackgroundTint", 55_u8).min(90),
            doc,
        })
    }

    pub fn save(&mut self) -> io::Result<()> {
        self.doc.set("mizuauto", "BackgroundPath", &self.background);
        self.doc.set("mizuauto", "BackgroundMode", &self.mode);
        self.doc
            .set("mizuauto", "BackgroundScale", self.scale.to_string());
        self.doc
            .set("mizuauto", "BackgroundTint", self.tint.to_string());
        self.doc.save(&self.path)
    }
}
