using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DrType
{
    // 引擎會在鍵盤執行緒上呼叫這些方法；實作端必須自行 BeginInvoke 到 UI 執行緒，不可阻塞。
    public interface IEngineHost
    {
        void ShowSuggestions(string[] items, int selected, bool firstShow);
        void SelectSuggestion(int selected);
        void HideSuggestions();
        void CommitWord(int backspaces, string text, bool releaseAltFirst);
        void ExpandHotstring(int backspaces, Hotstring hs, string trailing);
        void SendPlainSpace();
    }

    // 引擎用的設定快照；改設定時整個換新，鍵盤執行緒不必上鎖讀取。
    public sealed class EngineConfig
    {
        public bool Autocomplete;
        public bool Hotstrings;
        public int MinChars;
        public int MaxResults;
        public bool AcceptTab;
        public bool AcceptEnter;
        public bool Fuzzy;
        public bool CorrectCase;
        public bool AddSpace;
        public bool PauseInIme;
        public bool ShiftSpace;
        public HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public WordIndex Words = WordIndex.Empty;
        public Hotstring[] Immediate = new Hotstring[0];             // 依縮寫長度由長到短
        public Dictionary<string, Hotstring> OnEnd = new Dictionary<string, Hotstring>(StringComparer.OrdinalIgnoreCase);

        public static EngineConfig Build(AppSettings s, IList<string> words, IList<Hotstring> hotstrings)
        {
            var c = new EngineConfig
            {
                Autocomplete = s.AutocompleteEnabled,
                Hotstrings = s.HotstringsEnabled,
                MinChars = s.MinChars,
                MaxResults = s.MaxResults,
                AcceptTab = s.AcceptWithTab,
                AcceptEnter = s.AcceptWithEnter,
                Fuzzy = s.FuzzyMatch,
                CorrectCase = s.CorrectCase,
                AddSpace = s.AddSpaceAfter,
                PauseInIme = s.PauseInChineseIme,
                ShiftSpace = s.ShiftSpaceAsSpace,
                Words = new WordIndex(words)
            };
            foreach (var part in (s.ExcludedApps ?? "").Split(new[] { ';', ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = part.Trim();
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
                c.Excluded.Add(name);
            }
            var imm = new List<Hotstring>();
            foreach (var h in hotstrings)
            {
                if (string.IsNullOrEmpty(h.Abbr)) continue;
                if (h.Immediate) imm.Add(h);
                else c.OnEnd[h.Abbr] = h;
            }
            imm.Sort(delegate(Hotstring a, Hotstring b) { return b.Abbr.Length - a.Abbr.Length; });
            c.Immediate = imm.ToArray();
            return c;
        }
    }

    // 低階鍵盤/滑鼠攔截。跑在專屬的高優先權執行緒上，只做「記住目前打的字、決定要不要吞掉按鍵」，
    // 畫面與送字都交給 UI 執行緒，所以就算 UI 忙碌也不會拖慢全系統打字。
    public sealed class KeyboardEngine
    {
        private const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
        private const int VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_UP = 0x26, VK_DOWN = 0x28;
        private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_NUMPAD0 = 0x60, VK_NUMPAD9 = 0x69;
        private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA5;
        private const uint WM_APP_REHOOK = 0x8001;
        private const int MaxBuffer = 64;

        private readonly IEngineHost host;
        private readonly object sync = new object();
        private volatile EngineConfig config = new EngineConfig();
        private volatile bool enabled = true;

        // 以下狀態只在持有 sync 時存取
        private readonly StringBuilder word = new StringBuilder();
        private readonly StringBuilder hsBuf = new StringBuilder();
        private string[] suggestions = new string[0];
        private int selected;
        private bool popupShown;
        private Rectangle popupBounds;
        private IntPtr fgHwnd = IntPtr.Zero;
        private bool fgExcluded;
        private bool imeNative;
        private int imeCheckedAt;
        private bool imeValid;
        private readonly HashSet<uint> swallowedUp = new HashSet<uint>();
        private readonly byte[] keyState = new byte[256];
        private readonly StringBuilder charBuf = new StringBuilder(8);
        private readonly uint ownPid = NativeMethods.GetCurrentProcessId();

        private Thread thread;
        private uint threadId;
        private IntPtr kbHook = IntPtr.Zero, mouseHook = IntPtr.Zero;
        private NativeMethods.LowLevelProc kbProc, mouseProc; // 保留參考，避免被 GC 回收

        public KeyboardEngine(IEngineHost host)
        {
            this.host = host;
            kbProc = KeyboardProc;
            mouseProc = MouseProc;
        }

        public EngineConfig Config
        {
            get { return config; }
            set
            {
                config = value;
                lock (sync) ResetAll();
            }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                lock (sync) ResetAll();
                // 重新掛勾：Windows 偶爾會在系統忙碌時移除攔截，切換開關即可恢復
                if (value && threadId != 0) NativeMethods.PostThreadMessage(threadId, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public void SetPopupBounds(Rectangle r)
        {
            lock (sync) popupBounds = r;
        }

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate()
            {
                threadId = NativeMethods.GetCurrentThreadId();
                Install();
                ready.Set();
                NativeMethods.MSG msg;
                while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_APP_REHOOK)
                    {
                        Uninstall();
                        Install();
                        continue;
                    }
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
                Uninstall();
            });
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Name = "KeyboardHook";
            thread.Start();
            ready.WaitOne(3000);
        }

        public void Stop()
        {
            if (threadId != 0) NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (thread != null) thread.Join(1000);
        }

        private void Install()
        {
            IntPtr mod = NativeMethods.GetModuleHandle(null);
            kbHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, kbProc, mod, 0);
            mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, mouseProc, mod, 0);
        }

        private void Uninstall()
        {
            if (kbHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(kbHook);
            if (mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(mouseHook);
            kbHook = mouseHook = IntPtr.Zero;
        }

        // 由 UI 執行緒呼叫（使用者用滑鼠點選建議）
        public void AcceptIndex(int index)
        {
            lock (sync)
            {
                if (popupShown && index >= 0 && index < suggestions.Length) Accept(index, false);
            }
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == NativeMethods.WM_LBUTTONDOWN || msg == NativeMethods.WM_RBUTTONDOWN
                    || msg == NativeMethods.WM_MBUTTONDOWN || msg == NativeMethods.WM_XBUTTONDOWN)
                {
                    var m = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                    lock (sync)
                    {
                        if (!(popupShown && popupBounds.Contains(m.pt.X, m.pt.Y)))
                        {
                            ResetAll();
                            imeValid = false;
                        }
                    }
                }
            }
            return NativeMethods.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                if (k.dwExtraInfo != InputSender.Marker)
                {
                    int msg = (int)wParam;
                    bool swallow = false;
                    try
                    {
                        lock (sync)
                        {
                            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN) swallow = OnKeyDown(k);
                            else swallow = OnKeyUp(k);
                        }
                    }
                    catch (Exception)
                    {
                        // 攔截函式絕不能拋出例外，否則會拖垮整個輸入
                    }
                    if (swallow) return new IntPtr(1);
                }
            }
            return NativeMethods.CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        private bool OnKeyUp(NativeMethods.KBDLLHOOKSTRUCT k)
        {
            uint vk = k.vkCode;
            if (IsShiftOrCtrl(vk)) imeValid = false; // 可能剛切換中/英，下次重新檢查輸入法
            return swallowedUp.Remove(vk);
        }

        private bool OnKeyDown(NativeMethods.KBDLLHOOKSTRUCT k)
        {
            var cfg = config;
            uint vk = k.vkCode;
            if (!enabled || (!cfg.Autocomplete && !cfg.Hotstrings)) return false;
            if (IsModifier(vk)) return false;

            if ((k.flags & NativeMethods.LLKHF_INJECTED) != 0)
            {
                // 其他程式模擬的按鍵（遠端軟體、其他腳本）不列入計算
                ResetAll();
                return false;
            }

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != fgHwnd)
            {
                ResetAll();
                fgHwnd = fg;
                fgExcluded = IsExcluded(fg, cfg);
                imeValid = false;
            }
            if (fgExcluded) return false;

            bool shift = NativeMethods.IsDown(VK_SHIFT);
            bool ctrl = NativeMethods.IsDown(VK_CONTROL);
            bool alt = NativeMethods.IsDown(VK_MENU);
            bool win = NativeMethods.IsDown(VK_LWIN) || NativeMethods.IsDown(VK_RWIN);

            if (popupShown && suggestions.Length > 0 && !ctrl && !win)
            {
                if (alt)
                {
                    int digit = -1;
                    if (vk >= '0' && vk <= '9') digit = (int)vk - '0';
                    else if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) digit = (int)vk - VK_NUMPAD0;
                    if (digit >= 0)
                    {
                        int idx = digit == 0 ? 9 : digit - 1;
                        if (idx < suggestions.Length)
                        {
                            Accept(idx, true);
                            swallowedUp.Add(vk);
                            return true;
                        }
                    }
                }
                else
                {
                    switch ((int)vk)
                    {
                        case VK_UP:
                            selected = (selected - 1 + suggestions.Length) % suggestions.Length;
                            host.SelectSuggestion(selected);
                            swallowedUp.Add(vk);
                            return true;
                        case VK_DOWN:
                            selected = (selected + 1) % suggestions.Length;
                            host.SelectSuggestion(selected);
                            swallowedUp.Add(vk);
                            return true;
                        case VK_TAB:
                            if (cfg.AcceptTab && !shift)
                            {
                                Accept(selected, false);
                                swallowedUp.Add(vk);
                                return true;
                            }
                            break;
                        case VK_RETURN:
                            // 已經打出完整的詞（例如 with，建議是 without；不分大小寫）時，Enter 照常換行
                            if (cfg.AcceptEnter && !shift && !EnterMeansNewline(cfg))
                            {
                                Accept(selected, false);
                                swallowedUp.Add(vk);
                                return true;
                            }
                            break;
                        case VK_ESCAPE:
                            ResetAll();
                            swallowedUp.Add(vk);
                            return true;
                    }
                }
            }

            if (ctrl || alt || win)
            {
                ResetAll();
                return false;
            }

            if (vk == VK_BACK)
            {
                if (hsBuf.Length > 0) hsBuf.Length--;
                if (word.Length > 0)
                {
                    word.Length--;
                    UpdateSuggestions(cfg);
                }
                return false;
            }

            // Shift+空白 在注音中文模式下會切換全形，改送一般空白（不論輸入法狀態都處理）
            if (vk == VK_SPACE && shift && cfg.ShiftSpace)
            {
                word.Length = 0;
                hsBuf.Length = 0;
                HidePopup();
                host.SendPlainSpace();
                swallowedUp.Add(vk);
                return true;
            }

            char c = Translate(vk, k.scanCode, shift, fg);
            if (c == '\0')
            {
                ResetAll();
                return false;
            }

            if (cfg.PauseInIme && InChineseMode(fg))
            {
                ResetAll();
                return false;
            }

            if (c == ' ' || c == '\n' || c == '\t')
            {
                if (cfg.Hotstrings && cfg.OnEnd.Count > 0)
                {
                    int start = LastWhitespace(hsBuf) + 1;
                    string candidate = hsBuf.ToString(start, hsBuf.Length - start);
                    Hotstring hs;
                    if (candidate.Length > 0 && cfg.OnEnd.TryGetValue(candidate, out hs))
                    {
                        host.ExpandHotstring(candidate.Length, hs, c.ToString());
                        ResetAll();
                        swallowedUp.Add(vk);
                        return true;
                    }
                }
                word.Length = 0;
                HidePopup();
                hsBuf.Append(c);
                TrimBuffer();
                return false;
            }

            if (cfg.Hotstrings)
            {
                hsBuf.Append(c);
                TrimBuffer();
                foreach (var hs in cfg.Immediate)
                {
                    if (EndsWithIgnoreCase(hsBuf, hs.Abbr))
                    {
                        // 吞掉最後一個字，前面已打出的縮寫用 Backspace 刪除
                        host.ExpandHotstring(hs.Abbr.Length - 1, hs, null);
                        ResetAll();
                        swallowedUp.Add(vk);
                        return true;
                    }
                }
            }

            if (IsWordChar(c))
            {
                if (cfg.Autocomplete)
                {
                    if (word.Length >= MaxBuffer) word.Length = 0;
                    word.Append(c);
                    UpdateSuggestions(cfg);
                }
            }
            else
            {
                word.Length = 0;
                HidePopup();
            }
            return false;
        }

        private void UpdateSuggestions(EngineConfig cfg)
        {
            if (!cfg.Autocomplete || word.Length < cfg.MinChars)
            {
                HidePopup();
                return;
            }
            var list = cfg.Words.Suggest(word.ToString(), cfg.MaxResults, cfg.Fuzzy);
            if (list.Count == 0)
            {
                HidePopup();
                return;
            }
            suggestions = list.ToArray();
            selected = 0;
            bool first = !popupShown;
            popupShown = true;
            host.ShowSuggestions(suggestions, selected, first);
        }

        // 使用者沒有用方向鍵另選，而且目前打的字已經是詞庫裡的詞時，Enter 交給程式換行
        private bool EnterMeansNewline(EngineConfig cfg)
        {
            if (selected != 0) return false;
            string typed = word.ToString();
            return cfg.Words.Contains(typed) || FinalWord(cfg, typed, suggestions[0]) == typed;
        }

        private static string FinalWord(EngineConfig cfg, string typed, string suggestion)
        {
            return cfg.CorrectCase ? WordIndex.MatchCase(typed, suggestion) : suggestion;
        }

        private void Accept(int index, bool altHeld)
        {
            var cfg = config;
            string typed = word.ToString();
            string w = FinalWord(cfg, typed, suggestions[index]);
            if (cfg.AddSpace) w += " ";
            // 建議字以目前輸入開頭時只補後面的字，省去 Backspace
            if (w.StartsWith(typed, StringComparison.Ordinal)) host.CommitWord(0, w.Substring(typed.Length), altHeld);
            else host.CommitWord(typed.Length, w, altHeld);
            ResetAll();
        }

        private void HidePopup()
        {
            if (!popupShown) return;
            popupShown = false;
            suggestions = new string[0];
            host.HideSuggestions();
        }

        private void ResetAll()
        {
            word.Length = 0;
            hsBuf.Length = 0;
            HidePopup();
        }

        private void TrimBuffer()
        {
            if (hsBuf.Length > MaxBuffer) hsBuf.Remove(0, hsBuf.Length - MaxBuffer);
        }

        private char Translate(uint vk, uint scan, bool shift, IntPtr fg)
        {
            if (vk == VK_RETURN) return '\n';
            if (vk == VK_TAB) return '\t';
            if (vk == VK_SPACE) return ' ';
            Array.Clear(keyState, 0, keyState.Length);
            if (shift)
            {
                keyState[VK_SHIFT] = 0x80;
                keyState[VK_LSHIFT] = 0x80;
            }
            if ((NativeMethods.GetKeyState(VK_CAPITAL) & 1) != 0) keyState[VK_CAPITAL] = 0x01;
            uint pid;
            IntPtr hkl = NativeMethods.GetKeyboardLayout(NativeMethods.GetWindowThreadProcessId(fg, out pid));
            charBuf.Length = 0;
            // flag 4：不改變鍵盤狀態（不會吃掉 dead key），Windows 10 1607 起支援
            int n = NativeMethods.ToUnicodeEx(vk, scan, keyState, charBuf, 8, 4, hkl);
            if (n != 1 || charBuf.Length < 1) return '\0';
            char c = charBuf[0];
            return c < 0x20 || c == 0x7F ? '\0' : c;
        }

        // 注音/倉頡等中文輸入法在「中文模式」時不提示，避免干擾選字。
        private bool InChineseMode(IntPtr fg)
        {
            int now = Environment.TickCount;
            if (imeValid && unchecked(now - imeCheckedAt) < 1000) return imeNative;
            imeNative = false;
            IntPtr ime = NativeMethods.ImmGetDefaultIMEWnd(fg);
            if (ime != IntPtr.Zero)
            {
                IntPtr open, mode;
                if (NativeMethods.SendMessageTimeout(ime, NativeMethods.WM_IME_CONTROL, new IntPtr(NativeMethods.IMC_GETOPENSTATUS), IntPtr.Zero,
                        NativeMethods.SMTO_ABORTIFHUNG, 20, out open) != IntPtr.Zero && open != IntPtr.Zero)
                {
                    if (NativeMethods.SendMessageTimeout(ime, NativeMethods.WM_IME_CONTROL, new IntPtr(NativeMethods.IMC_GETCONVERSIONMODE), IntPtr.Zero,
                            NativeMethods.SMTO_ABORTIFHUNG, 20, out mode) != IntPtr.Zero)
                        imeNative = ((long)mode & NativeMethods.IME_CMODE_NATIVE) != 0;
                }
            }
            imeCheckedAt = now;
            imeValid = true;
            return imeNative;
        }

        private bool IsExcluded(IntPtr hwnd, EngineConfig cfg)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == ownPid) return true; // 自己的設定視窗不觸發
            if (cfg.Excluded.Count == 0) return false;
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size)) return false;
                string path = sb.ToString();
                int slash = path.LastIndexOf('\\');
                return cfg.Excluded.Contains(slash >= 0 ? path.Substring(slash + 1) : path);
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '\'' || c == '-' || c == '_';
        }

        private static bool IsModifier(uint vk)
        {
            return (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == VK_LWIN || vk == VK_RWIN
                || vk == VK_CAPITAL || vk == 0x90 /* NumLock */ || vk == 0xE8;
        }

        private static bool IsShiftOrCtrl(uint vk)
        {
            return vk == VK_SHIFT || vk == VK_CONTROL || (vk >= VK_LSHIFT && vk <= 0xA3);
        }

        private static int LastWhitespace(StringBuilder sb)
        {
            for (int i = sb.Length - 1; i >= 0; i--)
            {
                char c = sb[i];
                if (c == ' ' || c == '\n' || c == '\t') return i;
            }
            return -1;
        }

        private static bool EndsWithIgnoreCase(StringBuilder sb, string s)
        {
            int n = s.Length;
            if (n == 0 || sb.Length < n) return false;
            int off = sb.Length - n;
            for (int i = 0; i < n; i++)
            {
                if (char.ToLowerInvariant(sb[off + i]) != char.ToLowerInvariant(s[i])) return false;
            }
            return true;
        }
    }
}
