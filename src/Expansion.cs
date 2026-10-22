using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DrType
{
    public enum ActionKind
    {
        Text,
        Key,
        Wait,
        Cursor
    }

    public class OutAction
    {
        public ActionKind Kind;
        public string Text;    // Text
        public ushort Vk;      // Key
        public bool Ctrl, Shift, Alt, Win;
        public int Repeat = 1; // Key
        public int Ms;         // Wait
    }

    // 片語內容語法：
    //   一般文字照打；換行 = Enter
    //   {Enter} {Tab} {Esc} {Up} {Down} {Left} {Right} {Home} {End} {Del} {BS} {F1}..{F12}
    //   {Ctrl+A} {Shift+Tab} {Alt+F4}... 組合鍵；{Tab 3} 重複按 3 次
    //   {date} {date:MM/dd} {time} {time:HH:mm:ss}   日期時間（.NET 格式字串）
    //   {rocdate} 民國日期 115/10/22
    //   {clipboard} 剪貼簿文字　{cursor} 展開後游標停在這裡　{wait:100} 暫停 100 毫秒
    //   {{ 輸出 {
    public static class Expansion
    {
        private static readonly Dictionary<string, ushort> KeyNames = BuildKeyNames();

        private static Dictionary<string, ushort> BuildKeyNames()
        {
            var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            d["Enter"] = 0x0D; d["Return"] = 0x0D;
            d["Tab"] = 0x09;
            d["Esc"] = 0x1B; d["Escape"] = 0x1B;
            d["Space"] = 0x20;
            d["BS"] = 0x08; d["Backspace"] = 0x08;
            d["Del"] = 0x2E; d["Delete"] = 0x2E;
            d["Ins"] = 0x2D; d["Insert"] = 0x2D;
            d["Up"] = 0x26; d["Down"] = 0x28; d["Left"] = 0x25; d["Right"] = 0x27;
            d["Home"] = 0x24; d["End"] = 0x23;
            d["PgUp"] = 0x21; d["PageUp"] = 0x21; d["PgDn"] = 0x22; d["PageDown"] = 0x22;
            for (int i = 1; i <= 12; i++) d["F" + i] = (ushort)(0x70 + i - 1);
            return d;
        }

        public static bool IsExtendedKey(ushort vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E;
        }

        // clipboardText：展開時才取得剪貼簿內容（可為 null）。
        public static List<OutAction> Parse(string content, DateTime now, Func<string> clipboardText)
        {
            var actions = new List<OutAction>();
            var text = new StringBuilder();
            string s = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{')
                    {
                        text.Append('{');
                        i += 2;
                        continue;
                    }
                    int close = s.IndexOf('}', i + 1);
                    if (close > i + 1)
                    {
                        string token = s.Substring(i + 1, close - i - 1);
                        if (TryToken(token, now, clipboardText, text, actions))
                        {
                            i = close + 1;
                            continue;
                        }
                    }
                }
                if (c == '\n')
                {
                    Flush(text, actions);
                    actions.Add(new OutAction { Kind = ActionKind.Key, Vk = 0x0D });
                }
                else text.Append(c);
                i++;
            }
            Flush(text, actions);
            return actions;
        }

        private static void Flush(StringBuilder text, List<OutAction> actions)
        {
            if (text.Length == 0) return;
            actions.Add(new OutAction { Kind = ActionKind.Text, Text = text.ToString() });
            text.Length = 0;
        }

        private static bool TryToken(string token, DateTime now, Func<string> clip, StringBuilder text, List<OutAction> actions)
        {
            string name = token.Trim();
            string arg = null;
            int colon = name.IndexOf(':');
            if (colon > 0)
            {
                arg = name.Substring(colon + 1);
                name = name.Substring(0, colon).Trim();
            }
            string lname = name.ToLowerInvariant();

            if (lname == "date" || lname == "time")
            {
                string fmt = string.IsNullOrEmpty(arg) ? (lname == "date" ? "yyyy/MM/dd" : "HH:mm") : arg;
                try
                {
                    text.Append(now.ToString(fmt, CultureInfo.InvariantCulture));
                }
                catch (FormatException)
                {
                    return false;
                }
                return true;
            }
            if (lname == "rocdate")
            {
                // 民國日期：{rocdate} → 115/10/22；{rocdate:yyy.MM.dd} 其中 yyy 代表民國年
                string roc = (now.Year - 1911).ToString(CultureInfo.InvariantCulture);
                string fmt = string.IsNullOrEmpty(arg) ? "yyy/MM/dd" : arg;
                try
                {
                    text.Append(now.ToString(fmt.Replace("yyy", "'" + roc + "'"), CultureInfo.InvariantCulture));
                }
                catch (FormatException)
                {
                    return false;
                }
                return true;
            }
            if (lname == "clipboard" && arg == null)
            {
                var t = clip != null ? clip() : null;
                // 換行留在文字裡，由 InputSender 轉成 Enter
                if (!string.IsNullOrEmpty(t)) text.Append(t.Replace("\r\n", "\n").Replace('\r', '\n'));
                return true;
            }
            if (lname == "cursor" && arg == null)
            {
                Flush(text, actions);
                actions.Add(new OutAction { Kind = ActionKind.Cursor });
                return true;
            }
            if (lname == "wait" || lname == "sleep")
            {
                int ms;
                if (arg == null || !int.TryParse(arg.Trim(), out ms) || ms < 0) return false;
                Flush(text, actions);
                actions.Add(new OutAction { Kind = ActionKind.Wait, Ms = Math.Min(ms, 10000) });
                return true;
            }
            if (arg != null) return false;

            // 按鍵：可含修飾鍵與重複次數，如 "Ctrl+Shift+V"、"Tab 3"
            int repeat = 1;
            string keyPart = name;
            int sp = keyPart.LastIndexOf(' ');
            if (sp > 0)
            {
                int r;
                if (int.TryParse(keyPart.Substring(sp + 1), out r) && r > 0 && r <= 100)
                {
                    repeat = r;
                    keyPart = keyPart.Substring(0, sp).Trim();
                }
            }
            var act = new OutAction { Kind = ActionKind.Key, Repeat = repeat };
            var parts = keyPart.Split('+');
            for (int p = 0; p < parts.Length - 1; p++)
            {
                var m = parts[p].Trim().ToLowerInvariant();
                if (m == "ctrl" || m == "control") act.Ctrl = true;
                else if (m == "shift") act.Shift = true;
                else if (m == "alt") act.Alt = true;
                else if (m == "win") act.Win = true;
                else return false;
            }
            var key = parts[parts.Length - 1].Trim();
            ushort vk;
            if (KeyNames.TryGetValue(key, out vk)) act.Vk = vk;
            else if (key.Length == 1 && parts.Length > 1 && char.IsLetterOrDigit(key[0]) && key[0] < 128)
                act.Vk = (ushort)char.ToUpperInvariant(key[0]);
            else return false;

            Flush(text, actions);
            actions.Add(act);
            return true;
        }

        // 給設定視窗預覽用：把動作轉回人看得懂的文字。
        public static string Preview(List<OutAction> actions)
        {
            var sb = new StringBuilder();
            foreach (var a in actions)
            {
                switch (a.Kind)
                {
                    case ActionKind.Text:
                        sb.Append(a.Text);
                        break;
                    case ActionKind.Cursor:
                        sb.Append('│');
                        break;
                    case ActionKind.Wait:
                        break;
                    case ActionKind.Key:
                        for (int r = 0; r < a.Repeat; r++)
                        {
                            if (a.Vk == 0x0D && !a.Ctrl && !a.Alt && !a.Shift && !a.Win) sb.Append("\r\n");
                            else sb.Append('[').Append(KeyLabel(a)).Append(']');
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private static string KeyLabel(OutAction a)
        {
            var sb = new StringBuilder();
            if (a.Ctrl) sb.Append("Ctrl+");
            if (a.Shift) sb.Append("Shift+");
            if (a.Alt) sb.Append("Alt+");
            if (a.Win) sb.Append("Win+");
            string name = null;
            foreach (var kv in KeyNames)
            {
                if (kv.Value == a.Vk)
                {
                    name = kv.Key;
                    break;
                }
            }
            sb.Append(name ?? ((char)a.Vk).ToString());
            return sb.ToString();
        }
    }
}
