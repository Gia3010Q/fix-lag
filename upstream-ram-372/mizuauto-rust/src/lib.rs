//! Native optimizer backend for the original WinForms Mizuauto interface.

mod optimizer_state;
mod optimizer;
mod platform;

use optimizer_state::OptimizerState;
use optimizer::Optimizer;

struct Engine {
    optimizer: Optimizer,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct MizuSession {
    pub pid: u32,
    pub reserved: u32,
    pub working_set_bytes: u64,
    pub cpu_percent: f64,
    pub started_filetime: u64,
}

const _: () = assert!(core::mem::size_of::<MizuSession>() == 32);

#[unsafe(no_mangle)]
pub extern "system" fn mizu_engine_abi_version() -> u32 {
    1
}

#[unsafe(no_mangle)]
pub extern "system" fn mizu_engine_create() -> *mut core::ffi::c_void {
    let state = OptimizerState {
        auto_enabled: false,
        ..OptimizerState::default()
    };
    Box::into_raw(Box::new(Engine {
        optimizer: Optimizer::start(state),
    }))
    .cast()
}

/// The caller must stop all concurrent calls before destroying the engine.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn mizu_engine_destroy(engine: *mut core::ffi::c_void) {
    if !engine.is_null() {
        drop(unsafe { Box::from_raw(engine.cast::<Engine>()) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn mizu_engine_set_config(
    engine: *mut core::ffi::c_void,
    interval_seconds: f64,
    cpu_threads: u32,
    auto_enabled: i32,
    ram_limit_mb: u32,
) -> i32 {
    let Some(engine) = (unsafe { engine.cast::<Engine>().as_ref() }) else {
        return 0;
    };
    let Ok(mut state) = engine.optimizer.state.lock() else {
        return 0;
    };
    state.interval_seconds = if interval_seconds.is_finite() {
        interval_seconds.clamp(0.1, 3600.0)
    } else {
        0.1
    };
    state.cpu_threads = if matches!(cpu_threads, 1 | 2) {
        cpu_threads as u8
    } else {
        0
    };
    state.auto_enabled = auto_enabled != 0;
    state.ram_limit_mb = if ram_limit_mb <= 3000 && ram_limit_mb % 100 == 0 {
        ram_limit_mb as u16
    } else {
        0
    };
    1
}

/// Copies a consistent snapshot into `output`, returning the number of rows copied.
#[unsafe(no_mangle)]
pub unsafe extern "system" fn mizu_engine_sessions(
    engine: *const core::ffi::c_void,
    output: *mut MizuSession,
    capacity: u32,
) -> u32 {
    let Some(engine) = (unsafe { engine.cast::<Engine>().as_ref() }) else {
        return 0;
    };
    if output.is_null() || capacity == 0 {
        return 0;
    }
    let Ok(snapshot) = engine.optimizer.snapshot.lock() else {
        return 0;
    };
    let count = snapshot.sessions.len().min(capacity as usize);
    for (index, session) in snapshot.sessions.iter().take(count).enumerate() {
        let row = session
            .process
            .as_ref()
            .map(|process| MizuSession {
                pid: process.pid,
                reserved: 0,
                working_set_bytes: process.working_set,
                cpu_percent: session.cpu_percent,
                started_filetime: process.started_ticks,
            })
            .unwrap_or_default();
        unsafe {
            output.add(index).write(row);
        }
    }
    count as u32
}
