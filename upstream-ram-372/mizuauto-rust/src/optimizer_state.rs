use serde::{Deserialize, Serialize};
use std::{fs, io, path::Path};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default)]
pub struct OptimizerState {
    pub interval_seconds: f64,
    pub cpu_threads: u8,
    pub auto_enabled: bool,
    /// 0 keeps the original EmptyWorkingSet mode; 100..=3000 enables a hard working-set maximum.
    pub ram_limit_mb: u16,
}

impl Default for OptimizerState {
    fn default() -> Self {
        Self {
            interval_seconds: 0.1,
            cpu_threads: 0,
            auto_enabled: true,
            ram_limit_mb: 0,
        }
    }
}

impl OptimizerState {
    pub fn load(path: &Path) -> Self {
        let mut result: Self = fs::read(path)
            .ok()
            .and_then(|s| serde_json::from_slice(&s).ok())
            .unwrap_or_default();
        result.interval_seconds = if result.interval_seconds.is_finite() {
            result.interval_seconds.clamp(0.1, 3600.0)
        } else {
            0.1
        };
        if !matches!(result.cpu_threads, 0 | 1 | 2) {
            result.cpu_threads = 0;
        }
        if result.ram_limit_mb > 3000 || result.ram_limit_mb % 100 != 0 {
            result.ram_limit_mb = 0;
        }
        result
    }

    pub fn save(&self, path: &Path) -> io::Result<()> {
        let data = serde_json::to_vec_pretty(self).map_err(io::Error::other)?;
        crate::platform::atomic_write(path, &data)
    }
}

