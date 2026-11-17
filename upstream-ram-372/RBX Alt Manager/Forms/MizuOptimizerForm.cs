using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Forms
{
    // Companion panel inside Mizuauto's full account manager build.
    public sealed class MizuOptimizerForm : Form
    {
        private const long Mib = 1024L * 1024L;
        private const long TrimThreshold = 384L * Mib;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint SetQuota = 0x0100;
        private readonly string statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mizuauto", "state.json");
        private readonly object targetLock = new object();
        private readonly Dictionary<int, long> targets = new Dictionary<int, long>();
        private readonly Dictionary<int, CpuOriginal> cpuOriginals = new Dictionary<int, CpuOriginal>();
        private readonly Dictionary<int, CpuSample> cpuSamples = new Dictionary<int, CpuSample>();
        private readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer();
        private System.Threading.Timer trimTimer;
        private JObject state;
        private int trimming;
        private int cpuThreads;
        private int ramLimitMb;
        private volatile bool autoEnabled;
        private RustOptimizerBackend rustBackend;
        private bool ready;
        private decimal intervalSeconds;
        private DataGridView sessionTable;
        private NumericUpDown intervalBox;
        private ComboBox cpuBox;
        private ComboBox ramBox;
        private Button autoButton;
        private Label statusLabel;
        private Label multiStatusLabel;

        private sealed class CpuOriginal
        {
            public long Started;
            public IntPtr Affinity;
            public ProcessPriorityClass Priority;
        }

        private sealed class CpuSample
        {
            public long Started;
            public TimeSpan Cpu;
            public DateTime At;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLengthW(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr handle);

        public MizuOptimizerForm()
        {
            LoadState();
            BuildUi();
            string rustError;
            rustBackend = RustOptimizerBackend.TryCreate(out rustError);
            if (rustBackend != null)
                rustBackend.Configure((double)intervalSeconds, cpuThreads, false, ramLimitMb);
            else
            {
                ramBox.Enabled = false;
                SetStatus(rustError + " · đang dùng bộ tối ưu cũ");
            }
            ready = true;
            scanTimer.Interval = 2000;
            scanTimer.Tick += (sender, args) => RefreshSessions();
            scanTimer.Start();
            RefreshSessions();
            if (autoEnabled)
                StartAuto(false);
            FormClosing += (sender, args) =>
            {
                if (args.CloseReason == CloseReason.UserClosing && Owner != null && !Owner.IsDisposed && Owner.Visible)
                {
                    args.Cancel = true;
                    Hide();
                    return;
                }
                ShutdownOptimizer();
            };
            Application.ApplicationExit += (sender, args) => ShutdownOptimizer();
        }

        private void ShutdownOptimizer()
        {
            autoEnabled = false;
            scanTimer.Stop();
            trimTimer?.Dispose();
            trimTimer = null;
            if (rustBackend != null)
            {
                rustBackend.Dispose();
                rustBackend = null;
            }
            else RestoreCpu();
        }

        private void LoadState()
        {
            try { state = JObject.Parse(File.ReadAllText(statePath)); }
            catch { state = new JObject(); }
            double savedInterval;
            try { savedInterval = state.Value<double?>("interval_seconds") ?? 0.1; }
            catch { savedInterval = 0.1; }
            if (double.IsNaN(savedInterval) || double.IsInfinity(savedInterval)) savedInterval = 0.1;
            intervalSeconds = Math.Max(0.1m, Math.Min(3600m, (decimal)savedInterval));
            try { cpuThreads = state.Value<int?>("cpu_threads") ?? 0; }
            catch { cpuThreads = 0; }
            if (cpuThreads != 1 && cpuThreads != 2) cpuThreads = 0;
            try { ramLimitMb = state.Value<int?>("ram_limit_mb") ?? 0; }
            catch { ramLimitMb = 0; }
            if (ramLimitMb < 0 || ramLimitMb > 3000 || ramLimitMb % 100 != 0) ramLimitMb = 0;
            try { autoEnabled = state.Value<bool?>("auto_enabled") ?? true; }
            catch { autoEnabled = true; }
        }

        private void SaveState()
        {
            state["interval_seconds"] = intervalSeconds;
            state["cpu_threads"] = cpuThreads;
            state["auto_enabled"] = autoEnabled;
            state["ram_limit_mb"] = ramLimitMb;
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            string temporary = statePath + ".tmp";
            File.WriteAllText(temporary, state.ToString());
            if (File.Exists(statePath)) File.Replace(temporary, statePath, null);
            else File.Move(temporary, statePath);
        }

        private static Button MakeButton(string text, Color color, int width)
        {
            Button button = new Button { Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Font = new Font("Segoe UI", 9, FontStyle.Bold), Margin = new Padding(5, 0, 0, 0) };
            button.FlatAppearance.BorderColor = Color.FromArgb(180, 155, 198);
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, .16f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(color, .12f);
            return button;
        }

        private void BuildUi()
        {
            Color background = Color.FromArgb(20, 18, 30);
            Color card = Color.FromArgb(33, 28, 44);
            Color accent = Color.FromArgb(241, 118, 186);
            BackColor = background;
            ForeColor = Color.White;
            Text = "Mizuauto";
            ShowInTaskbar = false;
            ClientSize = new Size(660, 470);
            MinimumSize = new Size(620, 460);
            Font = new Font("Segoe UI", 9);
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label { Text = "Roblox", Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(20, 17) };
            Controls.Add(title);
            Panel panel = new Panel { BackColor = card, Location = new Point(20, 58), Size = new Size(620, 390), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(12) };
            Controls.Add(panel);
            GlassBackground.Apply(this);
            GlassBackground.Apply(panel);
            var backgroundTimer = new System.Windows.Forms.Timer { Interval = 140 };
            backgroundTimer.Tick += (sender, args) =>
            {
                backgroundTimer.Stop();
                if (IsDisposed || WindowState == FormWindowState.Minimized) return;
                GlassBackground.Apply(this);
                GlassBackground.Apply(panel);
            };
            Resize += (sender, args) =>
            {
                if (WindowState == FormWindowState.Minimized) return;
                backgroundTimer.Stop();
                backgroundTimer.Start();
            };
            panel.Resize += (sender, args) =>
            {
                backgroundTimer.Stop();
                backgroundTimer.Start();
            };
            ResizeBegin += (sender, args) => scanTimer.Stop();
            ResizeEnd += (sender, args) =>
            {
                backgroundTimer.Stop();
                if (!IsDisposed && WindowState != FormWindowState.Minimized)
                {
                    GlassBackground.Apply(this);
                    GlassBackground.Apply(panel);
                }
                scanTimer.Start();
            };
            FormClosed += (sender, args) => backgroundTimer.Dispose();
            sessionTable = new DataGridView { Location = new Point(12, 12), Size = new Size(596, 233), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, BackgroundColor = card, BorderStyle = BorderStyle.None, GridColor = Color.FromArgb(58, 48, 70), AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            sessionTable.EnableHeadersVisualStyles = false;
            sessionTable.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(48, 40, 61);
            sessionTable.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            sessionTable.DefaultCellStyle.BackColor = card;
            sessionTable.DefaultCellStyle.ForeColor = Color.White;
            sessionTable.DefaultCellStyle.SelectionBackColor = Color.FromArgb(103, 86, 158);
            sessionTable.DefaultCellStyle.SelectionForeColor = Color.White;
            sessionTable.Columns.Add("pid", "PID");
            sessionTable.Columns.Add("ram", "RAM");
            sessionTable.Columns.Add("cpu", "CPU");
            sessionTable.Columns.Add("time", "THỜI GIAN");
            panel.Controls.Add(sessionTable);

            FlowLayoutPanel controls = new FlowLayoutPanel { Location = new Point(12, 256), Size = new Size(596, 37), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, WrapContents = false };
            panel.Controls.Add(controls);
            controls.Controls.Add(new Label { Text = "Chu kỳ", ForeColor = Color.LightGray, AutoSize = true, Padding = new Padding(0, 9, 0, 0) });
            intervalBox = new NumericUpDown { Minimum = 0.1m, Maximum = 3600m, DecimalPlaces = 1, Increment = 0.1m, Value = intervalSeconds, Width = 72, Height = 32, BackColor = background, ForeColor = Color.White, Margin = new Padding(7, 3, 0, 0) };
            controls.Controls.Add(intervalBox);
            controls.Controls.Add(new Label { Text = "giây", ForeColor = Color.LightGray, AutoSize = true, Padding = new Padding(5, 9, 0, 0) });
            Button apply = MakeButton("Áp dụng", Color.FromArgb(103, 86, 158), 83);
            apply.Click += (sender, args) => ApplyInterval();
            controls.Controls.Add(apply);
            autoButton = MakeButton("Bật Auto", accent, 98);
            autoButton.ForeColor = Color.FromArgb(40, 20, 33);
            autoButton.Click += (sender, args) => { if (autoEnabled) StopAuto(); else StartAuto(true); };
            controls.Controls.Add(autoButton);
            controls.Controls.Add(new Label { Text = "CPU", ForeColor = Color.LightGray, AutoSize = true, Padding = new Padding(12, 9, 0, 0) });
            cpuBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 88, BackColor = background, ForeColor = Color.White, Margin = new Padding(5, 3, 0, 0) };
            cpuBox.Items.AddRange(new object[] { "Tắt", "2 luồng", "1 luồng" });
            cpuBox.SelectedIndex = cpuThreads == 2 ? 1 : cpuThreads == 1 ? 2 : 0;
            cpuBox.SelectedIndexChanged += (sender, args) =>
            {
                if (!ready) return;
                cpuThreads = cpuBox.SelectedIndex == 1 ? 2 : cpuBox.SelectedIndex == 2 ? 1 : 0;
                if (rustBackend != null) rustBackend.Configure((double)intervalSeconds, cpuThreads, autoEnabled, ramLimitMb);
                else if (cpuThreads == 0) RestoreCpu();
                else RefreshSessions();
                SaveState();
                SetStatus(cpuThreads == 0 ? "Đã tắt giới hạn CPU" : $"CPU: tối đa {cpuThreads} luồng mỗi phiên");
            };
            controls.Controls.Add(cpuBox);

            FlowLayoutPanel ramControls = new FlowLayoutPanel { Location = new Point(12, 296), Size = new Size(596, 33), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, WrapContents = false };
            ramControls.Controls.Add(new Label { Text = "RAM", ForeColor = Color.LightGray, AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            ramBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, BackColor = background, ForeColor = Color.White, Margin = new Padding(8, 2, 0, 0) };
            ramBox.Items.Add("Thu hồi tối đa");
            for (int limit = 100; limit <= 3000; limit += 100)
                ramBox.Items.Add(limit == 3000 ? "3000 MB (≈3 GB)" : $"{limit} MB");
            ramBox.SelectedIndex = ramLimitMb / 100;
            ramBox.SelectedIndexChanged += (sender, args) =>
            {
                if (!ready) return;
                ramLimitMb = ramBox.SelectedIndex * 100;
                SaveState();
                rustBackend?.Configure((double)intervalSeconds, cpuThreads, autoEnabled, ramLimitMb);
                SetStatus($"RAM: {ramBox.SelectedItem}");
            };
            ramControls.Controls.Add(ramBox);
            panel.Controls.Add(ramControls);

            statusLabel = new Label { Text = "Đang quét Roblox...", ForeColor = Color.FromArgb(194, 177, 195), Location = new Point(12, 333), Size = new Size(596, 22), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            panel.Controls.Add(statusLabel);
            multiStatusLabel = new Label { ForeColor = Color.FromArgb(241, 118, 186), Location = new Point(12, 357), Size = new Size(596, 20), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            panel.Controls.Add(multiStatusLabel);
        }

        private void SetStatus(string message)
        {
            if (!IsDisposed && statusLabel != null) statusLabel.Text = message;
        }

        private void ApplyInterval()
        {
            intervalSeconds = intervalBox.Value;
            SaveState();
            if (rustBackend != null) rustBackend.Configure((double)intervalSeconds, cpuThreads, autoEnabled, ramLimitMb);
            else if (autoEnabled) trimTimer?.Change(0, (int)(intervalSeconds * 1000));
            SetStatus($"Chu kỳ: {intervalSeconds:0.0} giây");
        }

        private bool OtherMizuautoRunning()
        {
            int ownPid = Process.GetCurrentProcess().Id;
            bool found = false;
            foreach (Process process in Process.GetProcessesByName("mizuauto"))
            {
                try { if (process.Id != ownPid) found = true; }
                finally { process.Dispose(); }
            }
            return found;
        }

        private void StartAuto(bool save)
        {
            if (OtherMizuautoRunning())
            {
                autoEnabled = false;
                SetStatus("Đóng bản Mizuauto cũ trước khi bật Auto");
                return;
            }
            autoEnabled = true;
            autoButton.Text = "Dừng";
            autoButton.BackColor = Color.FromArgb(170, 69, 107);
            if (save) SaveState();
            if (rustBackend != null) rustBackend.Configure((double)intervalSeconds, cpuThreads, true, ramLimitMb);
            else if (trimTimer == null) trimTimer = new System.Threading.Timer(TrimTick, null, 0, (int)(intervalSeconds * 1000));
            else trimTimer.Change(0, (int)(intervalSeconds * 1000));
            SetStatus("Auto đang chạy");
        }

        private void StopAuto()
        {
            autoEnabled = false;
            autoButton.Text = "Bật Auto";
            autoButton.BackColor = Color.FromArgb(241, 118, 186);
            if (rustBackend != null) rustBackend.Configure((double)intervalSeconds, cpuThreads, false, ramLimitMb);
            else
            {
                trimTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                RestoreCpu();
            }
            SaveState();
            SetStatus("Đã dừng Auto");
        }

        private static HashSet<int> VisibleRobloxPids()
        {
            HashSet<int> pids = new HashSet<int>();
            EnumWindows((hwnd, ignored) =>
            {
                if (IsWindowVisible(hwnd) && GetWindowTextLengthW(hwnd) > 0)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != 0) pids.Add((int)pid);
                }
                return true;
            }, IntPtr.Zero);
            return pids;
        }

        private void RefreshSessions()
        {
            if (rustBackend != null)
            {
                RefreshRustSessions();
                return;
            }
            bool optimizerConflict = OtherMizuautoRunning();
            if (optimizerConflict) RestoreCpu();
            HashSet<int> visible = VisibleRobloxPids();
            Dictionary<int, long> discovered = new Dictionary<int, long>();
            DateTime now = DateTime.UtcNow;
            sessionTable.Rows.Clear();
            foreach (Process process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    if (!visible.Contains(process.Id)) continue;
                    long started = process.StartTime.ToUniversalTime().Ticks;
                    discovered[process.Id] = started;
                    double cpu = 0;
                    if (cpuSamples.TryGetValue(process.Id, out CpuSample sample) && sample.Started == started)
                    {
                        double elapsed = (now - sample.At).TotalSeconds;
                        if (elapsed > 0) cpu = Math.Max(0, (process.TotalProcessorTime - sample.Cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
                    }
                    cpuSamples[process.Id] = new CpuSample { Started = started, Cpu = process.TotalProcessorTime, At = now };
                    TimeSpan uptime = now - process.StartTime.ToUniversalTime();
                    sessionTable.Rows.Add(process.Id, $"{process.WorkingSet64 / (double)(1024 * Mib):0.00} GB", $"{cpu:0.0}%", $"{(int)uptime.TotalHours}g {uptime.Minutes:00}p");
                    if (autoEnabled && cpuThreads > 0 && !optimizerConflict)
                        ApplyCpu(process, started);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                {
                    SetStatus($"Không đọc được PID {process.Id}");
                }
                finally { process.Dispose(); }
            }
            lock (targetLock)
            {
                targets.Clear();
                foreach (var item in discovered) targets[item.Key] = item.Value;
            }
            foreach (int pid in cpuOriginals.Keys.ToArray())
                if (!discovered.TryGetValue(pid, out long started) || cpuOriginals[pid].Started != started)
                    RestoreCpu(pid);
            if (!autoEnabled) RestoreCpu();
            foreach (int pid in cpuSamples.Keys.ToArray())
                if (!discovered.ContainsKey(pid)) cpuSamples.Remove(pid);
            multiStatusLabel.Text = !AccountManager.MultiRobloxEnabled ? "Multi Roblox: bật trong Settings" : AccountManager.MultiRobloxReady ? "Multi Roblox: sẵn sàng" : "Multi Roblox: kiểm tra RAM gốc hoặc đóng Roblox";
            if (optimizerConflict) SetStatus("Đóng bản Mizuauto cũ để bật tối ưu");
            else if (statusLabel.Text == "Đang quét Roblox..." || statusLabel.Text.StartsWith("Đang theo dõi") || statusLabel.Text.StartsWith("Đóng bản Mizuauto cũ"))
                SetStatus($"Đang theo dõi {discovered.Count} phiên");
        }

        private void RefreshRustSessions()
        {
            bool optimizerConflict = OtherMizuautoRunning();
            rustBackend.Configure((double)intervalSeconds, cpuThreads, autoEnabled && !optimizerConflict, ramLimitMb);
            HashSet<int> visible = VisibleRobloxPids();
            sessionTable.Rows.Clear();
            int count = 0;
            foreach (RustOptimizerBackend.Session session in rustBackend.Sessions())
            {
                if (session.Pid == 0 || !visible.Contains((int)session.Pid)) continue;
                try
                {
                    TimeSpan uptime = DateTime.UtcNow - DateTime.FromFileTimeUtc((long)session.StartedFiletime);
                    sessionTable.Rows.Add(session.Pid,
                        $"{session.WorkingSetBytes / (double)(1024 * Mib):0.00} GB",
                        $"{session.CpuPercent:0.0}%",
                        $"{(int)uptime.TotalHours}g {uptime.Minutes:00}p");
                    count++;
                }
                catch (ArgumentOutOfRangeException) { }
            }
            multiStatusLabel.Text = !AccountManager.MultiRobloxEnabled
                ? "Multi Roblox: bật trong Settings"
                : AccountManager.MultiRobloxReady ? "Multi Roblox: sẵn sàng" : "Multi Roblox: kiểm tra RAM gốc hoặc đóng Roblox";
            if (optimizerConflict) SetStatus("Đóng bản Mizuauto cũ để bật tối ưu");
            else if (statusLabel.Text == "Đang quét Roblox..." || statusLabel.Text.StartsWith("Đang theo dõi") || statusLabel.Text.StartsWith("Đóng bản Mizuauto cũ") || statusLabel.Text.StartsWith("RAM:"))
                SetStatus($"Đang theo dõi {count} phiên · RAM: {(ramLimitMb == 0 ? "thu hồi tối đa" : ramLimitMb + " MB")}");
        }

        private static IntPtr LimitedAffinity(IntPtr original, int count, int pid)
        {
            long mask = original.ToInt64();
            List<int> allowed = new List<int>();
            for (int bit = 0; bit < IntPtr.Size * 8; bit++)
                if ((mask & (1L << bit)) != 0) allowed.Add(bit);
            if (allowed.Count == 0) return original;
            long limited = 0;
            for (int index = 0; index < Math.Min(count, allowed.Count); index++)
                limited |= 1L << allowed[(pid + index) % allowed.Count];
            return new IntPtr(limited);
        }

        private void ApplyCpu(Process process, long started)
        {
            try
            {
                if (!cpuOriginals.TryGetValue(process.Id, out CpuOriginal original) || original.Started != started)
                {
                    original = new CpuOriginal { Started = started, Affinity = process.ProcessorAffinity, Priority = process.PriorityClass };
                    cpuOriginals[process.Id] = original;
                }
                IntPtr wanted = LimitedAffinity(original.Affinity, cpuThreads, process.Id);
                if (process.ProcessorAffinity != wanted) process.ProcessorAffinity = wanted;
                if (process.PriorityClass != ProcessPriorityClass.BelowNormal) process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                SetStatus($"Không giới hạn được CPU PID {process.Id}");
            }
        }

        private void RestoreCpu(int pid)
        {
            if (!cpuOriginals.TryGetValue(pid, out CpuOriginal original)) return;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                    if (process.StartTime.ToUniversalTime().Ticks == original.Started)
                    {
                        process.ProcessorAffinity = original.Affinity;
                        process.PriorityClass = original.Priority;
                    }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                // The session may have exited while restoring.
            }
            cpuOriginals.Remove(pid);
        }

        private void RestoreCpu()
        {
            foreach (int pid in cpuOriginals.Keys.ToArray()) RestoreCpu(pid);
        }

        private void TrimTick(object ignored)
        {
            if (!autoEnabled || Interlocked.Exchange(ref trimming, 1) != 0) return;
            try
            {
                KeyValuePair<int, long>[] current;
                lock (targetLock) current = targets.ToArray();
                foreach (var item in current)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(item.Key))
                        {
                            if (!process.ProcessName.Equals("RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase) || process.StartTime.ToUniversalTime().Ticks != item.Value || process.WorkingSet64 < TrimThreshold) continue;
                            IntPtr handle = OpenProcess(QueryLimitedInformation | SetQuota, false, item.Key);
                            if (handle == IntPtr.Zero) continue;
                            try { EmptyWorkingSet(handle); }
                            finally { CloseHandle(handle); }
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
                    {
                        // A Roblox window may close between scans.
                    }
                }
            }
            finally { Interlocked.Exchange(ref trimming, 0); }
        }
    }
}
