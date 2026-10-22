using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DrType
{
    // 負責把文字「打」出去。預設用 KEYEVENTF_UNICODE 直接送字元，
    // 不經過剪貼簿，也不會被注音輸入法攔截。
    public static class InputSender
    {
        // 標記自己送出的按鍵，鍵盤攔截時看到這個值就略過。
        public static readonly IntPtr Marker = new IntPtr(0x54594148);

        private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const ushort VK_BACK = 0x08, VK_RETURN = 0x0D, VK_TAB = 0x09, VK_LEFT = 0x25, VK_MASK = 0xE8;
        private static readonly int InputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        // 使用者還按著 Alt/Ctrl/Shift 時送字會變成快捷鍵（例如 Alt+Backspace = 復原），
        // 所以先等放開；逾時就直接送出放開事件。
        public static void WaitModifiersReleased(int timeoutMs)
        {
            int waited = 0;
            while (AnyModifierDown() && waited < timeoutMs)
            {
                Thread.Sleep(10);
                waited += 10;
            }
            if (!AnyModifierDown()) return;
            var list = new List<NativeMethods.INPUT>();
            foreach (ushort vk in new ushort[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN })
            {
                if (NativeMethods.IsDown(vk))
                {
                    if (vk == VK_MENU || vk == VK_LWIN || vk == VK_RWIN)
                    {
                        AddKey(list, VK_MASK, false);
                        AddKey(list, VK_MASK, true);
                    }
                    AddKey(list, vk, true);
                }
            }
            Flush(list);
        }

        private static bool AnyModifierDown()
        {
            return NativeMethods.IsDown(VK_SHIFT) || NativeMethods.IsDown(VK_CONTROL) || NativeMethods.IsDown(VK_MENU)
                || NativeMethods.IsDown(VK_LWIN) || NativeMethods.IsDown(VK_RWIN);
        }

        // 送一個沒有用途的按鍵，避免單獨放開 Alt 時觸發程式的選單列。
        public static void SendMaskKey()
        {
            var list = new List<NativeMethods.INPUT>();
            AddKey(list, VK_MASK, false);
            AddKey(list, VK_MASK, true);
            Flush(list);
        }

        public static void TapKey(ushort vk)
        {
            var list = new List<NativeMethods.INPUT>();
            AddKey(list, vk, false);
            AddKey(list, vk, true);
            Flush(list);
        }

        public static void Backspace(int count)
        {
            if (count <= 0) return;
            var list = new List<NativeMethods.INPUT>();
            for (int i = 0; i < count; i++)
            {
                AddKey(list, VK_BACK, false);
                AddKey(list, VK_BACK, true);
            }
            Flush(list);
            // 有輸入法的程式可能比字元晚處理 Backspace，順序一亂就會變成 rRt，所以先等一下
            Thread.Sleep(30);
        }

        public static void Send(List<OutAction> actions, OutputMode mode)
        {
            int leftPresses = CursorLeftPresses(actions);
            bool paste = mode == OutputMode.PasteCtrlV || mode == OutputMode.PasteCtrlShiftV;
            IDataObject savedClipboard = null;
            if (paste) savedClipboard = SaveClipboard();

            IntPtr hkl = IntPtr.Zero;
            if (mode == OutputMode.VirtualKey)
            {
                uint pid;
                hkl = NativeMethods.GetKeyboardLayout(NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out pid));
            }

            var list = new List<NativeMethods.INPUT>();
            try
            {
                foreach (var a in actions)
                {
                    switch (a.Kind)
                    {
                        case ActionKind.Text:
                            if (paste)
                            {
                                Flush(list);
                                PasteText(a.Text, mode == OutputMode.PasteCtrlShiftV);
                            }
                            else AddText(list, a.Text, mode == OutputMode.VirtualKey, hkl);
                            break;
                        case ActionKind.Key:
                            for (int r = 0; r < a.Repeat; r++) AddCombo(list, a);
                            break;
                        case ActionKind.Wait:
                            Flush(list);
                            Thread.Sleep(a.Ms);
                            break;
                    }
                }
                for (int i = 0; i < leftPresses; i++)
                {
                    AddKey(list, VK_LEFT, false);
                    AddKey(list, VK_LEFT, true);
                }
                Flush(list);
            }
            finally
            {
                if (paste) RestoreClipboard(savedClipboard);
            }
        }

        // {cursor} 之後還有幾個字元，就按幾次 ←。
        private static int CursorLeftPresses(List<OutAction> actions)
        {
            int idx = actions.FindIndex(delegate(OutAction a) { return a.Kind == ActionKind.Cursor; });
            if (idx < 0) return 0;
            int n = 0;
            for (int i = idx + 1; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.Kind == ActionKind.Text)
                {
                    foreach (char c in a.Text)
                        if (c != '\r' && !char.IsLowSurrogate(c)) n++;
                }
                else if (a.Kind == ActionKind.Key)
                {
                    if (a.Vk == VK_RETURN && !a.Ctrl && !a.Alt && !a.Shift && !a.Win) n += a.Repeat;
                    else return 0; // 游標之後還有其他按鍵，無法推算位置
                }
            }
            return n;
        }

        private static void AddText(List<NativeMethods.INPUT> list, string text, bool virtualKeys, IntPtr hkl)
        {
            bool caps = (NativeMethods.GetKeyState(0x14) & 1) != 0;
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    AddKey(list, VK_RETURN, false);
                    AddKey(list, VK_RETURN, true);
                    continue;
                }
                if (c == '\t')
                {
                    AddKey(list, VK_TAB, false);
                    AddKey(list, VK_TAB, true);
                    continue;
                }
                if (virtualKeys && c < 128)
                {
                    short scan = NativeMethods.VkKeyScanEx(c, hkl);
                    if (scan != -1)
                    {
                        ushort vk = (ushort)(scan & 0xFF);
                        int state = (scan >> 8) & 0xFF;
                        if ((state & 6) == 0)
                        {
                            bool shift = (state & 1) != 0;
                            if (caps && char.IsLetter(c)) shift = !shift;
                            if (shift) AddKey(list, VK_SHIFT, false);
                            AddKey(list, vk, false);
                            AddKey(list, vk, true);
                            if (shift) AddKey(list, VK_SHIFT, true);
                            continue;
                        }
                    }
                }
                AddUnicode(list, c, false);
                AddUnicode(list, c, true);
            }
        }

        private static void AddCombo(List<NativeMethods.INPUT> list, OutAction a)
        {
            if (a.Ctrl) AddKey(list, VK_CONTROL, false);
            if (a.Shift) AddKey(list, VK_SHIFT, false);
            if (a.Alt) AddKey(list, VK_MENU, false);
            if (a.Win) AddKey(list, VK_LWIN, false);
            AddKey(list, a.Vk, false);
            AddKey(list, a.Vk, true);
            if (a.Win) AddKey(list, VK_LWIN, true);
            if (a.Alt) AddKey(list, VK_MENU, true);
            if (a.Shift) AddKey(list, VK_SHIFT, true);
            if (a.Ctrl) AddKey(list, VK_CONTROL, true);
        }

        private static void AddKey(List<NativeMethods.INPUT> list, ushort vk, bool up)
        {
            var inp = new NativeMethods.INPUT();
            inp.type = NativeMethods.INPUT_KEYBOARD;
            inp.U.ki.wVk = vk;
            inp.U.ki.wScan = (ushort)NativeMethods.MapVirtualKey(vk, 0);
            inp.U.ki.dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0) | (Expansion.IsExtendedKey(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0);
            inp.U.ki.dwExtraInfo = Marker;
            list.Add(inp);
        }

        private static void AddUnicode(List<NativeMethods.INPUT> list, char c, bool up)
        {
            var inp = new NativeMethods.INPUT();
            inp.type = NativeMethods.INPUT_KEYBOARD;
            inp.U.ki.wVk = 0;
            inp.U.ki.wScan = c;
            inp.U.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0);
            inp.U.ki.dwExtraInfo = Marker;
            list.Add(inp);
        }

        private static void Flush(List<NativeMethods.INPUT> list)
        {
            if (list.Count == 0) return;
            // 分批送出，避免一次塞太多讓某些程式掉字
            const int chunk = 400;
            for (int i = 0; i < list.Count; i += chunk)
            {
                int n = Math.Min(chunk, list.Count - i);
                var arr = list.GetRange(i, n).ToArray();
                NativeMethods.SendInput((uint)arr.Length, arr, InputSize);
            }
            list.Clear();
        }

        private static void PasteText(string text, bool ctrlShift)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text.Replace("\r\n", "\n").Replace("\n", "\r\n")), true, 10, 30);
            }
            catch (Exception)
            {
                return;
            }
            var list = new List<NativeMethods.INPUT>();
            AddCombo(list, new OutAction { Kind = ActionKind.Key, Vk = (ushort)'V', Ctrl = true, Shift = ctrlShift });
            Flush(list);
            Thread.Sleep(120); // 等目標程式讀完剪貼簿再繼續
        }

        private static IDataObject SaveClipboard()
        {
            try
            {
                var cur = Clipboard.GetDataObject();
                if (cur == null) return null;
                var copy = new DataObject();
                foreach (var fmt in cur.GetFormats(false))
                {
                    try
                    {
                        var d = cur.GetData(fmt, false);
                        if (d != null) copy.SetData(fmt, false, d);
                    }
                    catch (Exception) { }
                }
                return copy;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RestoreClipboard(IDataObject saved)
        {
            try
            {
                Thread.Sleep(80);
                if (saved == null) Clipboard.Clear();
                else Clipboard.SetDataObject(saved, true, 10, 30);
            }
            catch (Exception) { }
        }

        public static string GetClipboardText()
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
