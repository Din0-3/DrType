using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DrType
{
    public enum OutputMode
    {
        Unicode = 0,      // 直接輸入 Unicode 字元（預設，最不受輸入法影響）
        VirtualKey = 1,   // 模擬實體按鍵（相容某些舊程式）
        PasteCtrlV = 2,   // 剪貼簿 + Ctrl+V
        PasteCtrlShiftV = 3 // 剪貼簿 + Ctrl+Shift+V（純文字貼上）
    }

    public enum SyncMode
    {
        Merge = 0,
        Replace = 1
    }

    // 所有設定，存成 DrType.ini（UTF-8，key=value）。
    public class AppSettings
    {
        public bool AutocompleteEnabled = true;
        public bool HotstringsEnabled = true;
        public string ToggleHotkey = "Alt+Q";
        public int MinChars = 2;
        public int MaxResults = 10;
        public bool AcceptWithTab = true;
        public bool AcceptWithEnter = true;
        public bool FuzzyMatch = true;
        public bool CorrectCase = true;
        public bool AddSpaceAfter = false;
        public OutputMode Output = OutputMode.Unicode;
        public bool PauseInChineseIme = true;
        public bool ShiftSpaceAsSpace = false;
        public string ExcludedApps = "";
        public int PopupFontSize = 11;

        public string WordsSource = "";
        public string HotstringsSource = "";
        public SyncMode SyncMode = SyncMode.Merge;
        public bool SyncOnStartup = false;

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }

        public static AppSettings Load(string path, string legacyIniPath)
        {
            var s = new AppSettings();
            if (!File.Exists(path))
            {
                s.ImportLegacy(legacyIniPath);
                return s;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            s.AutocompleteEnabled = GetBool(map, "AutocompleteEnabled", s.AutocompleteEnabled);
            s.HotstringsEnabled = GetBool(map, "HotstringsEnabled", s.HotstringsEnabled);
            s.ToggleHotkey = GetStr(map, "ToggleHotkey", s.ToggleHotkey);
            s.MinChars = Clamp(GetInt(map, "MinChars", s.MinChars), 1, 10);
            s.MaxResults = Clamp(GetInt(map, "MaxResults", s.MaxResults), 1, 20);
            s.AcceptWithTab = GetBool(map, "AcceptWithTab", s.AcceptWithTab);
            s.AcceptWithEnter = GetBool(map, "AcceptWithEnter", s.AcceptWithEnter);
            s.FuzzyMatch = GetBool(map, "FuzzyMatch", s.FuzzyMatch);
            s.CorrectCase = GetBool(map, "CorrectCase", s.CorrectCase);
            s.AddSpaceAfter = GetBool(map, "AddSpaceAfter", s.AddSpaceAfter);
            s.Output = (OutputMode)Clamp(GetInt(map, "OutputMode", (int)s.Output), 0, 3);
            s.PauseInChineseIme = GetBool(map, "PauseInChineseIme", s.PauseInChineseIme);
            s.ShiftSpaceAsSpace = GetBool(map, "ShiftSpaceAsSpace", s.ShiftSpaceAsSpace);
            s.ExcludedApps = GetStr(map, "ExcludedApps", s.ExcludedApps);
            s.PopupFontSize = Clamp(GetInt(map, "PopupFontSize", s.PopupFontSize), 8, 24);
            s.WordsSource = GetStr(map, "WordsSource", s.WordsSource);
            s.HotstringsSource = GetStr(map, "HotstringsSource", s.HotstringsSource);
            s.SyncMode = (SyncMode)Clamp(GetInt(map, "SyncMode", (int)s.SyncMode), 0, 1);
            s.SyncOnStartup = GetBool(map, "SyncOnStartup", s.SyncOnStartup);
            return s;
        }

        // 舊版 AHK 的 Settings.ini（UTF-16）只沿用三個數值。
        private void ImportLegacy(string legacyIniPath)
        {
            if (string.IsNullOrEmpty(legacyIniPath) || !File.Exists(legacyIniPath)) return;
            try
            {
                foreach (var raw in File.ReadAllLines(legacyIniPath))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    int v;
                    if (!int.TryParse(line.Substring(eq + 1).Trim(), out v)) continue;
                    if (key == "MaxResults") MaxResults = Clamp(v, 1, 20);
                    else if (key == "ShowLength") MinChars = Clamp(v, 1, 10);
                    else if (key == "CorrectCase") CorrectCase = v != 0;
                }
            }
            catch (Exception) { }
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; DrType 設定檔，建議從程式的「設定」視窗修改");
            Put(sb, "AutocompleteEnabled", AutocompleteEnabled);
            Put(sb, "HotstringsEnabled", HotstringsEnabled);
            Put(sb, "ToggleHotkey", ToggleHotkey);
            Put(sb, "MinChars", MinChars);
            Put(sb, "MaxResults", MaxResults);
            Put(sb, "AcceptWithTab", AcceptWithTab);
            Put(sb, "AcceptWithEnter", AcceptWithEnter);
            Put(sb, "FuzzyMatch", FuzzyMatch);
            Put(sb, "CorrectCase", CorrectCase);
            Put(sb, "AddSpaceAfter", AddSpaceAfter);
            Put(sb, "OutputMode", (int)Output);
            Put(sb, "PauseInChineseIme", PauseInChineseIme);
            Put(sb, "ShiftSpaceAsSpace", ShiftSpaceAsSpace);
            Put(sb, "ExcludedApps", ExcludedApps);
            Put(sb, "PopupFontSize", PopupFontSize);
            Put(sb, "WordsSource", WordsSource);
            Put(sb, "HotstringsSource", HotstringsSource);
            Put(sb, "SyncMode", (int)SyncMode);
            Put(sb, "SyncOnStartup", SyncOnStartup);
            FileUtil.WriteAllTextAtomic(path, sb.ToString());
        }

        private static void Put(StringBuilder sb, string key, object value)
        {
            string v;
            if (value is bool) v = (bool)value ? "1" : "0";
            else v = Convert.ToString(value, CultureInfo.InvariantCulture);
            sb.Append(key).Append('=').Append(v).AppendLine();
        }

        private static string GetStr(Dictionary<string, string> m, string k, string d)
        {
            string v;
            return m.TryGetValue(k, out v) ? v : d;
        }

        private static bool GetBool(Dictionary<string, string> m, string k, bool d)
        {
            string v;
            if (!m.TryGetValue(k, out v)) return d;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetInt(Dictionary<string, string> m, string k, int d)
        {
            string v;
            int r;
            if (m.TryGetValue(k, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return d;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    public static class FileUtil
    {
        public static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        // 先寫暫存檔再取代，避免當機或斷電時把檔案寫壞。
        public static void WriteAllTextAtomic(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Utf8Bom);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                    return;
                }
                catch (Exception)
                {
                    File.Delete(path);
                }
            }
            File.Move(tmp, path);
        }

        // 舊詞庫可能是 Big5（系統 ANSI），新檔一律 UTF-8。
        public static string ReadTextAutoDetect(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        public static string ReadTextAutoDetect(string path)
        {
            return ReadTextAutoDetect(File.ReadAllBytes(path));
        }
    }
}
