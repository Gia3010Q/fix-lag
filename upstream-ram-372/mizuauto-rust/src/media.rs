use image::AnimationDecoder;
use std::{
    io::Cursor,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender, TrySendError},
    },
    thread,
    time::Duration,
};

pub struct Frame {
    pub pixels: Vec<u8>,
    pub width: usize,
    pub height: usize,
}
pub enum MediaEvent {
    Frame(Frame),
    Error(String),
}

pub struct MediaWorker {
    pub receiver: Receiver<MediaEvent>,
    pub animated: bool,
    stopped: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}

impl MediaWorker {
    pub fn start(path: PathBuf) -> Self {
        let animated = path.extension().is_some_and(|e| {
            e.eq_ignore_ascii_case("gif")
                || e.eq_ignore_ascii_case("mp4")
                || e.eq_ignore_ascii_case("webm")
        });
        let (sender, receiver) = mpsc::sync_channel(2);
        let stopped = Arc::new(AtomicBool::new(false));
        let paused = Arc::new(AtomicBool::new(false));
        let signal = stopped.clone();
        let pause_signal = paused.clone();
        let worker = thread::spawn(move || decode(path, sender, signal, pause_signal));
        Self {
            receiver,
            animated,
            stopped,
            paused,
            worker: Some(worker),
        }
    }
    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::Relaxed);
    }
}

impl Drop for MediaWorker {
    fn drop(&mut self) {
        self.stopped.store(true, Ordering::Relaxed);
        self.worker.take(); // Detach: decoder open may be slow, so the UI never waits for it.
    }
}

fn send_frame(sender: &SyncSender<MediaEvent>, frame: Frame) -> bool {
    match sender.try_send(MediaEvent::Frame(frame)) {
        Ok(()) | Err(TrySendError::Full(_)) => true,
        Err(TrySendError::Disconnected(_)) => false,
    }
}

fn decode(
    path: PathBuf,
    sender: SyncSender<MediaEvent>,
    stopped: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
) {
    if path
        .extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("mp4") || e.eq_ignore_ascii_case("webm"))
    {
        decode_video(path, sender, stopped, paused);
        return;
    }
    let bytes = match std::fs::read(&path) {
        Ok(bytes) => bytes,
        Err(_) => {
            let _ = sender.try_send(MediaEvent::Error("Không đọc được ảnh nền".into()));
            return;
        }
    };
    let is_gif = path
        .extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("gif"));
    if !is_gif {
        match image::load_from_memory(&bytes) {
            Ok(image) => {
                let image = image.to_rgba8();
                send_frame(
                    &sender,
                    Frame {
                        width: image.width() as usize,
                        height: image.height() as usize,
                        pixels: image.into_raw(),
                    },
                );
            }
            Err(_) => {
                let _ = sender.try_send(MediaEvent::Error("Định dạng ảnh nền không hợp lệ".into()));
            }
        }
        return;
    }
    while !stopped.load(Ordering::Relaxed) {
        while paused.load(Ordering::Relaxed) && !stopped.load(Ordering::Relaxed) {
            thread::sleep(Duration::from_millis(50));
        }
        let decoder = match image::codecs::gif::GifDecoder::new(Cursor::new(&bytes)) {
            Ok(decoder) => decoder,
            Err(_) => {
                let _ = sender.try_send(MediaEvent::Error("Không giải mã được GIF".into()));
                return;
            }
        };
        let mut had_frame = false;
        for result in decoder.into_frames() {
            if stopped.load(Ordering::Relaxed) {
                return;
            }
            while paused.load(Ordering::Relaxed) && !stopped.load(Ordering::Relaxed) {
                thread::sleep(Duration::from_millis(50));
            }
            let Ok(frame) = result else {
                return;
            };
            had_frame = true;
            let delay = frame.delay().numer_denom_ms();
            let milliseconds =
                ((delay.0 as f64 / delay.1.max(1) as f64).round() as u64).clamp(20, 1000);
            let image = frame.into_buffer();
            if !send_frame(
                &sender,
                Frame {
                    width: image.width() as usize,
                    height: image.height() as usize,
                    pixels: image.into_raw(),
                },
            ) {
                return;
            }
            let mut remaining = milliseconds;
            while remaining > 0 && !stopped.load(Ordering::Relaxed) {
                let slice = remaining.min(25);
                thread::sleep(Duration::from_millis(slice));
                remaining -= slice;
            }
        }
        if !had_frame {
            return;
        }
    }
}

fn decode_video(
    path: PathBuf,
    sender: SyncSender<MediaEvent>,
    stopped: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
) {
    use alhazen_core::{Player, PlayerConfig, PlayerState, Source, VideoFrame};
    let config = PlayerConfig {
        autoplay: false,
        frame_queue_len: 2,
        packet_queue_len: 4,
        max_output_size: Some((1280, 720)),
        ..Default::default()
    };
    let player = match Player::open(Source::File(path), config) {
        Ok(player) => player,
        Err(_) => {
            let _ = sender.try_send(MediaEvent::Error("Không mở được video nền".into()));
            return;
        }
    };
    player.set_muted(true);
    player.play();
    let mut was_paused = false;
    let mut last_pts = None;
    while !stopped.load(Ordering::Relaxed) {
        let is_paused = paused.load(Ordering::Relaxed);
        if is_paused != was_paused {
            if is_paused {
                player.pause();
            } else {
                player.play();
            }
            was_paused = is_paused;
        }
        if !is_paused {
            if let Some(VideoFrame::Bgra {
                width,
                height,
                bgra,
                pts,
            }) = player.current_frame()
            {
                if last_pts != Some(pts) {
                    last_pts = Some(pts);
                    let mut rgba = bgra.to_vec();
                    for pixel in rgba.chunks_exact_mut(4) {
                        pixel.swap(0, 2);
                    }
                    if !send_frame(
                        &sender,
                        Frame {
                            pixels: rgba,
                            width: width as usize,
                            height: height as usize,
                        },
                    ) {
                        return;
                    }
                }
            }
            if matches!(player.state(), PlayerState::Ended) {
                player.seek(Duration::ZERO);
                player.play();
                last_pts = None;
            }
        }
        thread::sleep(Duration::from_millis(16));
    }
}
