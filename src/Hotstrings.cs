using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DrType
{
    public class Hotstring
    {
        public string Abbr = "";
        public string Content = "";
        public string Description = "";
        public bool Immediate = true; // true：打完縮寫立即展開；false：縮寫後按空白/Enter/Tab/標點才展開

        public Hotstring Clone()
        {
            return (Hotstring)MemberwiseClone();
        }
    }

    // 片語存成 CSV（UTF-8 BOM），可以直接用 Excel 開啟編輯，也方便放在 Google 試算表同步。
    public static class HotstringFile
    {
        public const string TriggerImmediate = "立即";
        public const string TriggerOnEnd = "空白後";

        public static List<Hotstring> Load(string path)
        {
            if (!File.Exists(path)) return null;
            return Parse(FileUtil.ReadTextAutoDetect(path));
        }

        public static List<Hotstring> Parse(string text)
        {
            var rows = Csv.Parse(text);
            var list = new List<Hotstring>();
            if (rows.Count == 0) return list;

            int cAbbr = 0, cContent = 1, cDesc = 2, cTrig = 3;
            int start = 0;
            var head = rows[0];
            int hA = FindCol(head, "縮寫", "abbr", "abbreviation", "trigger", "key", "快捷");
            int hC = FindCol(head, "內容", "content", "text", "replacement", "expansion");
            if (hA >= 0 && hC >= 0)
            {
                cAbbr = hA;
                cContent = hC;
                cDesc = FindCol(head, "說明", "description", "desc", "note", "備註");
                cTrig = FindCol(head, "觸發", "mode", "觸發方式");
                start = 1;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = start; i < rows.Count; i++)
            {
                var r = rows[i];
                var abbr = Cell(r, cAbbr).Trim();
                if (abbr.Length == 0 || abbr.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!seen.Add(abbr)) continue;
                var trig = Cell(r, cTrig).Trim();
                list.Add(new Hotstring
                {
                    Abbr = abbr,
                    Content = Cell(r, cContent).Replace("\r\n", "\n"),
                    Description = Cell(r, cDesc).Trim(),
                    Immediate = !(trig == TriggerOnEnd || trig.Equals("end", StringComparison.OrdinalIgnoreCase) || trig == "空白")
                });
            }
            return list;
        }

        public static string Serialize(IList<Hotstring> items)
        {
            var rows = new List<string[]>();
            rows.Add(new[] { "縮寫", "內容", "說明", "觸發" });
            foreach (var h in items)
                rows.Add(new[] { h.Abbr, h.Content.Replace("\n", "\r\n"), h.Description, h.Immediate ? TriggerImmediate : TriggerOnEnd });
            return Csv.Write(rows);
        }

        public static void Save(string path, IList<Hotstring> items)
        {
            FileUtil.WriteAllTextAtomic(path, Serialize(items));
        }

        private static int FindCol(string[] head, params string[] names)
        {
            for (int i = 0; i < head.Length; i++)
            {
                var h = head[i].Trim();
                foreach (var n in names)
                    if (h.Equals(n, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static string Cell(string[] r, int i)
        {
            return i >= 0 && i < r.Length ? r[i] : "";
        }

        public static List<Hotstring> Defaults()
        {
            var l = new List<Hotstring>();
            l.Add(new Hotstring { Abbr = "jjdt", Content = "{date:yyyy/MM/dd}", Description = "今天日期 (年/月/日)" });
            l.Add(new Hotstring { Abbr = "jjdd", Content = "{date:MM/dd}", Description = "今天日期 (月/日)" });
            l.Add(new Hotstring { Abbr = "jjtm", Content = "{time:HH:mm}", Description = "現在時間" });
            return l;
        }
    }

    public static class Csv
    {
        // RFC 4180：雙引號包住的欄位可含逗號、換行；"" 代表一個雙引號。
        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false, any = false;
            int i = 0;
            if (text.Length > 0 && text[0] == '﻿') i = 1;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else quoted = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                if (c == '"')
                {
                    quoted = true;
                    any = true;
                }
                else if (c == ',')
                {
                    row.Add(cell.ToString());
                    cell.Length = 0;
                    any = true;
                }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (any || cell.Length > 0)
                    {
                        row.Add(cell.ToString());
                        rows.Add(row.ToArray());
                    }
                    row.Clear();
                    cell.Length = 0;
                    any = false;
                }
                else
                {
                    cell.Append(c);
                    any = true;
                }
            }
            if (any || cell.Length > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row.ToArray());
            }
            return rows;
        }

        public static string Write(IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                for (int i = 0; i < r.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var v = r[i] ?? "";
                    if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v.StartsWith(" ") || v.EndsWith(" "))
                        sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                    else
                        sb.Append(v);
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
