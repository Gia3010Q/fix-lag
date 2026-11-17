import unittest
import tkinter as tk
import psutil
from unittest.mock import patch
from threading import Event
from types import SimpleNamespace
import time
from pathlib import Path

from roblox_optimizer import App, cpu_choice, game_windows_open, low_graphics_xml, matching_roblox, replace_xml_value, trim_batch
from ram_integration import RamSettings, list_ram_accounts


class ConfigTests(unittest.TestCase):
    def test_only_targeted_settings_change(self):
        xml = ('<roblox><token name="SavedQualityLevel">5</token>'
               '<token name="GraphicsOptimizationMode">1</token>'
               '<int name="FramerateCap">-1</int>'
               '<string name="Other">keep me</string></roblox>')
        updated = low_graphics_xml(xml)
        self.assertIn('<token name="SavedQualityLevel">1</token>', updated)
        self.assertIn('<token name="GraphicsOptimizationMode">0</token>', updated)
        self.assertIn('<int name="FramerateCap">60</int>', updated)
        self.assertIn('<string name="Other">keep me</string>', updated)

    def test_ambiguous_config_is_rejected(self):
        with self.assertRaises(ValueError):
            replace_xml_value('<x><int name="FramerateCap">1</int><int name="FramerateCap">2</int></x>', "int", "FramerateCap", "60")

    def test_old_background_process_does_not_block_config(self):
        class BackgroundProcess:
            pid = 123

            def create_time(self):
                return time.time() - 600

        with patch("roblox_optimizer.running_roblox", return_value=[BackgroundProcess()]), patch("roblox_optimizer.get_windows", return_value={}):
            self.assertFalse(game_windows_open())

    def test_realtime_batch_skips_reused_pid(self):
        class NewProcess:
            def create_time(self):
                return 200.0

        with patch("roblox_optimizer.psutil.Process", return_value=NewProcess()), patch("roblox_optimizer.trim_working_set") as trim:
            self.assertEqual(trim_batch([(123, 100.0)]), [(123, 0.0, "Phiên đã thay đổi")])
            trim.assert_not_called()

    def test_realtime_batch_reports_actual_working_set_drop(self):
        class SameProcess:
            def create_time(self):
                return 100.0

        with patch("roblox_optimizer.psutil.Process", return_value=SameProcess()), patch("roblox_optimizer.trim_working_set", return_value=(6144.0, 1024.0)):
            self.assertEqual(trim_batch([(123, 100.0)]), [(123, 5120.0, "")])

    def test_realtime_scheduler_does_not_overlap_batches(self):
        app = App(startup_auto=False)
        started, release = Event(), Event()
        calls = []

        def slow_batch(targets, _minimum=0):
            calls.append(targets)
            started.set()
            release.wait(2)
            return [(123, 1.0, "")]

        try:
            app.auto_trim.set(True)
            app.discovered_targets = {123: 100.0}
            with patch("roblox_optimizer.trim_batch", side_effect=slow_batch):
                app.realtime_tick()
                self.assertTrue(started.wait(1))
                app.realtime_tick()
                self.assertEqual(len(calls), 1)
                release.set()
                app.realtime_worker.join(1)
        finally:
            release.set()
            app.on_close()

    def test_realtime_batch_ignores_closed_instance(self):
        import psutil

        with patch("roblox_optimizer.psutil.Process", side_effect=psutil.NoSuchProcess(123)):
            self.assertEqual(trim_batch([(123, 100.0)]), [(123, 0.0, "Phiên đã thoát")])

    def test_auto_mode_requests_admin_when_needed(self):
        app = App(startup_auto=False)
        try:
            with patch("roblox_optimizer.older_optimizer_running", return_value=False), patch("roblox_optimizer.shell32.IsUserAnAdmin", return_value=False), patch.object(app, "relaunch_admin", return_value=False) as relaunch, patch("roblox_optimizer.save_state"):
                app.start_auto()
                relaunch.assert_called_once()
                self.assertFalse(app.auto_trim.get())
        finally:
            app.on_close()

    def test_auto_mode_starts_with_admin_and_discovers_windows(self):
        app = App(startup_auto=False)
        try:
            app.discovered_targets = {123: 100.0, 456: 200.0}
            with patch("roblox_optimizer.older_optimizer_running", return_value=False), patch("roblox_optimizer.shell32.IsUserAnAdmin", return_value=True), patch("roblox_optimizer.save_state"):
                app.start_auto()
            self.assertTrue(app.auto_trim.get())
            self.assertIn("2 phiên", app.realtime_text.get())
            self.assertTrue(app.state["auto_enabled"])
        finally:
            app.on_close()

    def test_interval_can_be_changed(self):
        app = App(startup_auto=False)
        try:
            app.interval_entry.set("0,1")
            with patch("roblox_optimizer.save_state"):
                app.apply_interval()
            self.assertAlmostEqual(app.interval_seconds, 0.1)
            self.assertAlmostEqual(app.state["interval_seconds"], 0.1)
            app.interval_entry.set("0.05")
            with patch("roblox_optimizer.save_state"):
                app.apply_interval()
            self.assertAlmostEqual(app.interval_seconds, 0.1)
        finally:
            app.on_close()

    def test_auto_waits_for_older_tool(self):
        app = App(startup_auto=False)
        try:
            with patch("roblox_optimizer.older_optimizer_running", return_value=True), patch("roblox_optimizer.save_state"):
                app.start_auto()
            self.assertTrue(app.waiting_for_old)
            self.assertFalse(app.auto_trim.get())
            with patch("roblox_optimizer.older_optimizer_running", return_value=False), patch("roblox_optimizer.shell32.IsUserAnAdmin", return_value=True), patch("roblox_optimizer.save_state"):
                app.start_auto()
            self.assertTrue(app.auto_trim.get())
        finally:
            app.on_close()

    def test_new_roblox_instance_joins_next_batch(self):
        app = App(startup_auto=False)
        calls = []

        def record_batch(targets, _minimum=0):
            calls.append(targets)
            return [(pid, 1.0, "") for pid, _ in targets]

        try:
            app.auto_trim.set(True)
            app.discovered_targets = {123: 100.0}
            with patch("roblox_optimizer.trim_batch", side_effect=record_batch):
                app.realtime_tick()
                app.realtime_worker.join(1)
                app.discovered_targets = {123: 100.0, 456: 200.0}
                app.next_trim_at = 0.0
                app.realtime_tick()
                app.realtime_worker.join(1)
            self.assertEqual(calls, [[(123, 100.0)], [(123, 100.0), (456, 200.0)]])
        finally:
            app.on_close()


    def test_auto_skips_small_working_set(self):
        class SmallProcess:
            def create_time(self):
                return 100.0

            def memory_info(self):
                return SimpleNamespace(rss=300 * 1024 * 1024)

        with patch("roblox_optimizer.psutil.Process", return_value=SmallProcess()), patch("roblox_optimizer.trim_working_set") as trim:
            self.assertEqual(trim_batch([(123, 100.0)], 384), [(123, 0.0, "")])
            trim.assert_not_called()

    def test_auto_trims_at_five_hundred_mb(self):
        class LargeProcess:
            def create_time(self):
                return 100.0

            def memory_info(self):
                return SimpleNamespace(rss=500 * 1024 * 1024)

        with patch("roblox_optimizer.psutil.Process", return_value=LargeProcess()), patch("roblox_optimizer.trim_working_set", return_value=(500.0, 300.0)) as trim:
            self.assertEqual(trim_batch([(123, 100.0)], 384), [(123, 200.0, "")])
            trim.assert_called_once_with(123)

    def test_buttons_have_square_corners(self):
        app = App(startup_auto=False)
        try:
            self.assertIsInstance(app.auto_button, tk.Button)
        finally:
            app.on_close()

    def test_cpu_choice_uses_original_allowed_processors(self):
        self.assertEqual(cpu_choice([2, 4, 6, 8], 2, 1), [4, 6])
        self.assertEqual(cpu_choice([2, 4, 6, 8], 1, 3), [8])

    def test_cpu_mode_restores_original_affinity_and_priority(self):
        class FakeProcess:
            def __init__(self):
                self.affinity = [0, 1, 2, 3]
                self.priority = 32

            def cpu_affinity(self, value=None):
                if value is not None:
                    self.affinity = value
                return self.affinity

            def nice(self, value=None):
                if value is not None:
                    self.priority = value
                return self.priority

        with patch("roblox_optimizer.load_state", return_value={}):
            app = App(startup_auto=False)
        process = FakeProcess()
        try:
            app.discovered_targets = {123: 100.0}
            with patch("roblox_optimizer.shell32.IsUserAnAdmin", return_value=True), patch("roblox_optimizer.matching_roblox", return_value=process), patch("roblox_optimizer.save_state"):
                app.cpu_setting.set("1 luồng")
                app.change_cpu_mode()
                self.assertEqual(process.affinity, [3])
                self.assertEqual(process.priority, psutil.BELOW_NORMAL_PRIORITY_CLASS)
                self.assertEqual(app.cpu_originals["123"]["affinity"], [0, 1, 2, 3])
                app.cpu_setting.set("Tắt")
                app.change_cpu_mode()
                self.assertEqual(process.affinity, [0, 1, 2, 3])
                self.assertEqual(process.priority, 32)
                self.assertFalse(app.cpu_originals)
        finally:
            app.on_close()

    def test_cpu_limit_rejects_reused_pid(self):
        class ReusedProcess:
            def create_time(self):
                return 200.0

        with patch("roblox_optimizer.psutil.Process", return_value=ReusedProcess()):
            with self.assertRaises(psutil.NoSuchProcess):
                matching_roblox(123, 100.0)

    def test_account_list_keeps_only_display_fields(self):
        response = '[{"Username":"alt_a","Alias":"Farm","Group":"Main","Cookie":"secret"}]'
        with patch("ram_integration._request", return_value=response) as request:
            accounts = list_ram_accounts(7963, "test-password")
        self.assertEqual([(account.username, account.alias, account.group) for account in accounts], [("alt_a", "Farm", "Main")])
        self.assertNotIn("secret", repr(accounts))
        self.assertEqual(request.call_args.args[1], "GetAccountsJson")
        self.assertEqual(request.call_args.args[2]["IncludeCookies"], "false")

    def test_selected_accounts_launch_in_order(self):
        settings = RamSettings(7963, True, True, True, True, "12345", 0)
        with patch("roblox_optimizer.load_state", return_value={}):
            app = App(startup_auto=False)
        try:
            with patch("roblox_optimizer.find_ram_executable", return_value=Path("C:/RAM/Roblox Account Manager.exe")), patch("roblox_optimizer.read_ram_settings", return_value=settings), patch("roblox_optimizer.save_state"):
                app.open_account_manager()
            app.account_table.insert("", "end", iid="0", values=("alt_a", "", ""))
            app.account_table.insert("", "end", iid="1", values=("alt_b", "", ""))
            app.select_all_accounts()
            app.account_password.set("test-password")
            with patch("roblox_optimizer.launch_ram_account") as launch:
                app.launch_selected_accounts()
                app.account_worker.join(2)
                app.poll_account_results()
            self.assertEqual([call.args[2] for call in launch.call_args_list], ["alt_a", "alt_b"])
            self.assertIn("2 tài khoản", app.account_status.get())
        finally:
            app.on_close()


if __name__ == "__main__":
    unittest.main()
