using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DrType
{
    public static class HotkeyText
    {
        // "Ctrl+Alt+Q" ⇄ RegisterHotKey 用的修飾鍵與按鍵
        public static bool Parse(string text, out uint mods, out Keys key)
        {
            mods = 0;
            key = Keys.None;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var raw in text.Split('+'))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                var l = p.ToLowerInvariant();
                if (l == "ctrl" || l == "control") mods |= NativeMethods.MOD_CONTROL;
                else if (l == "alt") mods |= NativeMethods.MOD_ALT;
                else if (l == "shift") mods |= NativeMethods.MOD_SHIFT;
                else if (l == "win") mods |= NativeMethods.MOD_WIN;
                else
                {
                    try
                    {
                        if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                        key = (Keys)Enum.Parse(typeof(Keys), p, true);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }
            }
            return key != Keys.None;
        }

        public static string Format(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.None || key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin) return null;
            var parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            string k = key.ToString();
            if (key >= Keys.D0 && key <= Keys.D9) k = k.Substring(1);
            parts.Add(k);
            return string.Join("+", parts.ToArray());
        }
    }

    // 只用來接收全域快捷鍵的隱藏視窗
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int Id = 1;
        private bool registered;
        public event Action Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(string hotkey)
        {
            Unregister();
            uint mods;
            Keys key;
            if (!HotkeyText.Parse(hotkey, out mods, out key)) return string.IsNullOrEmpty(hotkey);
            registered = NativeMethods.RegisterHotKey(Handle, Id, mods | NativeMethods.MOD_NOREPEAT, (uint)key);
            return registered;
        }

        public void Unregister()
        {
            if (registered) NativeMethods.UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && Pressed != null) Pressed();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }

    public sealed class TrayApp : ApplicationContext, IEngineHost
    {
        public readonly AppData Data;
        private readonly KeyboardEngine engine;
        private readonly SuggestionPopup popup;
        private readonly Osd osd;
        private readonly NotifyIcon tray;
        private readonly HotkeyWindow hotkey;
        private readonly Icon iconOn, iconOff;
        private ToolStripMenuItem toggleItem, syncItem;
        private SettingsForm settingsForm;
        private readonly Thread showSignalThread;
        private readonly EventWaitHandle showSignal;

        public TrayApp(EventWaitHandle showSignal)
        {
            Data = new AppData();
            AutoStart.MigrateOldName();

            popup = new SuggestionPopup();
            popup.SetFontSize(Data.Settings.PopupFontSize);
            UpdatePopupHint();
            var force = popup.Handle; // 先在 UI 執行緒建立視窗，之後才能 BeginInvoke
            popup.ItemClicked += delegate(int i) { engine.AcceptIndex(i); };
            popup.BoundsChangedOnScreen += delegate(Rectangle r) { engine.SetPopupBounds(r); };
            osd = new Osd();
            force = osd.Handle;

            iconOn = Icons.Make(true, SystemInformation.SmallIconSize.Width);
            iconOff = Icons.Make(false, SystemInformation.SmallIconSize.Width);

            engine = new KeyboardEngine(this);
            engine.Config = EngineConfig.Build(Data.Settings, Data.Words, Data.Hotstrings);
            engine.Start();

            tray = new NotifyIcon();
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowSettings(0);
            };
            tray.Visible = true;

            hotkey = new HotkeyWindow();
            hotkey.Pressed += Toggle;
            RegisterHotkey(false);
            UpdateTrayState();

            this.showSignal = showSignal;
            showSignalThread = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        showSignal.WaitOne();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    Post(delegate { ShowSettings(-1); });
                }
            });
            showSignalThread.IsBackground = true;
            showSignalThread.Start();

            osd.Flash("DrType 已啟動", Icons.OnColor);
            if (Data.Settings.SyncOnStartup) SyncAsync(null);
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Font = new Font("Microsoft JhengHei UI", 10);
            toggleItem = new ToolStripMenuItem("暫停", null, delegate { Toggle(); });
            m.Items.Add(toggleItem);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("詞庫…", null, delegate { ShowSettings(0); }));
            m.Items.Add(new ToolStripMenuItem("快捷片語…", null, delegate { ShowSettings(1); }));
            syncItem = new ToolStripMenuItem("立即同步", null, delegate { SyncAsync(null); });
            m.Items.Add(syncItem);
            m.Items.Add(new ToolStripMenuItem("設定…", null, delegate { ShowSettings(3); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("結束 DrType", null, delegate { ExitThread(); }));
            m.Opening += delegate
            {
                syncItem.Enabled = Data.Settings.WordsSource.Length > 0 || Data.Settings.HotstringsSource.Length > 0;
            };
            return m;
        }

        private void Post(MethodInvoker action)
        {
            try
            {
                if (popup.IsHandleCreated && !popup.IsDisposed) popup.BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        // ===== 開關 =====

        public void Toggle()
        {
            engine.Enabled = !engine.Enabled;
            UpdateTrayState();
            osd.Flash(engine.Enabled ? "DrType：開啟" : "DrType：已暫停", engine.Enabled ? Icons.OnColor : Icons.OffColor);
        }

        private void UpdateTrayState()
        {
            bool on = engine.Enabled;
            tray.Icon = on ? iconOn : iconOff;
            string hk = string.IsNullOrEmpty(Data.Settings.ToggleHotkey) ? "" : "（" + Data.Settings.ToggleHotkey + "）";
            tray.Text = on ? "DrType：開啟" : "DrType：已暫停";
            toggleItem.Text = (on ? "暫停" : "開啟") + hk;
            toggleItem.Checked = !on;
        }

        public bool RegisterHotkey(bool warn)
        {
            bool ok = hotkey.Register(Data.Settings.ToggleHotkey);
            if (!ok && warn)
                MessageBox.Show("快捷鍵「" + Data.Settings.ToggleHotkey + "」無法使用，可能已被其他程式占用，請換一組。", "DrType",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (toggleItem != null) UpdateTrayState();
            return ok;
        }

        public void SuspendHotkey()
        {
            hotkey.Unregister();
        }

        // ===== 資料變更後套用 =====

        public void ApplyChanges()
        {
            engine.Config = EngineConfig.Build(Data.Settings, Data.Words, Data.Hotstrings);
            popup.SetFontSize(Data.Settings.PopupFontSize);
            UpdatePopupHint();
        }

        private void UpdatePopupHint()
        {
            var s = Data.Settings;
            string keys = s.AcceptWithTab && s.AcceptWithEnter ? "Tab/Enter" : (s.AcceptWithTab ? "Tab" : (s.AcceptWithEnter ? "Enter" : "Alt+數字"));
            popup.SetHint(keys + " 選取 · ↑↓ 移動 · Esc 關閉");
        }

        // ===== IEngineHost（由鍵盤執行緒呼叫，一律轉到 UI 執行緒） =====

        public void ShowSuggestions(string[] items, int selected, bool firstShow)
        {
            Post(delegate { popup.ShowItems(items, selected, firstShow); });
        }

        public void SelectSuggestion(int selected)
        {
            Post(delegate { popup.Select(selected); });
        }

        public void HideSuggestions()
        {
            Post(delegate { popup.HidePopup(); });
        }

        public void CommitWord(int backspaces, string text, bool releaseAltFirst)
        {
            Post(delegate
            {
                if (releaseAltFirst) InputSender.SendMaskKey();
                InputSender.WaitModifiersReleased(800);
                InputSender.Backspace(backspaces);
                var actions = new List<OutAction>();
                actions.Add(new OutAction { Kind = ActionKind.Text, Text = text });
                InputSender.Send(actions, Data.Settings.Output);
            });
        }

        public void ExpandHotstring(int backspaces, Hotstring hs, string trailing)
        {
            Post(delegate
            {
                var actions = Expansion.Parse(hs.Content, DateTime.Now, InputSender.GetClipboardText);
                if (!string.IsNullOrEmpty(trailing)) actions.Add(new OutAction { Kind = ActionKind.Text, Text = trailing });
                InputSender.WaitModifiersReleased(1000);
                InputSender.Backspace(backspaces);
                InputSender.Send(actions, Data.Settings.Output);
            });
        }

        public void SendPlainSpace()
        {
            Post(delegate { InputSender.TapKey(0x20); });
        }

        // ===== 同步 =====

        // log 為 null 時用 OSD 顯示結果
        public void SyncAsync(Action<string> log)
        {
            var s = Data.Settings.Clone();
            if (s.WordsSource.Length == 0 && s.HotstringsSource.Length == 0)
            {
                if (log != null) log("尚未設定同步來源。");
                return;
            }
            if (log != null) log("同步中…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> words = null;
                List<Hotstring> hs = null;
                var errors = new List<string>();
                if (s.WordsSource.Length > 0)
                {
                    try
                    {
                        words = SyncService.ParseWords(s.WordsSource, SyncService.Fetch(s.WordsSource));
                    }
                    catch (Exception ex)
                    {
                        errors.Add("詞庫同步失敗：" + ex.Message);
                    }
                }
                if (s.HotstringsSource.Length > 0)
                {
                    try
                    {
                        hs = HotstringFile.Parse(SyncService.Fetch(s.HotstringsSource));
                    }
                    catch (Exception ex)
                    {
                        errors.Add("片語同步失敗：" + ex.Message);
                    }
                }
                Post(delegate
                {
                    var msgs = new List<string>(errors);
                    if (words != null)
                    {
                        if (words.Count == 0) msgs.Add("詞庫來源是空的，未變更。");
                        else
                        {
                            int added;
                            if (s.SyncMode == SyncMode.Replace) Data.Backup(Data.WordsPath);
                            Data.Words = SyncService.MergeWords(Data.Words, words, s.SyncMode, out added);
                            Data.SaveWords();
                            msgs.Add("詞庫：新增 " + added + " 個，目前共 " + Data.Words.Count + " 個。");
                        }
                    }
                    if (hs != null)
                    {
                        if (hs.Count == 0) msgs.Add("片語來源是空的，未變更。");
                        else
                        {
                            int added, updated;
                            if (s.SyncMode == SyncMode.Replace) Data.Backup(Data.HotstringsPath);
                            Data.Hotstrings = SyncService.MergeHotstrings(Data.Hotstrings, hs, s.SyncMode, out added, out updated);
                            Data.SaveHotstrings();
                            msgs.Add("片語：新增 " + added + " 個、更新 " + updated + " 個，目前共 " + Data.Hotstrings.Count + " 個。");
                        }
                    }
                    ApplyChanges();
                    if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.ReloadData();
                    string text = string.Join("\r\n", msgs.ToArray());
                    if (log != null) log(text);
                    else osd.Flash(errors.Count > 0 ? "同步失敗，請到設定查看" : "同步完成", errors.Count > 0 ? Color.OrangeRed : Icons.OnColor);
                });
            });
        }

        // ===== 視窗 =====

        public void ShowSettings(int tab)
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(this);
                settingsForm.FormClosed += delegate { RegisterHotkey(false); };
            }
            if (tab >= 0) settingsForm.SelectTab(tab);
            settingsForm.Show();
            if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
        }

        protected override void ExitThreadCore()
        {
            engine.Stop();
            hotkey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            popup.Close();
            osd.Close();
            base.ExitThreadCore();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { ReportError(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { ReportError(e.ExceptionObject as Exception); };

            bool created;
            using (var mutex = new Mutex(true, "Local\\DrType_SingleInstance", out created))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DrType_ShowSettings"))
            {
                if (!created)
                {
                    // 已經在執行：叫原本的那個打開設定視窗
                    showSignal.Set();
                    return;
                }
                Application.Run(new TrayApp(showSignal));
                GC.KeepAlive(mutex);
            }
        }

        private static void ReportError(Exception ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "\r\n" + ex + "\r\n\r\n");
            }
            catch (Exception) { }
        }
    }
}
