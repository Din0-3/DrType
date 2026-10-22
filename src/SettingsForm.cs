using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DrType
{
    public sealed class SettingsForm : Form
    {
        private readonly TrayApp app;
        private readonly AppData data;
        private readonly float scale;
        private readonly TabControl tabs = new TabControl();
        private static readonly Color HintColor = Color.FromArgb(100, 100, 100);

        // 詞庫
        private TextBox wordSearch;
        private ListBox wordList;
        private Label wordCount;
        private readonly List<int> wordView = new List<int>(); // 目前顯示的項目在 data.Words 的位置

        // 片語
        private TextBox hsSearch, hsAbbr, hsDesc, hsContent, hsPreview;
        private ComboBox hsTrigger;
        private ListView hsList;
        private Label hsStatus;
        private Hotstring hsEditing;   // 正在編輯的原始物件（新增時為 null）
        private bool hsDirty, hsLoading;

        // 同步
        private TextBox syncWords, syncHs, syncLog;
        private RadioButton syncMerge, syncReplace;
        private CheckBox syncStartup;

        private bool loadingSettings;

        public SettingsForm(TrayApp app)
        {
            this.app = app;
            data = app.Data;
            Text = "DrType 自動完成 — 詞庫與設定";
            Font = new Font("Microsoft JhengHei UI", 10);
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size(S(860), S(620));
            MinimumSize = new Size(S(700), S(480));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icons.Make(true, 32);
            KeyPreview = true;

            tabs.Dock = DockStyle.Fill;
            tabs.Padding = new Point(S(14), S(5));
            tabs.TabPages.Add(BuildWordsTab());
            tabs.TabPages.Add(BuildHotstringsTab());
            tabs.TabPages.Add(BuildSyncTab());
            tabs.TabPages.Add(BuildSettingsTab());
            Controls.Add(tabs);

            ReloadData();
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (hsDirty && !SaveHotstring(true)) e.Cancel = true;
            };
        }

        private int S(int px)
        {
            return (int)Math.Round(px * scale);
        }

        public void SelectTab(int i)
        {
            if (i >= 0 && i < tabs.TabCount) tabs.SelectedIndex = i;
        }

        public void ReloadData()
        {
            RefreshWords(null);
            RefreshHotstrings(null);
        }

        private void Changed()
        {
            app.ApplyChanges();
        }

        private static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = HintColor, Margin = new Padding(3, 4, 3, 4) };
        }

        private Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(S(110), S(32)), Margin = new Padding(3, 3, 3, 3) };
            b.Click += click;
            return b;
        }

        // ================= 詞庫 =================

        private TabPage BuildWordsTab()
        {
            var page = new TabPage("詞庫（自動完成）") { Padding = new Padding(S(10)) };

            var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            wordSearch = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12), Margin = new Padding(3, 4, 3, 4) };
            wordSearch.TextChanged += delegate { RefreshWords(null); };
            wordSearch.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddWordFromBox();
                }
                else if (e.KeyCode == Keys.Down && wordList.Items.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    wordList.Focus();
                    wordList.ClearSelected();
                    wordList.SelectedIndex = 0;
                }
            };
            top.Controls.Add(wordSearch, 0, 0);
            top.Controls.Add(Btn("加入詞庫", delegate { AddWordFromBox(); }), 1, 0);
            wordCount = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = HintColor, Margin = new Padding(S(8), 0, 0, 0) };
            top.Controls.Add(wordCount, 2, 0);
            var hint = Hint("在上方輸入：可即時搜尋；按 Enter 直接加入詞庫。雙擊項目可修改，Delete 鍵刪除。排越前面的詞，建議時越優先。");
            hint.Dock = DockStyle.Top;

            var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(S(8), 0, 0, 0) };
            side.Controls.Add(Btn("批次加入…", delegate { BatchAddWords(); }));
            side.Controls.Add(Btn("修改…", delegate { EditSelectedWord(); }));
            side.Controls.Add(Btn("刪除", delegate { DeleteSelectedWords(); }));
            side.Controls.Add(Spacer());
            side.Controls.Add(Btn("上移 ↑", delegate { MoveWord(-1); }));
            side.Controls.Add(Btn("下移 ↓", delegate { MoveWord(1); }));
            side.Controls.Add(Btn("依字母排序", delegate { SortWords(); }));
            side.Controls.Add(Spacer());
            side.Controls.Add(Btn("從檔案匯入…", delegate { ImportWords(); }));
            side.Controls.Add(Btn("匯出…", delegate { ExportWords(); }));

            wordList = new ListBox
            {
                Dock = DockStyle.Fill,
                SelectionMode = SelectionMode.MultiExtended,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 11)
            };
            wordList.DoubleClick += delegate { EditSelectedWord(); };
            wordList.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) DeleteSelectedWords();
                else if (e.KeyCode == Keys.F2 || e.KeyCode == Keys.Enter) EditSelectedWord();
            };

            page.Controls.Add(wordList);
            page.Controls.Add(side);
            page.Controls.Add(hint);
            page.Controls.Add(top);
            return page;
        }

        private Control Spacer()
        {
            return new Label { AutoSize = false, Size = new Size(S(10), S(10)) };
        }

        private void RefreshWords(string select)
        {
            string q = wordSearch.Text.Trim().ToLowerInvariant();
            wordList.BeginUpdate();
            wordList.Items.Clear();
            wordView.Clear();
            for (int i = 0; i < data.Words.Count; i++)
            {
                var w = data.Words[i];
                if (q.Length > 0 && w.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                wordView.Add(i);
                wordList.Items.Add(w);
            }
            wordList.EndUpdate();
            if (select != null)
            {
                int idx = wordList.Items.IndexOf(select);
                if (idx >= 0)
                {
                    wordList.ClearSelected();
                    wordList.SelectedIndex = idx;
                }
            }
            wordCount.Text = q.Length > 0 ? "符合 " + wordView.Count + " / 共 " + data.Words.Count + " 個" : "共 " + data.Words.Count + " 個";
        }

        private void SaveWordsAndApply(string select)
        {
            data.SaveWords();
            Changed();
            RefreshWords(select);
        }

        private void AddWordFromBox()
        {
            string w = wordSearch.Text.Trim();
            if (w.Length == 0) return;
            if (data.Words.Contains(w))
            {
                RefreshWords(w);
                return;
            }
            data.Words.Add(w);
            wordSearch.Text = "";
            SaveWordsAndApply(w);
            wordList.TopIndex = Math.Max(0, wordList.Items.Count - 1);
        }

        private void BatchAddWords()
        {
            string text = TextPrompt.Show(this, "批次加入詞彙", "一行一個詞（可直接從 Excel 或 Word 複製貼上）：", "", true);
            if (text == null) return;
            int added = 0;
            foreach (var w in WordListFile.Parse(text))
            {
                if (data.Words.Contains(w)) continue;
                data.Words.Add(w);
                added++;
            }
            SaveWordsAndApply(null);
            MessageBox.Show(this, "已加入 " + added + " 個新詞。", "批次加入");
        }

        private void EditSelectedWord()
        {
            if (wordList.SelectedIndices.Count != 1) return;
            int real = wordView[wordList.SelectedIndex];
            string old = data.Words[real];
            string w = TextPrompt.Show(this, "修改詞彙", "詞彙：", old, false);
            if (w == null) return;
            w = w.Trim();
            if (w.Length == 0 || w == old) return;
            if (data.Words.Contains(w))
            {
                MessageBox.Show(this, "詞庫裡已經有「" + w + "」。", "修改詞彙");
                return;
            }
            data.Words[real] = w;
            SaveWordsAndApply(w);
        }

        private void DeleteSelectedWords()
        {
            int n = wordList.SelectedIndices.Count;
            if (n == 0) return;
            if (n > 1 && MessageBox.Show(this, "確定刪除選取的 " + n + " 個詞？", "刪除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var remove = new List<int>();
            foreach (int i in wordList.SelectedIndices) remove.Add(wordView[i]);
            remove.Sort();
            int firstSel = wordList.SelectedIndices[0];
            for (int i = remove.Count - 1; i >= 0; i--) data.Words.RemoveAt(remove[i]);
            SaveWordsAndApply(null);
            if (wordList.Items.Count > 0) wordList.SelectedIndex = Math.Min(firstSel, wordList.Items.Count - 1);
        }

        private void MoveWord(int dir)
        {
            if (wordSearch.Text.Length > 0)
            {
                MessageBox.Show(this, "請先清空搜尋框再調整順序。", "調整順序");
                return;
            }
            if (wordList.SelectedIndices.Count != 1) return;
            int i = wordList.SelectedIndex, j = i + dir;
            if (j < 0 || j >= data.Words.Count) return;
            var t = data.Words[i];
            data.Words[i] = data.Words[j];
            data.Words[j] = t;
            SaveWordsAndApply(t);
        }

        private void SortWords()
        {
            if (MessageBox.Show(this, "依字母排序會打亂目前的優先順序，確定嗎？", "排序", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            data.Backup(data.WordsPath);
            data.Words.Sort(StringComparer.OrdinalIgnoreCase);
            SaveWordsAndApply(null);
        }

        private void ImportWords()
        {
            using (var dlg = new OpenFileDialog { Filter = "文字或 CSV 檔|*.txt;*.csv|所有檔案|*.*", Title = "匯入詞庫" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var words = SyncService.ParseWords(dlg.FileName, FileUtil.ReadTextAutoDetect(dlg.FileName));
                    int added;
                    data.Words = SyncService.MergeWords(data.Words, words, SyncMode.Merge, out added);
                    SaveWordsAndApply(null);
                    MessageBox.Show(this, "已匯入 " + added + " 個新詞（重複的略過）。", "匯入詞庫");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "匯入失敗：" + ex.Message, "匯入詞庫", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportWords()
        {
            using (var dlg = new SaveFileDialog { Filter = "文字檔|*.txt", FileName = "WordList.txt", Title = "匯出詞庫" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                WordListFile.Save(dlg.FileName, data.Words);
            }
        }

        // ================= 快捷片語 =================

        private TabPage BuildHotstringsTab()
        {
            var page = new TabPage("快捷片語") { Padding = new Padding(S(10)) };
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            page.Controls.Add(split);
            bool splitSet = false;
            split.SizeChanged += delegate
            {
                // 要等容器有實際寬度後才能設定分隔線位置
                if (splitSet || split.Width < S(600)) return;
                splitSet = true;
                split.SplitterDistance = S(280);
            };

            // 左：清單
            hsSearch = new TextBox { Dock = DockStyle.Top };
            hsSearch.TextChanged += delegate { RefreshHotstrings(null); };
            var searchHint = Hint("搜尋縮寫、說明或內容");
            searchHint.Dock = DockStyle.Top;
            hsList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            hsList.Columns.Add("縮寫", S(90));
            hsList.Columns.Add("說明", S(170));
            hsList.SelectedIndexChanged += delegate
            {
                if (hsLoading || hsList.SelectedItems.Count == 0) return;
                var h = (Hotstring)hsList.SelectedItems[0].Tag;
                if (h == hsEditing) return;
                if (hsDirty && !SaveHotstring(true))
                {
                    SelectHotstringInList(hsEditing);
                    return;
                }
                LoadEditor(h);
            };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            leftButtons.Controls.Add(Btn("＋ 新增片語", delegate { NewHotstring(); }));
            leftButtons.Controls.Add(Btn("刪除", delegate { DeleteHotstring(); }));
            split.Panel1.Controls.Add(hsList);
            split.Panel1.Controls.Add(leftButtons);
            split.Panel1.Controls.Add(searchHint);
            split.Panel1.Controls.Add(hsSearch);

            // 右：編輯區
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(S(8), 0, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.Panel2.Controls.Add(grid);

            hsAbbr = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 11) };
            hsDesc = new TextBox { Dock = DockStyle.Fill };
            hsTrigger = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            hsTrigger.Items.Add("打完縮寫立即展開");
            hsTrigger.Items.Add("縮寫後按空白 / Enter / Tab 才展開");
            hsContent = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new Font("Consolas", 11)
            };
            hsPreview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(248, 248, 248) };
            EventHandler dirty = delegate
            {
                if (hsLoading) return;
                hsDirty = true;
                hsStatus.Text = "尚未儲存";
                UpdatePreview();
            };
            hsAbbr.TextChanged += dirty;
            hsDesc.TextChanged += dirty;
            hsContent.TextChanged += dirty;
            hsTrigger.SelectedIndexChanged += dirty;

            var tokens = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            tokens.Controls.Add(MenuButton("日期 ▾", new[]
            {
                "{date:yyyy/MM/dd}|西元 " + DateTime.Now.ToString("yyyy/MM/dd"),
                "{date:MM/dd}|月/日 " + DateTime.Now.ToString("MM/dd"),
                "{date:yyyy-MM-dd}|" + DateTime.Now.ToString("yyyy-MM-dd"),
                "{rocdate}|民國 " + (DateTime.Now.Year - 1911) + DateTime.Now.ToString("/MM/dd"),
                "{date:yyyyMMdd}|" + DateTime.Now.ToString("yyyyMMdd")
            }));
            tokens.Controls.Add(MenuButton("時間 ▾", new[] { "{time:HH:mm}|" + DateTime.Now.ToString("HH:mm"), "{time:HH:mm:ss}|" + DateTime.Now.ToString("HH:mm:ss") }));
            tokens.Controls.Add(TokenButton("Enter", "{Enter}"));
            tokens.Controls.Add(TokenButton("Tab", "{Tab}"));
            tokens.Controls.Add(TokenButton("游標停這裡", "{cursor}"));
            tokens.Controls.Add(TokenButton("剪貼簿", "{clipboard}"));
            tokens.Controls.Add(TokenButton("暫停 0.1 秒", "{wait:100}"));
            tokens.Controls.Add(MenuButton("其他按鍵 ▾", new[]
            {
                "{Ctrl+A}|Ctrl+A 全選", "{Ctrl+C}|Ctrl+C 複製", "{Ctrl+V}|Ctrl+V 貼上", "{Shift+Tab}|Shift+Tab",
                "{Up}|↑", "{Down}|↓", "{Left}|←", "{Right}|→", "{Home}|Home", "{End}|End",
                "{Del}|Delete", "{BS}|Backspace", "{Esc}|Esc", "{Tab 2}|Tab 按兩次"
            }));

            hsStatus = new Label { AutoSize = true, ForeColor = HintColor, Anchor = AnchorStyles.Left };
            var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
            saveRow.Controls.Add(Btn("儲存片語", delegate { SaveHotstring(false); }));
            saveRow.Controls.Add(hsStatus);

            int r = 0;
            AddRow(grid, r++, "縮寫", hsAbbr, SizeType.AutoSize, 0);
            AddRow(grid, r++, "觸發方式", hsTrigger, SizeType.AutoSize, 0);
            AddRow(grid, r++, "說明", hsDesc, SizeType.AutoSize, 0);
            AddRow(grid, r++, "插入", tokens, SizeType.AutoSize, 0);
            AddRow(grid, r++, "內容", hsContent, SizeType.Percent, 60);
            AddRow(grid, r++, "預覽", hsPreview, SizeType.Percent, 40);
            var help = Hint("內容中的換行會送出 Enter。{cursor} 表示展開後游標停的位置。內容要輸出大括號 { 請打 {{");
            help.MaximumSize = new Size(S(520), 0);
            AddRow(grid, r++, "", help, SizeType.AutoSize, 0);
            AddRow(grid, r++, "", saveRow, SizeType.AutoSize, 0);
            grid.RowCount = r;

            SetEditorEnabled(false);
            return page;
        }

        private void AddRow(TableLayoutPanel grid, int row, string label, Control c, SizeType st, float h)
        {
            grid.RowStyles.Add(new RowStyle(st, h));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, S(7), S(6), 3) };
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(c, 1, row);
        }

        private Button TokenButton(string text, string token)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(2), Padding = new Padding(S(4), 0, S(4), 0) };
            b.Click += delegate { InsertToken(token); };
            return b;
        }

        // items 格式："{token}|選單顯示文字"
        private Button MenuButton(string text, string[] items)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(2), Padding = new Padding(S(4), 0, S(4), 0) };
            var menu = new ContextMenuStrip { Font = Font };
            foreach (var it in items)
            {
                var parts = it.Split('|');
                string token = parts[0];
                menu.Items.Add(new ToolStripMenuItem(parts[1] + "    " + token, null, delegate { InsertToken(token); }));
            }
            b.Click += delegate { menu.Show(b, new Point(0, b.Height)); };
            return b;
        }

        private void InsertToken(string token)
        {
            if (!hsContent.Enabled) return;
            hsContent.SelectedText = token;
            hsContent.Focus();
        }

        private void SetEditorEnabled(bool on)
        {
            hsAbbr.Enabled = hsDesc.Enabled = hsContent.Enabled = hsTrigger.Enabled = on;
        }

        private void RefreshHotstrings(Hotstring select)
        {
            string q = hsSearch.Text.Trim().ToLowerInvariant();
            hsLoading = true;
            hsList.BeginUpdate();
            hsList.Items.Clear();
            foreach (var h in data.Hotstrings)
            {
                if (q.Length > 0 && (h.Abbr + "\n" + h.Description + "\n" + h.Content).ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                var item = new ListViewItem(new[] { h.Abbr, h.Description.Length > 0 ? h.Description : FirstLine(h.Content) }) { Tag = h };
                hsList.Items.Add(item);
            }
            hsList.EndUpdate();
            hsLoading = false;
            if (select != null) SelectHotstringInList(select);
            else if (hsEditing == null && !hsDirty && hsList.Items.Count > 0) hsList.Items[0].Selected = true;
            else if (hsEditing != null && !data.Hotstrings.Contains(hsEditing))
            {
                // 同步後原本的物件被換掉了
                hsDirty = false;
                hsEditing = null;
                if (hsList.Items.Count > 0) hsList.Items[0].Selected = true;
            }
            else SelectHotstringInList(hsEditing);
        }

        private void SelectHotstringInList(Hotstring h)
        {
            hsLoading = true;
            foreach (ListViewItem it in hsList.Items)
            {
                bool sel = it.Tag == h;
                it.Selected = sel;
                if (sel) it.EnsureVisible();
            }
            hsLoading = false;
            if (h != null && h != hsEditing && !hsDirty) LoadEditor(h);
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return nl >= 0 ? s.Substring(0, nl) + " …" : s;
        }

        private void LoadEditor(Hotstring h)
        {
            hsLoading = true;
            hsEditing = h;
            SetEditorEnabled(true);
            hsAbbr.Text = h != null ? h.Abbr : "";
            hsDesc.Text = h != null ? h.Description : "";
            hsContent.Text = h != null ? h.Content.Replace("\n", "\r\n") : "";
            hsTrigger.SelectedIndex = h == null || h.Immediate ? 0 : 1;
            hsLoading = false;
            hsDirty = false;
            hsStatus.Text = h == null ? "新片語，填好後按「儲存片語」" : "";
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            var actions = Expansion.Parse(hsContent.Text, DateTime.Now, delegate { return "(剪貼簿內容)"; });
            hsPreview.Text = Expansion.Preview(actions);
        }

        private void NewHotstring()
        {
            if (hsDirty && !SaveHotstring(true)) return;
            hsLoading = true;
            foreach (ListViewItem it in hsList.Items) it.Selected = false;
            hsLoading = false;
            LoadEditor(null);
            hsDirty = true;
            hsAbbr.Focus();
        }

        // 回傳 false 代表內容有問題、使用者選擇留在原地
        private bool SaveHotstring(bool askFirst)
        {
            if (!hsDirty) return true;
            if (askFirst)
            {
                var r = MessageBox.Show(this, "片語「" + hsAbbr.Text + "」尚未儲存，要儲存嗎？", "快捷片語", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return false;
                if (r == DialogResult.No)
                {
                    hsDirty = false;
                    return true;
                }
            }
            string abbr = hsAbbr.Text.Trim();
            if (abbr.Length == 0)
            {
                MessageBox.Show(this, "請輸入縮寫。", "快捷片語");
                hsAbbr.Focus();
                return false;
            }
            if (abbr.IndexOf(' ') >= 0)
            {
                MessageBox.Show(this, "縮寫不能有空白。", "快捷片語");
                hsAbbr.Focus();
                return false;
            }
            foreach (var other in data.Hotstrings)
            {
                if (other != hsEditing && other.Abbr.Equals(abbr, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "縮寫「" + abbr + "」已經存在。", "快捷片語");
                    hsAbbr.Focus();
                    return false;
                }
            }
            var h = hsEditing ?? new Hotstring();
            h.Abbr = abbr;
            h.Description = hsDesc.Text.Trim();
            h.Content = hsContent.Text.Replace("\r\n", "\n");
            h.Immediate = hsTrigger.SelectedIndex != 1;
            if (hsEditing == null) data.Hotstrings.Add(h);
            hsEditing = h;
            hsDirty = false;
            data.SaveHotstrings();
            Changed();
            RefreshHotstrings(h);
            hsStatus.Text = "已儲存 ✓";
            return true;
        }

        private void DeleteHotstring()
        {
            if (hsEditing == null)
            {
                hsDirty = false;
                LoadEditor(null);
                SetEditorEnabled(false);
                return;
            }
            if (MessageBox.Show(this, "確定刪除片語「" + hsEditing.Abbr + "」？", "刪除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            data.Hotstrings.Remove(hsEditing);
            data.SaveHotstrings();
            Changed();
            hsDirty = false;
            hsEditing = null;
            RefreshHotstrings(null);
            if (hsList.Items.Count == 0)
            {
                LoadEditor(null);
                hsDirty = false;
                SetEditorEnabled(false);
            }
        }

        // ================= 同步 / 匯入匯出 =================

        private TabPage BuildSyncTab()
        {
            var page = new TabPage("同步與分享") { Padding = new Padding(S(10)), AutoScroll = true };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            page.Controls.Add(grid);

            var intro = Hint("把詞庫或片語放在 Google 試算表、雲端硬碟或診所共用資料夾，所有電腦都能一鍵同步成同一份。\r\n" +
                             "• Google 試算表：右上「共用」→ 設成「知道連結的任何人」可檢視，直接貼上網址即可（A 欄放詞彙）。\r\n" +
                             "• 片語試算表欄位：縮寫｜內容｜說明｜觸發（觸發填「立即」或「空白後」，可留空）。\r\n" +
                             "• 也可以填網址（.txt / .csv）或檔案路徑，例如 \\\\server\\共用\\WordList.txt");
            intro.MaximumSize = new Size(S(780), 0);
            grid.Controls.Add(intro, 0, 0);
            grid.SetColumnSpan(intro, 3);

            syncWords = new TextBox { Dock = DockStyle.Fill };
            syncHs = new TextBox { Dock = DockStyle.Fill };
            grid.Controls.Add(new Label { Text = "詞庫來源", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            grid.Controls.Add(syncWords, 1, 1);
            grid.Controls.Add(Btn("選擇檔案…", delegate { BrowseInto(syncWords); }), 2, 1);
            grid.Controls.Add(new Label { Text = "片語來源", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            grid.Controls.Add(syncHs, 1, 2);
            grid.Controls.Add(Btn("選擇檔案…", delegate { BrowseInto(syncHs); }), 2, 2);

            var modes = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
            syncMerge = new RadioButton { Text = "合併：加入來源的新項目，保留我自己加的（建議）", AutoSize = true };
            syncReplace = new RadioButton { Text = "取代：完全以來源為準（會先自動備份到 backup 資料夾）", AutoSize = true };
            syncStartup = new CheckBox { Text = "每次啟動程式時自動同步", AutoSize = true };
            modes.Controls.Add(syncMerge);
            modes.Controls.Add(syncReplace);
            modes.Controls.Add(syncStartup);
            grid.Controls.Add(new Label { Text = "同步方式", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, S(6), 3, 3) }, 0, 3);
            grid.Controls.Add(modes, 1, 3);
            grid.SetColumnSpan(modes, 2);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            actions.Controls.Add(Btn("立即同步", delegate
            {
                SaveSyncSettings();
                app.SyncAsync(delegate(string msg) { syncLog.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + msg; });
            }));
            grid.Controls.Add(actions, 1, 4);
            grid.SetColumnSpan(actions, 2);

            syncLog = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Height = S(70), BackColor = Color.FromArgb(248, 248, 248) };
            grid.Controls.Add(syncLog, 1, 5);
            grid.SetColumnSpan(syncLog, 2);

            var shareTitle = new Label { Text = "分享給同事", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, S(16), 3, 3) };
            grid.Controls.Add(shareTitle, 0, 6);
            grid.SetColumnSpan(shareTitle, 3);
            var share = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            share.Controls.Add(Btn("匯出詞庫 (.txt)…", delegate { ExportWords(); }));
            share.Controls.Add(Btn("匯出片語 (.csv)…", delegate { ExportHotstrings(); }));
            share.Controls.Add(Btn("匯入片語檔…", delegate { ImportHotstrings(); }));
            share.Controls.Add(Btn("開啟資料夾", delegate { Process.Start("explorer.exe", "\"" + data.Dir + "\""); }));
            grid.Controls.Add(share, 0, 7);
            grid.SetColumnSpan(share, 3);
            var shareHint = Hint("最簡單的分享方式：把整個 DrType 資料夾（含 DrType.exe、WordList.txt、Hotstrings.csv）複製給對方即可。\r\nHotstrings.csv 可以直接用 Excel 開啟編輯。");
            shareHint.MaximumSize = new Size(S(780), 0);
            grid.Controls.Add(shareHint, 0, 8);
            grid.SetColumnSpan(shareHint, 3);

            syncWords.Text = data.Settings.WordsSource;
            syncHs.Text = data.Settings.HotstringsSource;
            syncMerge.Checked = data.Settings.SyncMode == SyncMode.Merge;
            syncReplace.Checked = data.Settings.SyncMode == SyncMode.Replace;
            syncStartup.Checked = data.Settings.SyncOnStartup;
            syncWords.Leave += delegate { SaveSyncSettings(); };
            syncHs.Leave += delegate { SaveSyncSettings(); };
            syncMerge.CheckedChanged += delegate { SaveSyncSettings(); };
            syncStartup.CheckedChanged += delegate { SaveSyncSettings(); };
            return page;
        }

        private void BrowseInto(TextBox box)
        {
            using (var dlg = new OpenFileDialog { Filter = "文字或 CSV 檔|*.txt;*.csv|所有檔案|*.*" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    box.Text = dlg.FileName;
                    SaveSyncSettings();
                }
            }
        }

        private void SaveSyncSettings()
        {
            var s = data.Settings;
            s.WordsSource = syncWords.Text.Trim();
            s.HotstringsSource = syncHs.Text.Trim();
            s.SyncMode = syncReplace.Checked ? SyncMode.Replace : SyncMode.Merge;
            s.SyncOnStartup = syncStartup.Checked;
            data.SaveSettings();
        }

        private void ExportHotstrings()
        {
            using (var dlg = new SaveFileDialog { Filter = "CSV 檔（可用 Excel 開啟）|*.csv", FileName = "Hotstrings.csv", Title = "匯出片語" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                HotstringFile.Save(dlg.FileName, data.Hotstrings);
            }
        }

        private void ImportHotstrings()
        {
            using (var dlg = new OpenFileDialog { Filter = "CSV 檔|*.csv|所有檔案|*.*", Title = "匯入片語" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var list = HotstringFile.Load(dlg.FileName) ?? new List<Hotstring>();
                    int added, updated;
                    data.Backup(data.HotstringsPath);
                    data.Hotstrings = SyncService.MergeHotstrings(data.Hotstrings, list, SyncMode.Merge, out added, out updated);
                    data.SaveHotstrings();
                    Changed();
                    hsDirty = false;
                    hsEditing = null;
                    RefreshHotstrings(null);
                    MessageBox.Show(this, "新增 " + added + " 個、更新 " + updated + " 個片語。", "匯入片語");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "匯入失敗：" + ex.Message, "匯入片語", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ================= 設定 =================

        private TabPage BuildSettingsTab()
        {
            var page = new TabPage("設定") { Padding = new Padding(S(10)), AutoScroll = true };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(grid);
            var s = data.Settings;
            loadingSettings = true;
            int r = 0;

            var acOn = Check("啟用自動完成（打字時跳出建議）", s.AutocompleteEnabled, delegate(bool v) { s.AutocompleteEnabled = v; });
            var hsOn = Check("啟用快捷片語", s.HotstringsEnabled, delegate(bool v) { s.HotstringsEnabled = v; });
            Row(grid, ref r, "功能", Stack(acOn, hsOn));

            var hkBox = new TextBox { ReadOnly = true, Width = S(180), Text = s.ToggleHotkey, BackColor = SystemColors.Window, Cursor = Cursors.Hand };
            hkBox.Enter += delegate { app.SuspendHotkey(); hkBox.BackColor = Color.FromArgb(255, 250, 220); };
            hkBox.Leave += delegate { hkBox.BackColor = SystemColors.Window; app.RegisterHotkey(false); };
            hkBox.KeyDown += delegate(object o, KeyEventArgs e)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete)
                {
                    hkBox.Text = "";
                }
                else
                {
                    string t = HotkeyText.Format(e.KeyData);
                    if (t == null) return;
                    bool hasMod = (e.KeyData & (Keys.Control | Keys.Alt)) != 0;
                    bool isF = e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F24;
                    if (!hasMod && !isF) return; // 單一字母會干擾打字，不允許
                    hkBox.Text = t;
                }
                s.ToggleHotkey = hkBox.Text;
                data.SaveSettings();
            };
            hkBox.PreviewKeyDown += delegate(object o, PreviewKeyDownEventArgs e) { e.IsInputKey = true; };
            var hkTest = Btn("套用", delegate
            {
                if (app.RegisterHotkey(true)) MessageBox.Show(this, s.ToggleHotkey.Length > 0 ? "快捷鍵 " + s.ToggleHotkey + " 已生效。" : "已取消快捷鍵。", "DrType");
            });
            var hkRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            hkRow.Controls.Add(hkBox);
            hkRow.Controls.Add(hkTest);
            hkRow.Controls.Add(Hint("點一下框框再按想要的組合鍵；Backspace 清除"));
            Row(grid, ref r, "開關快捷鍵", hkRow);

            var minChars = Num(1, 6, s.MinChars, delegate(int v) { s.MinChars = v; });
            Row(grid, ref r, "打幾個字後提示", Inline(minChars, Hint("個字母")));
            var maxRes = Num(1, 20, s.MaxResults, delegate(int v) { s.MaxResults = v; });
            Row(grid, ref r, "最多顯示", Inline(maxRes, Hint("個建議")));
            var font = Num(8, 20, s.PopupFontSize, delegate(int v) { s.PopupFontSize = v; });
            Row(grid, ref r, "建議清單字體", Inline(font, Hint("點")));

            var tab = Check("Tab", s.AcceptWithTab, delegate(bool v) { s.AcceptWithTab = v; });
            var enter = Check("Enter", s.AcceptWithEnter, delegate(bool v) { s.AcceptWithEnter = v; });
            Row(grid, ref r, "選取建議的按鍵", Inline(tab, enter, Hint("（也可用 Alt+1~9 或滑鼠點選）")));

            var fuzzy = Check("允許跳字比對（打 pn 也能找到 pain）", s.FuzzyMatch, delegate(bool v) { s.FuzzyMatch = v; });
            var cc = Check("依照輸入的大小寫調整（打 Pa → Pain）", s.CorrectCase, delegate(bool v) { s.CorrectCase = v; });
            var sp = Check("選取建議後自動補一個空白", s.AddSpaceAfter, delegate(bool v) { s.AddSpaceAfter = v; });
            Row(grid, ref r, "比對", Stack(fuzzy, cc, sp));

            var output = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(420) };
            output.Items.Add("直接輸入文字（建議，不動剪貼簿）");
            output.Items.Add("模擬鍵盤按鍵（若某些舊程式收不到字再試）");
            output.Items.Add("剪貼簿貼上 Ctrl+V");
            output.Items.Add("剪貼簿貼上 Ctrl+Shift+V（純文字）");
            output.SelectedIndex = (int)s.Output;
            output.SelectedIndexChanged += delegate
            {
                s.Output = (OutputMode)output.SelectedIndex;
                SettingsChanged();
            };
            Row(grid, ref r, "輸出方式", output);

            var ime = Check("注音等中文輸入法在「中文模式」時自動停用", s.PauseInChineseIme, delegate(bool v) { s.PauseInChineseIme = v; });
            var ss = Check("Shift+空白 只輸出空白（避免不小心切成全形）", s.ShiftSpaceAsSpace, delegate(bool v) { s.ShiftSpaceAsSpace = v; });
            Row(grid, ref r, "輸入法", Stack(ime, ss));

            var excl = new TextBox { Width = S(420), Text = s.ExcludedApps };
            excl.Leave += delegate
            {
                s.ExcludedApps = excl.Text.Trim();
                SettingsChanged();
            };
            Row(grid, ref r, "不要啟用的程式", Stack(excl, Hint("程式檔名，以分號隔開，例如：game.exe; mstsc.exe")));

            var autostart = new CheckBox { Text = "開機時自動啟動 DrType", AutoSize = true, Checked = AutoStart.IsOn() };
            autostart.CheckedChanged += delegate { SetAutoStart(autostart.Checked); };
            Row(grid, ref r, "開機", autostart);

            var info = Hint("資料夾：" + data.Dir + "\r\n版本 " + Application.ProductVersion);
            Row(grid, ref r, "", info);
            grid.RowCount = r;
            loadingSettings = false;
            return page;
        }

        private void Row(TableLayoutPanel grid, ref int row, string label, Control c)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, S(7), S(16), S(6)), Font = new Font(Font, FontStyle.Bold) };
            grid.Controls.Add(l, 0, row);
            c.Margin = new Padding(3, 3, 3, S(6));
            grid.Controls.Add(c, 1, row);
            row++;
        }

        private static Control Stack(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            p.Controls.AddRange(cs);
            return p;
        }

        private static Control Inline(params Control[] cs)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in cs) c.Anchor = AnchorStyles.Left;
            p.Controls.AddRange(cs);
            return p;
        }

        private CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = value };
            c.CheckedChanged += delegate
            {
                set(c.Checked);
                SettingsChanged();
            };
            return c;
        }

        private NumericUpDown Num(int min, int max, int value, Action<int> set)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = S(70) };
            n.ValueChanged += delegate
            {
                set((int)n.Value);
                SettingsChanged();
            };
            return n;
        }

        private void SettingsChanged()
        {
            if (loadingSettings) return;
            data.SaveSettings();
            Changed();
        }

        private void SetAutoStart(bool on)
        {
            try
            {
                AutoStart.Set(on);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "無法設定開機啟動：" + ex.Message, "DrType");
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S) && tabs.SelectedIndex == 1)
            {
                SaveHotstring(false);
                return true;
            }
            if (keyData == (Keys.Control | Keys.F))
            {
                if (tabs.SelectedIndex == 0) wordSearch.Focus();
                else if (tabs.SelectedIndex == 1) hsSearch.Focus();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    // 開機自動啟動：寫在目前使用者的 Run 機碼
    public static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "DrType";
        private const string OldName = "TypeAhead"; // 改名前的版本

        public static bool IsOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k == null ? null : k.GetValue(Name) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(Name, false);
            }
        }

        // 舊版設過開機啟動的話，改成啟動 DrType
        public static void MigrateOldName()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null || k.GetValue(OldName) == null) return;
                    k.DeleteValue(OldName, false);
                    k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch (Exception) { }
        }
    }

    // 簡單的輸入對話框
    public static class TextPrompt
    {
        public static string Show(IWin32Window owner, string title, string label, string value, bool multiline)
        {
            using (var f = new Form())
            {
                float scale;
                using (var g = f.CreateGraphics()) scale = g.DpiX / 96f;
                f.Text = title;
                f.Font = new Font("Microsoft JhengHei UI", 10);
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.Size = new Size((int)(460 * scale), (int)((multiline ? 420 : 170) * scale));
                f.Padding = new Padding((int)(10 * scale));

                var lbl = new Label { Text = label, Dock = DockStyle.Top, AutoSize = true };
                var box = new TextBox { Text = value, Dock = multiline ? DockStyle.Fill : DockStyle.Top, Multiline = multiline, AcceptsReturn = multiline, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None, Font = new Font("Segoe UI", 11) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
                var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                f.Controls.Add(box);
                f.Controls.Add(lbl);
                f.Controls.Add(buttons);
                if (!multiline) f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Shown += delegate
                {
                    box.Focus();
                    box.SelectAll();
                };
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
