#![allow(non_snake_case)]

use std::{
    collections::HashSet,
    ffi::OsStr,
    fs,
    io::{self, Write},
    os::windows::ffi::OsStrExt,
    path::Path,
    ptr,
};

type Handle = isize;
const INVALID_HANDLE_VALUE: Handle = -1;
const TH32CS_SNAPPROCESS: u32 = 2;
const PROCESS_TERMINATE: u32 = 0x0001;
const PROCESS_SET_QUOTA: u32 = 0x0100;
const PROCESS_SET_INFORMATION: u32 = 0x0200;
const PROCESS_QUERY_INFORMATION: u32 = 0x0400;
const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
const BELOW_NORMAL_PRIORITY_CLASS: u32 = 0x4000;
const QUOTA_LIMITS_HARDWS_MAX_ENABLE: u32 = 0x0000_0004;
const QUOTA_LIMITS_HARDWS_MAX_DISABLE: u32 = 0x0000_0008;

#[repr(C)]
#[derive(Clone, Copy)]
struct ProcessEntry32W {
    dwSize: u32,
    cntUsage: u32,
    th32ProcessID: u32,
    th32DefaultHeapID: usize,
    th32ModuleID: u32,
    cntThreads: u32,
    th32ParentProcessID: u32,
    pcPriClassBase: i32,
    dwFlags: u32,
    szExeFile: [u16; 260],
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct FileTime {
    low: u32,
    high: u32,
}
impl FileTime {
    fn ticks(self) -> u64 {
        ((self.high as u64) << 32) | self.low as u64
    }
}

#[repr(C)]
#[derive(Default)]
struct ProcessMemoryCounters {
    cb: u32,
    page_fault_count: u32,
    peak_working_set: usize,
    working_set: usize,
    quota_peak_paged_pool: usize,
    quota_paged_pool: usize,
    quota_peak_nonpaged_pool: usize,
    quota_nonpaged_pool: usize,
    pagefile_usage: usize,
    peak_pagefile_usage: usize,
}

#[repr(C)]
struct DataBlob {
    cb_data: u32,
    data: *mut u8,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateToolhelp32Snapshot(flags: u32, pid: u32) -> Handle;
    fn Process32FirstW(snapshot: Handle, entry: *mut ProcessEntry32W) -> i32;
    fn Process32NextW(snapshot: Handle, entry: *mut ProcessEntry32W) -> i32;
    fn OpenProcess(access: u32, inherit: i32, pid: u32) -> Handle;
    fn TerminateProcess(handle: Handle, exit_code: u32) -> i32;
    fn CloseHandle(handle: Handle) -> i32;
    fn GetProcessTimes(
        handle: Handle,
        creation: *mut FileTime,
        exit: *mut FileTime,
        kernel: *mut FileTime,
        user: *mut FileTime,
    ) -> i32;
    fn GetProcessAffinityMask(
        handle: Handle,
        process_mask: *mut usize,
        system_mask: *mut usize,
    ) -> i32;
    fn SetProcessAffinityMask(handle: Handle, mask: usize) -> i32;
    fn GetPriorityClass(handle: Handle) -> u32;
    fn SetPriorityClass(handle: Handle, class: u32) -> i32;
    fn GetProcessWorkingSetSizeEx(
        handle: Handle,
        minimum: *mut usize,
        maximum: *mut usize,
        flags: *mut u32,
    ) -> i32;
    fn SetProcessWorkingSetSizeEx(
        handle: Handle,
        minimum: usize,
        maximum: usize,
        flags: u32,
    ) -> i32;
    fn MoveFileExW(source: *const u16, target: *const u16, flags: u32) -> i32;
    fn CreateMutexW(attributes: *const u8, initial_owner: i32, name: *const u16) -> Handle;
    fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
    fn ReleaseMutex(handle: Handle) -> i32;
    fn LocalFree(memory: isize) -> isize;
}

#[link(name = "psapi")]
unsafe extern "system" {
    fn GetProcessMemoryInfo(handle: Handle, counters: *mut ProcessMemoryCounters, cb: u32) -> i32;
    fn EmptyWorkingSet(handle: Handle) -> i32;
}

#[link(name = "user32")]
unsafe extern "system" {
    fn EnumWindows(
        callback: Option<unsafe extern "system" fn(isize, isize) -> i32>,
        data: isize,
    ) -> i32;
    fn IsWindowVisible(window: isize) -> i32;
    fn GetWindowTextLengthW(window: isize) -> i32;
    fn GetWindowThreadProcessId(window: isize, pid: *mut u32) -> u32;
}

#[link(name = "crypt32")]
unsafe extern "system" {
    fn CryptUnprotectData(
        input: *const DataBlob,
        description: *mut *mut u16,
        entropy: *const DataBlob,
        reserved: *const u8,
        prompt: *const u8,
        flags: u32,
        output: *mut DataBlob,
    ) -> i32;
    fn CryptProtectData(
        input: *const DataBlob,
        description: *const u16,
        entropy: *const DataBlob,
        reserved: *const u8,
        prompt: *const u8,
        flags: u32,
        output: *mut DataBlob,
    ) -> i32;
}

#[link(name = "shell32")]
unsafe extern "system" {
    fn ShellExecuteW(
        window: isize,
        operation: *const u16,
        file: *const u16,
        parameters: *const u16,
        directory: *const u16,
        show: i32,
    ) -> isize;
}

#[link(name = "advapi32")]
unsafe extern "system" {
    fn RegCreateKeyExW(
        key: Handle,
        subkey: *const u16,
        reserved: u32,
        class: *const u16,
        options: u32,
        access: u32,
        security: *const u8,
        result: *mut Handle,
        disposition: *mut u32,
    ) -> i32;
    fn RegSetValueExW(
        key: Handle,
        name: *const u16,
        reserved: u32,
        kind: u32,
        data: *const u8,
        len: u32,
    ) -> i32;
    fn RegDeleteValueW(key: Handle, name: *const u16) -> i32;
    fn RegCloseKey(key: Handle) -> i32;
}

struct OwnedHandle(Handle);
impl OwnedHandle {
    fn open(pid: u32, access: u32) -> io::Result<Self> {
        let handle = unsafe { OpenProcess(access, 0, pid) };
        if handle == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(Self(handle))
        }
    }
}
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if self.0 != 0 && self.0 != INVALID_HANDLE_VALUE {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}

fn wide(s: impl AsRef<OsStr>) -> Vec<u16> {
    s.as_ref().encode_wide().chain(Some(0)).collect()
}

#[derive(Clone, Debug)]
pub struct ProcessInfo {
    pub pid: u32,
    pub working_set: u64,
    pub cpu_ticks: u64,
    pub started_ticks: u64,
}

fn roblox_pids() -> io::Result<Vec<u32>> {
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return Err(io::Error::last_os_error());
    }
    let snapshot = OwnedHandle(snapshot);
    let mut entry = ProcessEntry32W {
        dwSize: std::mem::size_of::<ProcessEntry32W>() as u32,
        cntUsage: 0,
        th32ProcessID: 0,
        th32DefaultHeapID: 0,
        th32ModuleID: 0,
        cntThreads: 0,
        th32ParentProcessID: 0,
        pcPriClassBase: 0,
        dwFlags: 0,
        szExeFile: [0; 260],
    };
    let mut result = Vec::new();
    if unsafe { Process32FirstW(snapshot.0, &mut entry) } == 0 {
        return Ok(result);
    }
    loop {
        let len = entry.szExeFile.iter().position(|c| *c == 0).unwrap_or(260);
        let name = String::from_utf16_lossy(&entry.szExeFile[..len]);
        if name.eq_ignore_ascii_case("RobloxPlayerBeta.exe") {
            result.push(entry.th32ProcessID);
        }
        entry.dwSize = std::mem::size_of::<ProcessEntry32W>() as u32;
        if unsafe { Process32NextW(snapshot.0, &mut entry) } == 0 {
            break;
        }
    }
    Ok(result)
}

pub fn roblox_processes() -> io::Result<Vec<ProcessInfo>> {
    let mut result = Vec::new();
    for pid in roblox_pids()? {
        let Ok(handle) = OwnedHandle::open(
            pid,
            PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
        ) else {
            continue;
        };
        let mut memory = ProcessMemoryCounters {
            cb: std::mem::size_of::<ProcessMemoryCounters>() as u32,
            ..Default::default()
        };
        let mut started = FileTime::default();
        let (mut ended, mut kernel, mut user) = (
            FileTime::default(),
            FileTime::default(),
            FileTime::default(),
        );
        if unsafe { GetProcessMemoryInfo(handle.0, &mut memory, memory.cb) } == 0 {
            continue;
        }
        if unsafe { GetProcessTimes(handle.0, &mut started, &mut ended, &mut kernel, &mut user) }
            == 0
        {
            continue;
        }
        result.push(ProcessInfo {
            pid,
            working_set: memory.working_set as u64,
            cpu_ticks: kernel.ticks() + user.ticks(),
            started_ticks: started.ticks(),
        });
    }
    Ok(result)
}

pub fn terminate_roblox_at_startup() -> usize {
    let mut closed = 0;
    for pid in roblox_pids().unwrap_or_default() {
        if let Ok(handle) = OwnedHandle::open(pid, PROCESS_TERMINATE) {
            if unsafe { TerminateProcess(handle.0, 0) } != 0 {
                closed += 1;
            }
        }
    }
    closed
}

pub fn trim_working_set(pid: u32, expected_start: u64) -> io::Result<Option<u64>> {
    let handle = OwnedHandle::open(pid, PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA)?;
    if process_start(handle.0)? != expected_start {
        return Ok(None);
    }
    if unsafe { EmptyWorkingSet(handle.0) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let mut memory = ProcessMemoryCounters {
        cb: std::mem::size_of::<ProcessMemoryCounters>() as u32,
        ..Default::default()
    };
    if unsafe { GetProcessMemoryInfo(handle.0, &mut memory, memory.cb) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(Some(memory.working_set as u64))
}

#[derive(Clone, Copy)]
pub struct WorkingSetOriginal {
    pub pid: u32,
    pub started: u64,
    pub minimum: usize,
    pub maximum: usize,
    pub flags: u32,
}

pub fn limit_working_set(
    pid: u32,
    started: u64,
    maximum_mb: u16,
    original: Option<WorkingSetOriginal>,
) -> io::Result<WorkingSetOriginal> {
    let handle = OwnedHandle::open(pid, PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA)?;
    if process_start(handle.0)? != started {
        return Err(io::Error::other("Process ID was reused"));
    }
    let saved = if let Some(original) = original {
        original
    } else {
        let (mut minimum, mut maximum, mut flags) = (0_usize, 0_usize, 0_u32);
        if unsafe { GetProcessWorkingSetSizeEx(handle.0, &mut minimum, &mut maximum, &mut flags) }
            == 0
        {
            return Err(io::Error::last_os_error());
        }
        WorkingSetOriginal {
            pid,
            started,
            minimum,
            maximum,
            flags,
        }
    };
    let maximum = usize::from(maximum_mb) * 1024 * 1024;
    let minimum = saved.minimum.min(maximum);
    let flags = (saved.flags & !QUOTA_LIMITS_HARDWS_MAX_DISABLE) | QUOTA_LIMITS_HARDWS_MAX_ENABLE;
    if unsafe { SetProcessWorkingSetSizeEx(handle.0, minimum, maximum, flags) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(saved)
}

pub fn restore_working_set(saved: WorkingSetOriginal) {
    if let Ok(handle) = OwnedHandle::open(saved.pid, PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA)
    {
        if process_start(handle.0).ok() == Some(saved.started) {
            let flags = if saved.flags & QUOTA_LIMITS_HARDWS_MAX_ENABLE != 0 {
                saved.flags
            } else {
                (saved.flags & !QUOTA_LIMITS_HARDWS_MAX_ENABLE) | QUOTA_LIMITS_HARDWS_MAX_DISABLE
            };
            unsafe {
                SetProcessWorkingSetSizeEx(handle.0, saved.minimum, saved.maximum, flags);
            }
        }
    }
}

fn process_start(handle: Handle) -> io::Result<u64> {
    let (mut start, mut end, mut kernel, mut user) = (
        FileTime::default(),
        FileTime::default(),
        FileTime::default(),
        FileTime::default(),
    );
    if unsafe { GetProcessTimes(handle, &mut start, &mut end, &mut kernel, &mut user) } == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(start.ticks())
    }
}

#[derive(Clone, Copy)]
pub struct CpuOriginal {
    pub pid: u32,
    pub started: u64,
    pub affinity: usize,
    pub priority: u32,
}

pub fn limit_cpu(
    pid: u32,
    started: u64,
    threads: u8,
    original: Option<CpuOriginal>,
) -> io::Result<CpuOriginal> {
    let handle = OwnedHandle::open(pid, PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION)?;
    if process_start(handle.0)? != started {
        return Err(io::Error::other("Process ID was reused"));
    }
    let saved = if let Some(saved) = original {
        saved
    } else {
        let (mut mask, mut system) = (0_usize, 0_usize);
        if unsafe { GetProcessAffinityMask(handle.0, &mut mask, &mut system) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let priority = unsafe { GetPriorityClass(handle.0) };
        if priority == 0 {
            return Err(io::Error::last_os_error());
        }
        CpuOriginal {
            pid,
            started,
            affinity: mask,
            priority,
        }
    };
    let available: Vec<usize> = (0..usize::BITS)
        .map(|bit| 1_usize << bit)
        .filter(|bit| saved.affinity & bit != 0)
        .collect();
    if available.is_empty() {
        return Ok(saved);
    }
    let mut wanted = 0_usize;
    for offset in 0..usize::from(threads).min(available.len()) {
        wanted |= available[(pid as usize + offset) % available.len()];
    }
    if unsafe { SetProcessAffinityMask(handle.0, wanted) } == 0 {
        return Err(io::Error::last_os_error());
    }
    unsafe {
        SetPriorityClass(handle.0, BELOW_NORMAL_PRIORITY_CLASS);
    }
    Ok(saved)
}

pub fn restore_cpu(saved: CpuOriginal) {
    if let Ok(handle) = OwnedHandle::open(
        saved.pid,
        PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION,
    ) {
        if process_start(handle.0).ok() == Some(saved.started) {
            unsafe {
                SetProcessAffinityMask(handle.0, saved.affinity);
                SetPriorityClass(handle.0, saved.priority);
            }
        }
    }
}

unsafe extern "system" fn visible_window(window: isize, data: isize) -> i32 {
    if unsafe { IsWindowVisible(window) } != 0 && unsafe { GetWindowTextLengthW(window) } > 0 {
        let mut pid = 0_u32;
        unsafe {
            GetWindowThreadProcessId(window, &mut pid);
        }
        unsafe { &mut *(data as *mut HashSet<u32>) }.insert(pid);
    }
    1
}

pub fn visible_window_pids() -> HashSet<u32> {
    let mut pids = HashSet::new();
    unsafe {
        EnumWindows(
            Some(visible_window),
            (&mut pids as *mut HashSet<u32>) as isize,
        );
    }
    pids
}

pub fn atomic_write(path: &Path, contents: &[u8]) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let tmp = path.with_extension(format!("mizu-{}.tmp", std::process::id()));
    {
        let mut file = fs::File::create(&tmp)?;
        file.write_all(contents)?;
        file.sync_all()?;
    }
    let source = wide(tmp.as_os_str());
    let target = wide(path.as_os_str());
    if unsafe { MoveFileExW(source.as_ptr(), target.as_ptr(), 0x1 | 0x8) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

pub fn open_url(url: &str) -> io::Result<()> {
    let url = wide(OsStr::new(url));
    let result =
        unsafe { ShellExecuteW(0, ptr::null(), url.as_ptr(), ptr::null(), ptr::null(), 1) };
    if result <= 32 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

pub fn set_startup(enabled: bool, exe: &Path) -> io::Result<()> {
    let subkey = wide(OsStr::new(
        "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
    ));
    let name = wide(OsStr::new("Mizuauto"));
    let mut key = 0;
    let result = unsafe {
        RegCreateKeyExW(
            0x80000001_u32 as i32 as isize,
            subkey.as_ptr(),
            0,
            ptr::null(),
            0,
            0x0002,
            ptr::null(),
            &mut key,
            ptr::null_mut(),
        )
    };
    if result != 0 {
        return Err(io::Error::from_raw_os_error(result));
    }
    let status = if enabled {
        let command = wide(OsStr::new(&format!("\"{}\"", exe.display())));
        unsafe {
            RegSetValueExW(
                key,
                name.as_ptr(),
                0,
                1,
                command.as_ptr() as *const u8,
                (command.len() * 2) as u32,
            )
        }
    } else {
        let code = unsafe { RegDeleteValueW(key, name.as_ptr()) };
        if code == 2 { 0 } else { code }
    };
    unsafe {
        RegCloseKey(key);
    }
    if status == 0 {
        Ok(())
    } else {
        Err(io::Error::from_raw_os_error(status))
    }
}

pub struct RobloxMutex {
    handle: OwnedHandle,
    owned: bool,
}
impl RobloxMutex {
    pub fn create() -> io::Result<Self> {
        let name = wide(OsStr::new("ROBLOX_singletonMutex"));
        let handle = unsafe { CreateMutexW(ptr::null(), 0, name.as_ptr()) };
        if handle == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(Self {
                handle: OwnedHandle(handle),
                owned: false,
            })
        }
    }
    pub fn try_acquire(&mut self) -> bool {
        if self.owned {
            return true;
        }
        self.owned = matches!(unsafe { WaitForSingleObject(self.handle.0, 0) }, 0 | 0x80);
        self.owned
    }
    pub fn is_owned(&self) -> bool {
        self.owned
    }
    pub fn release(&mut self) {
        if self.owned {
            unsafe {
                ReleaseMutex(self.handle.0);
            }
            self.owned = false;
        }
    }
}
impl Drop for RobloxMutex {
    fn drop(&mut self) {
        self.release();
    }
}

fn dpapi(input: &[u8], entropy: &[u8], protect: bool, local_machine: bool) -> io::Result<Vec<u8>> {
    let mut input_copy = input.to_vec();
    let mut entropy_copy = entropy.to_vec();
    let source = DataBlob {
        cb_data: input_copy.len() as u32,
        data: input_copy.as_mut_ptr(),
    };
    let extra = DataBlob {
        cb_data: entropy_copy.len() as u32,
        data: entropy_copy.as_mut_ptr(),
    };
    let mut output = DataBlob {
        cb_data: 0,
        data: ptr::null_mut(),
    };
    let ok = unsafe {
        if protect {
            CryptProtectData(
                &source,
                ptr::null(),
                &extra,
                ptr::null(),
                ptr::null(),
                if local_machine { 4 } else { 0 },
                &mut output,
            )
        } else {
            CryptUnprotectData(
                &source,
                ptr::null_mut(),
                &extra,
                ptr::null(),
                ptr::null(),
                0,
                &mut output,
            )
        }
    };
    if ok == 0 {
        return Err(io::Error::last_os_error());
    }
    let bytes =
        unsafe { std::slice::from_raw_parts(output.data, output.cb_data as usize).to_vec() };
    unsafe {
        LocalFree(output.data as isize);
    }
    Ok(bytes)
}

pub fn dpapi_unprotect(input: &[u8], entropy: &[u8]) -> io::Result<Vec<u8>> {
    dpapi(input, entropy, false, false)
}
pub fn dpapi_protect(input: &[u8], entropy: &[u8]) -> io::Result<Vec<u8>> {
    dpapi(input, entropy, true, true)
}
