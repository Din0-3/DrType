using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DrType
{
    // 不可變的詞庫索引；編輯時整個換新，鍵盤執行緒讀取時不需要上鎖。
    public sealed class WordIndex
    {
        private sealed class Entry
        {
            public string Text;
            public string Lower;
            public int[] WordStarts; // 片語中第 2 個以後單字的起點，例如 "fibrillar pattern" 的 "pattern"
            public int Order;
        }

        private struct Hit
        {
            public Entry E;
            public int Tier;
            public int Extra;
        }

        private readonly Entry[] entries;
        private readonly HashSet<string> wordSet = new HashSet<string>(StringComparer.Ordinal);

        public static readonly WordIndex Empty = new WordIndex(new string[0]);

        public WordIndex(IList<string> words)
        {
            var list = new List<Entry>(words.Count);
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (string.IsNullOrEmpty(w)) continue;
                var starts = new List<int>();
                for (int j = 1; j < w.Length; j++)
                {
                    char prev = w[j - 1];
                    if (char.IsLetterOrDigit(w[j]) && !char.IsLetterOrDigit(prev) && prev != '\'') starts.Add(j);
                }
                list.Add(new Entry { Text = w, Lower = w.ToLowerInvariant(), WordStarts = starts.ToArray(), Order = i });
                wordSet.Add(w);
            }
            entries = list.ToArray();
        }

        public int Count
        {
            get { return entries.Length; }
        }

        // 詞庫裡是否有這個完整的詞（大小寫也要相同：打 djd 不算打完 DJD）
        public bool ContainsExact(string word)
        {
            return wordSet.Contains(word);
        }

        // 排序：開頭相符 > 片語中某個單字開頭相符 > 跳字相符（如 "pn" → "pain"）；
        // 同一級內，多出的字越少越前面，再依詞庫中的順序。
        public List<string> Suggest(string typed, int max, bool fuzzy)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(typed) || max <= 0) return result;
            // 數字開頭（劑量、日期、20%…）不提示；L5S1、D5W 這類字母開頭的照常比對
            if (char.IsDigit(typed[0])) return result;
            string q = typed.ToLowerInvariant();
            var hits = new List<Hit>();
            foreach (var e in entries)
            {
                if (e.Text.Length < q.Length) continue;
                if (e.Text == typed) continue; // 已經打完整了，不必再建議
                if (e.Lower.StartsWith(q, StringComparison.Ordinal))
                {
                    hits.Add(new Hit { E = e, Tier = 3, Extra = e.Text.Length - q.Length });
                    continue;
                }
                bool inner = false;
                foreach (int s in e.WordStarts)
                {
                    if (string.CompareOrdinal(e.Lower, s, q, 0, q.Length) == 0)
                    {
                        inner = true;
                        break;
                    }
                }
                if (inner)
                {
                    hits.Add(new Hit { E = e, Tier = 2, Extra = e.Text.Length - q.Length });
                    continue;
                }
                if (fuzzy && q.Length >= 2 && e.Lower[0] == q[0])
                {
                    int span = SubsequenceSpan(e.Lower, q);
                    if (span > 0) hits.Add(new Hit { E = e, Tier = 1, Extra = span - q.Length });
                }
            }
            hits.Sort(delegate(Hit a, Hit b)
            {
                if (a.Tier != b.Tier) return b.Tier - a.Tier;
                if (a.Extra != b.Extra) return a.Extra - b.Extra;
                return a.E.Order - b.E.Order;
            });
            for (int i = 0; i < hits.Count && result.Count < max; i++) result.Add(hits[i].E.Text);
            return result;
        }

        // q 依序出現在 s 中（從開頭起算）時回傳涵蓋長度，否則 0。
        private static int SubsequenceSpan(string s, string q)
        {
            int qi = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == q[qi])
                {
                    qi++;
                    if (qi == q.Length) return i + 1;
                }
            }
            return 0;
        }

        // 依使用者輸入的大小寫調整建議字：首字大寫 → 建議字首字大寫；全大寫 → 全大寫。
        public static string MatchCase(string typed, string word)
        {
            if (string.IsNullOrEmpty(typed) || string.IsNullOrEmpty(word)) return word;
            int letters = 0, uppers = 0;
            foreach (char c in typed)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (char.IsUpper(c)) uppers++;
            }
            if (letters >= 2 && uppers == letters && word.ToLowerInvariant().StartsWith(typed.ToLowerInvariant(), StringComparison.Ordinal))
                return word.ToUpperInvariant();
            if (char.IsUpper(typed[0]) && char.IsLower(word[0]))
                return char.ToUpperInvariant(word[0]) + word.Substring(1);
            return word;
        }
    }

    public static class WordListFile
    {
        public static List<string> Parse(string text)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var w = raw.Trim();
                if (w.Length == 0 || w.StartsWith("//", StringComparison.Ordinal)) continue;
                if (seen.Add(w)) list.Add(w);
            }
            return list;
        }

        public static List<string> Load(string path)
        {
            if (!File.Exists(path)) return new List<string>();
            return Parse(FileUtil.ReadTextAutoDetect(path));
        }

        public static void Save(string path, IList<string> words)
        {
            var sb = new StringBuilder();
            foreach (var w in words) sb.Append(w).Append("\r\n");
            FileUtil.WriteAllTextAtomic(path, sb.ToString());
        }
    }
}
