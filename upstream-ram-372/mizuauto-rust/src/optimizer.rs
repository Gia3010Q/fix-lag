use crate::{optimizer_state::OptimizerState, platform};
use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

const TRIM_THRESHOLD: u64 = 384 * 1024 * 1024;

#[derive(Clone, Default)]
pub struct Session {
    pub process: Option<platform::ProcessInfo>,
    pub cpu_percent: f64,
    pub trimmed_mb: f64,
    pub error: String,
}

#[derive(Default)]
pub struct Snapshot {
    pub sessions: Vec<Session>,
    pub last_trimmed_mb: f64,
    pub last_trim_at: String,
}

pub struct Optimizer {
    pub state: Arc<Mutex<OptimizerState>>,
    pub snapshot: Arc<Mutex<Snapshot>>,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}

impl Optimizer {
    pub fn start(state: OptimizerState) -> Self {
        let state = Arc::new(Mutex::new(state));
        let snapshot = Arc::new(Mutex::new(Snapshot::default()));
        let stop = Arc::new(AtomicBool::new(false));
        let worker = {
            let (state, snapshot, stop) = (state.clone(), snapshot.clone(), stop.clone());
            thread::spawn(move || run(state, snapshot, stop))
        };
        Self {
            state,
            snapshot,
            stop,
            worker: Some(worker),
        }
    }
}

impl Drop for Optimizer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn run(state: Arc<Mutex<OptimizerState>>, snapshot: Arc<Mutex<Snapshot>>, stop: Arc<AtomicBool>) {
    let logical_cores = std::thread::available_parallelism().map_or(1, |count| count.get()) as f64;
    let mut originals: HashMap<u32, platform::CpuOriginal> = HashMap::new();
    let mut memory_limits: HashMap<u32, (u16, platform::WorkingSetOriginal)> = HashMap::new();
    let mut previous: HashMap<u32, (u64, u64, Instant)> = HashMap::new();
    let mut total_trimmed: HashMap<u32, (u64, f64)> = HashMap::new();
    let mut last_threads = 0;
    while !stop.load(Ordering::Relaxed) {
        let settings = state.lock().expect("optimizer state").clone();
        let processes = platform::roblox_processes().unwrap_or_default();
        let visible = if settings.auto_enabled && settings.cpu_threads != 0 {
            platform::visible_window_pids()
        } else {
            std::collections::HashSet::new()
        };
        let active: HashMap<u32, u64> = processes
            .iter()
            .map(|process| (process.pid, process.started_ticks))
            .collect();
        originals.retain(|pid, saved| active.get(pid) == Some(&saved.started));
        memory_limits.retain(|pid, (_, saved)| active.get(pid) == Some(&saved.started));
        previous.retain(|pid, (started, _, _)| active.get(pid) == Some(started));
        total_trimmed.retain(|pid, (started, _)| active.get(pid) == Some(started));
        if !settings.auto_enabled || settings.ram_limit_mb == 0 {
            for (_, (_, saved)) in memory_limits.drain() {
                platform::restore_working_set(saved);
            }
        }
        if !settings.auto_enabled || settings.cpu_threads == 0 {
            for (_, saved) in originals.drain() {
                platform::restore_cpu(saved);
            }
        } else if settings.cpu_threads != last_threads {
            for process in &processes {
                if let Some(saved) = originals.get(&process.pid).copied() {
                    let _ = platform::limit_cpu(
                        process.pid,
                        process.started_ticks,
                        settings.cpu_threads,
                        Some(saved),
                    );
                }
            }
        }
        last_threads = settings.cpu_threads;
        let mut sessions = Vec::with_capacity(processes.len());
        let mut last_trim = None;
        for process in processes {
            let mut row = Session {
                process: Some(process.clone()),
                ..Default::default()
            };
            if let Some((started, cpu, time)) = previous.insert(
                process.pid,
                (process.started_ticks, process.cpu_ticks, Instant::now()),
            ) {
                let elapsed = time.elapsed().as_secs_f64();
                if started == process.started_ticks && elapsed > 0.0 {
                    row.cpu_percent = ((process.cpu_ticks.saturating_sub(cpu) as f64)
                        / 10_000_000.0
                        / elapsed
                        / logical_cores
                        * 100.0)
                        .max(0.0);
                }
            }
            if settings.auto_enabled {
                if settings.ram_limit_mb > 0 {
                    let existing = memory_limits.get(&process.pid).copied();
                    if existing.map(|(limit, _)| limit) != Some(settings.ram_limit_mb) {
                        match platform::limit_working_set(
                            process.pid,
                            process.started_ticks,
                            settings.ram_limit_mb,
                            existing.map(|(_, saved)| saved),
                        ) {
                            Ok(saved) => {
                                memory_limits.insert(process.pid, (settings.ram_limit_mb, saved));
                            }
                            Err(error) => row.error = error.to_string(),
                        }
                    }
                } else if process.working_set > TRIM_THRESHOLD {
                    match platform::trim_working_set(process.pid, process.started_ticks) {
                        Ok(Some(after)) => {
                            let mb = process.working_set.saturating_sub(after) as f64 / 1048576.0;
                            if let Some(display) = &mut row.process {
                                display.working_set = after;
                            }
                            total_trimmed
                                .entry(process.pid)
                                .or_insert((process.started_ticks, 0.0))
                                .1 += mb;
                            last_trim = Some(mb);
                        }
                        Ok(None) => {}
                        Err(error) => row.error = error.to_string(),
                    }
                }
                if settings.cpu_threads != 0
                    && !originals.contains_key(&process.pid)
                    && visible.contains(&process.pid)
                {
                    match platform::limit_cpu(
                        process.pid,
                        process.started_ticks,
                        settings.cpu_threads,
                        None,
                    ) {
                        Ok(original) => {
                            originals.insert(process.pid, original);
                        }
                        Err(error) => row.error = error.to_string(),
                    }
                }
            }
            row.trimmed_mb = total_trimmed.get(&process.pid).map_or(0.0, |(_, mb)| *mb);
            sessions.push(row);
        }
        {
            let mut view = snapshot.lock().expect("optimizer snapshot");
            view.sessions = sessions;
            if let Some(mb) = last_trim {
                view.last_trimmed_mb = mb;
                view.last_trim_at = chrono::Local::now().format("%H:%M:%S").to_string();
            }
        }
        let wait = Duration::from_secs_f64(settings.interval_seconds.clamp(0.1, 3600.0));
        let until = Instant::now() + wait;
        while Instant::now() < until && !stop.load(Ordering::Relaxed) {
            if *state.lock().expect("optimizer state") != settings
            {
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
    }
    for (_, saved) in originals {
        platform::restore_cpu(saved);
    }
    for (_, (_, saved)) in memory_limits {
        platform::restore_working_set(saved);
    }
}
