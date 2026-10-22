using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace DrType
{
    // 所有檔案都放在程式旁邊（免安裝、可整個資料夾帶著走）；
    // 若資料夾不可寫入（例如放在 Program Files），改放 %APPDATA%\DrType。
    public sealed class AppData
    {
        public readonly string Dir;
        public readonly string WordsPath;
        public readonly string HotstringsPath;
        public readonly string SettingsPath;
        public readonly string BackupDir;

        public AppSettings Settings;
        public List<string> Words;
        public List<Hotstring> Hotstrings;

        public AppData()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Dir = IsWritable(exeDir) ? exeDir : Path.Combine(appData, "DrType");
            // 改名前（TypeAhead）的資料沿用過來
            string oldAppDataDir = Path.Combine(appData, "TypeAhead");
            if (Dir != exeDir && !Directory.Exists(Dir) && Directory.Exists(oldAppDataDir))
            {
                try { Directory.Move(oldAppDataDir, Dir); }
                catch (Exception) { }
            }
            Directory.CreateDirectory(Dir);
            WordsPath = Path.Combine(Dir, "WordList.txt");
            HotstringsPath = Path.Combine(Dir, "Hotstrings.csv");
            SettingsPath = Path.Combine(Dir, "DrType.ini");
            BackupDir = Path.Combine(Dir, "backup");
            string oldIni = Path.Combine(Dir, "TypeAhead.ini");
            if (!File.Exists(SettingsPath) && File.Exists(oldIni))
            {
                try { File.Move(oldIni, SettingsPath); }
                catch (Exception) { }
            }

            Settings = AppSettings.Load(SettingsPath, Path.Combine(Dir, "Settings.ini"));
            Words = WordListFile.Load(WordsPath);
            Hotstrings = HotstringFile.Load(HotstringsPath);
            if (Hotstrings == null)
            {
                Hotstrings = HotstringFile.Defaults();
                SaveHotstrings();
            }
            if (!File.Exists(SettingsPath)) SaveSettings();
        }

        private static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void SaveWords()
        {
            WordListFile.Save(WordsPath, Words);
        }

        public void SaveHotstrings()
        {
            HotstringFile.Save(HotstringsPath, Hotstrings);
        }

        public void SaveSettings()
        {
            Settings.Save(SettingsPath);
        }

        public void Backup(string path)
        {
            if (!File.Exists(path)) return;
            Directory.CreateDirectory(BackupDir);
            string name = Path.GetFileNameWithoutExtension(path) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + Path.GetExtension(path);
            File.Copy(path, Path.Combine(BackupDir, name), true);
            // 只保留最近 30 份
            var files = new List<string>(Directory.GetFiles(BackupDir, Path.GetFileNameWithoutExtension(path) + "_*"));
            files.Sort(StringComparer.Ordinal);
            for (int i = 0; i < files.Count - 30; i++)
            {
                try { File.Delete(files[i]); }
                catch (Exception) { }
            }
        }
    }

    // 從網址或共用資料夾抓詞庫/片語。支援 Google 試算表、Google 文件、Dropbox、一般 .txt/.csv 網址與檔案路徑。
    public static class SyncService
    {
        public sealed class Result
        {
            public List<string> Words;
            public List<Hotstring> Hotstrings;
            public string Message;
            public bool Ok;
        }

        public static string NormalizeUrl(string src)
        {
            src = (src ?? "").Trim().Trim('"');
            var m = Regex.Match(src, @"docs\.google\.com/spreadsheets/d/(e/)?([\w-]+)");
            if (m.Success)
            {
                if (m.Groups[1].Success)
                {
                    // 「發佈到網路」的連結：/d/e/xxx/pubhtml → pub?output=csv
                    string gidPub = Regex.Match(src, @"[?&#]gid=(\d+)").Groups[1].Value;
                    return "https://docs.google.com/spreadsheets/d/e/" + m.Groups[2].Value + "/pub?output=csv" + (gidPub.Length > 0 ? "&gid=" + gidPub : "");
                }
                string gid = Regex.Match(src, @"[?&#]gid=(\d+)").Groups[1].Value;
                return "https://docs.google.com/spreadsheets/d/" + m.Groups[2].Value + "/export?format=csv" + (gid.Length > 0 ? "&gid=" + gid : "");
            }
            m = Regex.Match(src, @"docs\.google\.com/document/d/([\w-]+)");
            if (m.Success) return "https://docs.google.com/document/d/" + m.Groups[1].Value + "/export?format=txt";
            if (src.Contains("dropbox.com/")) return Regex.Replace(src, @"([?&])dl=0", "$1dl=1");
            return src;
        }

        public static bool LooksLikeCsv(string src)
        {
            string n = NormalizeUrl(src).ToLowerInvariant();
            return n.Contains("format=csv") || n.Contains("output=csv") || n.EndsWith(".csv") || n.Contains(".csv?");
        }

        public static string Fetch(string src)
        {
            string url = NormalizeUrl(src);
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 20000;
                req.UserAgent = "DrType";
                req.AllowAutoRedirect = true;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    string text = FileUtil.ReadTextAutoDetect(ms.ToArray());
                    if (text.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("取得的是網頁而不是資料，請確認連結已設為「知道連結的任何人都能檢視」。");
                    return text;
                }
            }
            if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) url = new Uri(url).LocalPath;
            return FileUtil.ReadTextAutoDetect(url);
        }

        public static List<string> ParseWords(string src, string text)
        {
            if (!LooksLikeCsv(src)) return WordListFile.Parse(text);
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rows = Csv.Parse(text);
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Length == 0) continue;
                string w = rows[i][0].Trim();
                if (i == 0 && (w == "詞" || w == "詞彙" || w == "詞庫" || w.Equals("word", StringComparison.OrdinalIgnoreCase) || w.Equals("words", StringComparison.OrdinalIgnoreCase))) continue;
                if (w.Length > 0 && seen.Add(w)) list.Add(w);
            }
            return list;
        }

        // 合併：本機順序不變，來源新增的詞加在後面。取代：完全使用來源。
        public static List<string> MergeWords(List<string> local, List<string> remote, SyncMode mode, out int added)
        {
            if (mode == SyncMode.Replace)
            {
                var set = new HashSet<string>(local, StringComparer.Ordinal);
                added = 0;
                foreach (var w in remote) if (!set.Contains(w)) added++;
                return new List<string>(remote);
            }
            var result = new List<string>(local);
            var seen = new HashSet<string>(local, StringComparer.Ordinal);
            added = 0;
            foreach (var w in remote)
            {
                if (seen.Add(w))
                {
                    result.Add(w);
                    added++;
                }
            }
            return result;
        }

        // 合併：同縮寫以來源為準，本機獨有的保留。
        public static List<Hotstring> MergeHotstrings(List<Hotstring> local, List<Hotstring> remote, SyncMode mode, out int added, out int updated)
        {
            added = updated = 0;
            var remoteMap = new Dictionary<string, Hotstring>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in remote) remoteMap[h.Abbr] = h;
            var localMap = new Dictionary<string, Hotstring>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in local) localMap[h.Abbr] = h;

            foreach (var h in remote)
            {
                Hotstring old;
                if (!localMap.TryGetValue(h.Abbr, out old)) added++;
                else if (old.Content != h.Content || old.Immediate != h.Immediate || old.Description != h.Description) updated++;
            }
            if (mode == SyncMode.Replace) return new List<Hotstring>(remote);

            var result = new List<Hotstring>();
            foreach (var h in local)
            {
                Hotstring r;
                result.Add(remoteMap.TryGetValue(h.Abbr, out r) ? r : h);
            }
            foreach (var h in remote)
                if (!localMap.ContainsKey(h.Abbr)) result.Add(h);
            return result;
        }
    }
}
