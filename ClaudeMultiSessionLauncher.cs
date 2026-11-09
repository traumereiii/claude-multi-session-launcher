// ClaudeMultiSessionLauncher — 클론 + Windows Terminal 세션 열기를 화면에서 하는 도구.
// 실제 일은 같은 폴더의 open-sessions.ps1 이 한다. 이 프로그램은 화면·설정 저장·git clone 만 맡는다.
// 빌드: launcher\build.ps1 (설치 없이 .NET Framework 의 csc.exe 사용 → C# 5 문법만 쓴다)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeMultiSessionLauncher
{
    // 프로젝트 하나: 폴더 {Root}\{Name}\{Name}-a ~ , 세션 이름 {Title}A ~
    class ProjectDef
    {
        public string Name;     // 폴더 이름(예: my-app)
        public string Title;    // 세션 이름 접두사(예: MY-APP-)
        public int Count;       // 폴더 개수(a ~)
        public string Remote;   // 원격 주소

        public string Prefix { get { return Name + "-"; } }

        public static string DefaultTitle(string name) { return name.ToUpperInvariant() + "-"; }

        // 세션 이름 = 접두사 + 글자. 글자의 대소문자는 접두사를 따른다(접두사가 모두 소문자면 소문자, 아니면 대문자).
        // open-sessions.ps1 도 같은 규칙으로 이름을 만든다.
        public static string SessionName(string title, string letter)
        {
            title = title ?? "";
            bool lower = title.Any(char.IsLower) && !title.Any(char.IsUpper);
            return title + (lower ? letter.ToLowerInvariant() : letter.ToUpperInvariant());
        }

        // 원격 주소에서 저장소 이름을 뽑는다(폴더명 기본값).
        // https://github.com/org/my_app.git · git@github.com:org/repo.git · D:\repos\repo → my_app · repo · repo
        public static string NameFromRemote(string url)
        {
            string u = (url ?? "").Trim().TrimEnd('/', '\\');
            int cut = u.LastIndexOfAny(new[] { '/', '\\', ':' });
            if (cut >= 0) u = u.Substring(cut + 1);
            if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u.Substring(0, u.Length - 4);
            return Regex.Replace(u, @"[^A-Za-z0-9._-]", "");   // 폴더명에 쓸 수 없는 글자는 뺀다
        }

        public static List<string> Letters(int count)
        {
            var list = new List<string>();
            for (int i = 0; i < count && i < 26; i++) list.Add(((char)('a' + i)).ToString());
            return list;
        }

        public string Serialize() { return string.Join("|", new[] { Name, Title, Count.ToString(), Remote ?? "" }); }

        public static ProjectDef Parse(string s)
        {
            string[] p = s.Split('|');
            int n;
            if (p.Length < 3 || p[0].Length == 0 || !int.TryParse(p[2], out n)) return null;
            return new ProjectDef { Name = p[0], Title = p[1], Count = Math.Max(1, Math.Min(26, n)), Remote = p.Length > 3 ? p[3] : "" };
        }
    }

    // 실행 옵션 한 벌(마지막 실행값·최근 실행 목록에 저장된다)
    class RunOptions
    {
        public string Project = "";
        public List<string> Names = new List<string>();
        public int Monitor;     // 0 = 주 모니터
        public int Cols;        // 0 = 자동
        public bool Bypass, NoLayout, Tabs;

        // claude 에 넘길 모델·effort. 빈 문자열이면 지정하지 않는다(claude 기본값/기존 설정 유지).
        public const string DefaultModel = "claude-sonnet-5-5";
        public const string DefaultEffort = "high";
        public string Model = DefaultModel;
        public string Effort = DefaultEffort;

        public string Serialize()
        {
            return string.Join("|", new[] {
                Project, string.Join(",", Names.ToArray()), Monitor.ToString(), Cols.ToString(),
                Bypass ? "1" : "0", NoLayout ? "1" : "0", Tabs ? "1" : "0", Model ?? "", Effort ?? "" });
        }

        public static RunOptions Parse(string s)
        {
            string[] p = s.Split('|');
            int m, c;
            if (p.Length < 7 || p[0].Length == 0 || !int.TryParse(p[2], out m) || !int.TryParse(p[3], out c)) return null;
            var o = new RunOptions { Project = p[0], Monitor = m, Cols = c, Bypass = p[4] == "1", NoLayout = p[5] == "1", Tabs = p[6] == "1" };
            // 모델·effort 가 없던 예전 설정은 기본값(sonnet 5.5 / high)을 쓴다
            if (p.Length >= 9) { o.Model = p[7]; o.Effort = p[8]; }
            foreach (string n in p[1].Split(',')) if (n.Length > 0) o.Names.Add(n);
            return o;
        }

        public RunOptions Copy() { return Parse(Serialize()); }

        // 목록에 보여 줄 한 줄 설명
        public string Describe()
        {
            var parts = new List<string>();
            parts.Add(Monitor == 0 ? "주 모니터" : "모니터 " + (Monitor + 1));
            parts.Add(NoLayout ? "배치 안 함" : (Cols == 0 ? "자동 배치" : Cols + "열"));
            if (Tabs) parts.Add("탭");
            if (Bypass) parts.Add("Bypass");
            if (!string.IsNullOrEmpty(Model)) parts.Add(Model);
            if (!string.IsNullOrEmpty(Effort)) parts.Add("effort " + Effort);
            string names = string.Join(",", Names.Select(n => n.ToUpperInvariant()).ToArray());
            return Project + "   " + names + "   [" + string.Join(" · ", parts.ToArray()) + "]";
        }
    }

    // 설정 파일(key=value 줄 형식). 기본 위치: %APPDATA%\ClaudeMultiSessionLauncher\settings.txt
    class Settings
    {
        public string Root;
        public bool CloseAfterRun;
        public List<ProjectDef> Projects = new List<ProjectDef>();
        public RunOptions Last;
        public List<RunOptions> Recent = new List<RunOptions>();
        public const int MaxRecent = 5;
        string path;

        public static Settings Load(string homeDir, string defaultRoot)
        {
            var s = new Settings { Root = defaultRoot, path = Path.Combine(homeDir, "settings.txt") };
            if (File.Exists(s.path))
            {
                foreach (string line in File.ReadAllLines(s.path, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    switch (k)
                    {
                        case "root": if (v.Length > 0) s.Root = v; break;
                        case "closeAfterRun": s.CloseAfterRun = v == "1"; break;
                        case "project": { var p = ProjectDef.Parse(v); if (p != null) s.Projects.Add(p); break; }
                        case "last": s.Last = RunOptions.Parse(v); break;
                        case "recent": { var o = RunOptions.Parse(v); if (o != null && s.Recent.Count < MaxRecent) s.Recent.Add(o); break; }
                    }
                }
            }
            // 내장 프로젝트는 없다. 처음 실행하면 비어 있고, 클론 탭에서 클론하면 등록된다.
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.AppendLine("root=" + Root);
            sb.AppendLine("closeAfterRun=" + (CloseAfterRun ? "1" : "0"));
            foreach (var p in Projects) sb.AppendLine("project=" + p.Serialize());
            if (Last != null) sb.AppendLine("last=" + Last.Serialize());
            foreach (var o in Recent) sb.AppendLine("recent=" + o.Serialize());
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public ProjectDef Find(string name) { return Projects.FirstOrDefault(p => p.Name == name); }

        public void UpsertProject(ProjectDef def)
        {
            var old = Find(def.Name);
            if (old == null) Projects.Add(def);
            else { old.Title = def.Title; old.Count = def.Count; old.Remote = def.Remote; }
        }

        // 최근 실행에 넣는다: 같은 옵션이 있으면 맨 위로 올리고, 5개를 넘으면 오래된 것을 버린다
        public void PushRecent(RunOptions o)
        {
            string key = o.Serialize();
            Recent.RemoveAll(r => r.Serialize() == key);
            Recent.Insert(0, o.Copy());
            while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
        }
    }

    static class Launch
    {
        static string Q(string s) { return "\"" + s.TrimEnd('\\') + "\""; }

        // open-sessions.ps1 에 넘길 인자
        public static string BuildArgs(string script, string root, ProjectDef p, RunOptions o, bool dryRun)
        {
            var sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -File ").Append("\"").Append(script).Append("\"");
            sb.Append(" -Group ").Append(Q(p.Name));
            sb.Append(" -Prefix ").Append(Q(p.Prefix));
            sb.Append(" -TitlePrefix ").Append(Q(p.Title));
            sb.Append(" -Names ").Append(string.Join(",", o.Names.ToArray()));
            sb.Append(" -Root ").Append(Q(root));
            sb.Append(" -Monitor ").Append(o.Monitor);
            sb.Append(" -Cols ").Append(o.Cols);
            if (!string.IsNullOrEmpty(o.Model)) sb.Append(" -Model ").Append(Q(o.Model));
            if (!string.IsNullOrEmpty(o.Effort)) sb.Append(" -Effort ").Append(Q(o.Effort));
            if (o.Bypass) sb.Append(" -Bypass");
            if (o.NoLayout) sb.Append(" -NoLayout");
            if (o.Tabs) sb.Append(" -Tabs");
            if (dryRun) sb.Append(" -DryRun");
            return sb.ToString();
        }

        // 스크립트는 exe 와 같은 폴더에서 찾고, 없으면 한 단계 위, 그래도 없으면 작업 루트에서 찾는다
        public static string FindScript(string root)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var dirs = new List<string> { exeDir };
            try { dirs.Add(Directory.GetParent(exeDir.TrimEnd('\\')).FullName); } catch (Exception) { }
            dirs.Add(root);
            foreach (string d in dirs)
            {
                string f = Path.Combine(d, "open-sessions.ps1");
                if (File.Exists(f)) return f;
            }
            return null;
        }

        [DllImport("kernel32.dll")] static extern uint GetOEMCP();
        // powershell 이 리다이렉트된 출력에 쓰는 인코딩(콘솔 코드 페이지)
        public static Encoding ConsoleEncoding() { try { return Encoding.GetEncoding((int)GetOEMCP()); } catch (Exception) { return Encoding.Default; } }
    }

    static class Cloner
    {
        static string Q(string s) { return "\"" + s + "\""; }

        // {root}\{name}\{name}-a ~ 에 원격 저장소를 count 개 클론한다(동시에 진행). 이미 내용이 있는 폴더는 건너뛴다.
        // 돌려주는 값: 사용할 수 있게 된(새로 받았거나 이미 있던) 폴더 수
        public static int CloneAll(string root, string name, string url, int count, Action<string> log)
        {
            return CloneLetters(root, name, url, ProjectDef.Letters(count), log).Count;
        }

        // 지정한 글자의 폴더만 클론한다(세션 추가에 쓴다). 돌려주는 값: 사용할 수 있게 된 글자 목록(가나다순)
        public static List<string> CloneLetters(string root, string name, string url, IEnumerable<string> letters, Action<string> log)
        {
            string group = Path.Combine(root, name);
            Directory.CreateDirectory(group);
            var ok = new List<string>();
            object lk = new object();
            var threads = new List<Thread>();
            foreach (string letterItem in letters)
            {
                string letter = letterItem;
                string dest = Path.Combine(group, name + "-" + letter);
                if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
                {
                    log("[" + letter + "] 이미 있어 건너뜁니다: " + dest);
                    lock (lk) ok.Add(letter);
                    continue;
                }
                var t = new Thread(() =>
                {
                    bool success = CloneOne(url, dest, letter, log);
                    if (success) lock (lk) ok.Add(letter);
                });
                t.IsBackground = true;
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join();
            ok.Sort(StringComparer.Ordinal);
            return ok;
        }

        static bool CloneOne(string url, string dest, string letter, Action<string> log)
        {
            log("[" + letter + "] 클론 시작 → " + dest);
            var psi = new ProcessStartInfo("git", "clone -q " + Q(url) + " " + Q(dest))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";   // 인증을 물어보며 멈추지 않게 한다
            try
            {
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) { log("[" + letter + "] 완료"); return true; }
                    log("[" + letter + "] 실패(exit " + p.ExitCode + "): " + err.Trim());
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                log("[" + letter + "] git 을 찾을 수 없습니다. Git 이 설치돼 있고 PATH 에 있는지 확인하세요.");
                return false;
            }
        }
    }

    // 이미 클론돼 있는 프로젝트 폴더를 훑은 결과
    class ExistingScan
    {
        public string Name;     // 프로젝트 폴더 이름(예: safed)
        public string Root;     // 그 폴더의 상위 = 작업 폴더(예: C:\workspace). 드라이브 루트면 null
        public List<string> Letters = new List<string>();   // 실제로 있는 글자 폴더(a, b, …)
        public int Count;       // 가장 뒤 글자까지의 개수(a,b,c,f 가 있으면 6)
    }

    // 이미 클론된 프로젝트를 등록하기 위한 도우미(클론은 하지 않는다)
    static class Registrar
    {
        // {dir}\{이름}-a ~ z 중 있는 폴더를 찾는다. 폴더가 없으면 null.
        public static ExistingScan Scan(string dir)
        {
            string d = (dir ?? "").Trim().TrimEnd('\\', '/');
            if (d.Length == 0 || !Directory.Exists(d)) return null;
            var r = new ExistingScan { Name = Path.GetFileName(d) };
            try { var parent = Directory.GetParent(d); if (parent != null) r.Root = parent.FullName.TrimEnd('\\'); } catch (Exception) { }
            foreach (string l in ProjectDef.Letters(26))
                if (Directory.Exists(Path.Combine(d, r.Name + "-" + l))) { r.Letters.Add(l); r.Count = l[0] - 'a' + 1; }
            return r;
        }

        // 클론된 폴더의 origin 주소를 읽는다(없거나 git 이 없으면 빈 문자열)
        public static string ReadRemote(string folder)
        {
            var psi = new ProcessStartInfo("git", "-C \"" + folder.TrimEnd('\\') + "\" config --get remote.origin.url")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (Exception) { } return ""; }
                    return p.ExitCode == 0 ? output.Trim() : "";
                }
            }
            catch (Exception) { return ""; }
        }
    }

    class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        readonly Settings s;
        bool loading;
        bool titleEdited;   // 세션 이름 접두사를 직접 고쳤으면 폴더명에서 자동으로 채우지 않는다
        bool nameEdited;    // 폴더명을 직접 고쳤으면 원격 주소에서 자동으로 채우지 않는다
        bool autoFilling;   // 코드가 칸을 채우는 중(직접 고친 것으로 치지 않는다)
        // 탭 페이지는 만들 때 아주 작아서, 그때 Anchor 를 주면 나중에 페이지가 커질 때 컨트롤이 과하게 늘어난다.
        // 그래서 창이 뜬 뒤(Load)에 적용한다.
        readonly List<KeyValuePair<Control, AnchorStyles>> pendingAnchors = new List<KeyValuePair<Control, AnchorStyles>>();
        readonly int startTab;
        bool busy;   // 열기·미리보기·세션 추가 중에는 다른 작업을 막는다

        // 실행 탭
        ComboBox cmbProject, cmbMonitor, cmbCols, cmbModel, cmbEffort;
        const string NoneLabel = "(지정 안 함)";
        FlowLayoutPanel flpSessions;
        CheckBox chkBypass, chkNoLayout, chkTabs, chkClose;
        Button btnPreview, btnRun;
        ListBox lstRecent;
        TextBox txtLog;
        Label lblStatus, lblRoot;

        // 클론 탭
        internal TextBox txtName, txtUrl, txtTitle, txtRoot, txtCloneLog;   // internal: 자체 점검에서 쓴다
        internal NumericUpDown nudCount;
        internal TextBox lblClonePreview;   // 미리보기(읽기 전용, 길면 스크롤)
        Button btnClone;

        // 등록 탭(이미 클론된 프로젝트)
        internal TextBox txtRegPath, txtRegUrl, txtRegTitle, txtRegLog;
        internal NumericUpDown nudRegCount;
        internal TextBox lblRegPreview;
        Button btnRegister;
        bool regTitleEdited, regUrlEdited;   // 직접 고친 칸은 폴더를 다시 고를 때 덮어쓰지 않는다
        bool regAutoFilling;

        public MainForm(Settings settings, int startTab)
        {
            s = settings;
            this.startTab = startTab;
            Text = "Claude Multi Session Launcher";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(700, 700);
            MinimumSize = new Size(716, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            LoadState();
            Load += delegate
            {
                foreach (var kv in pendingAnchors) kv.Key.Anchor = kv.Value;
                pendingAnchors.Clear();
                // 등록된 프로젝트가 없으면 실행할 것이 없으므로 클론 탭으로 연다
                int tab = s.Projects.Count == 0 ? 1 : this.startTab;
                if (tab > 0 && Controls.Count > 0 && Controls[0] is TabControl) ((TabControl)Controls[0]).SelectedIndex = tab;
            };
            FormClosing += delegate { try { s.Last = Collect(); s.Save(); } catch (Exception) { } };
        }

        T Anchored<T>(T c, AnchorStyles a) where T : Control { pendingAnchors.Add(new KeyValuePair<Control, AnchorStyles>(c, a)); return c; }

        // ───────── 화면 구성 ─────────
        void BuildUi()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var run = new TabPage("실행");
            var clone = new TabPage("클론");
            tabs.TabPages.Add(run);
            var register = new TabPage("등록");
            tabs.TabPages.Add(clone);
            tabs.TabPages.Add(register);
            Controls.Add(tabs);
            BuildRunTab(run);
            BuildCloneTab(clone);
            BuildRegisterTab(register);
        }

        static Label L(string text, int x, int y, int w) { return new Label { Text = text, Left = x, Top = y + 4, Width = w, AutoSize = false }; }

        void BuildRunTab(TabPage t)
        {
            t.Controls.Add(L("프로젝트", 14, 14, 80));
            cmbProject = new ComboBox { Left = 100, Top = 14, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbProject.SelectedIndexChanged += delegate { if (!loading) OnProjectChanged(); };
            t.Controls.Add(cmbProject);

            t.Controls.Add(L("세션", 14, 50, 80));
            flpSessions = new FlowLayoutPanel { Left = 100, Top = 46, Width = 480, Height = 64, WrapContents = true, AutoScroll = true };
            t.Controls.Add(flpSessions);
            var btnAll = new Button { Text = "전체 선택", Left = 590, Top = 46, Width = 80, Height = 28 };
            var btnNone = new Button { Text = "전체 해제", Left = 590, Top = 78, Width = 80, Height = 28 };
            btnAll.Click += delegate { SetAllSessions(true); };
            btnNone.Click += delegate { SetAllSessions(false); };
            t.Controls.Add(btnAll);
            t.Controls.Add(btnNone);

            t.Controls.Add(L("모니터", 14, 122, 80));
            cmbMonitor = new ComboBox { Left = 100, Top = 122, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            var screens = ScreensInOrder();
            for (int i = 0; i < screens.Count; i++)
                cmbMonitor.Items.Add((i == 0 ? "주 모니터" : "모니터 " + (i + 1)) + " (" + screens[i].Bounds.Width + "x" + screens[i].Bounds.Height + ")");
            t.Controls.Add(cmbMonitor);
            t.Controls.Add(L("열 수", 380, 122, 50));
            cmbCols = new ComboBox { Left = 436, Top = 122, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbCols.Items.Add("자동");
            for (int c = 1; c <= 8; c++) cmbCols.Items.Add(c + "열");
            t.Controls.Add(cmbCols);

            // 모델은 직접 입력도 가능(별칭 sonnet/opus 나 전체 모델 ID). 비우거나 "(지정 안 함)" 이면 --model 을 붙이지 않는다.
            t.Controls.Add(L("모델", 14, 158, 80));
            cmbModel = new ComboBox { Left = 100, Top = 158, Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
            cmbModel.Items.AddRange(new object[] { NoneLabel, "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-haiku-5-5", "sonnet", "opus", "haiku", "fable" });
            cmbModel.Text = RunOptions.DefaultModel;
            t.Controls.Add(cmbModel);
            t.Controls.Add(L("Effort", 380, 158, 50));
            cmbEffort = new ComboBox { Left = 436, Top = 158, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbEffort.Items.AddRange(new object[] { NoneLabel, "low", "medium", "high", "xhigh", "max" });
            cmbEffort.SelectedItem = RunOptions.DefaultEffort;
            t.Controls.Add(cmbEffort);

            chkBypass = new CheckBox { Text = "권한 확인 건너뛰기 (--dangerously-skip-permissions)", Left = 100, Top = 194, Width = 540 };
            chkNoLayout = new CheckBox { Text = "창 위치·크기 배치 안 함 (Windows Terminal 기본값)", Left = 100, Top = 220, Width = 540 };
            chkTabs = new CheckBox { Text = "한 창의 탭으로 열기 (이 경우 배치는 적용되지 않음)", Left = 100, Top = 246, Width = 540 };
            chkClose = new CheckBox { Text = "열기 후 런처 닫기", Left = 100, Top = 272, Width = 540 };
            t.Controls.AddRange(new Control[] { chkBypass, chkNoLayout, chkTabs, chkClose });

            btnPreview = new Button { Text = "미리보기", Left = 100, Top = 308, Width = 120, Height = 34 };
            btnRun = new Button { Text = "열기", Left = 230, Top = 308, Width = 150, Height = 34 };
            btnPreview.Click += delegate { Execute(Collect(), true); };
            btnRun.Click += delegate { Execute(Collect(), false); };
            t.Controls.Add(btnPreview);
            t.Controls.Add(btnRun);
            lblStatus = new Label { Left = 392, Top = 314, Width = 280, AutoSize = false, ForeColor = Color.DimGray };
            t.Controls.Add(lblStatus);

            t.Controls.Add(new Label { Text = "최근 실행 — 클릭하면 옵션을 불러오고, 더블클릭하면 바로 실행합니다", Left = 14, Top = 358, Width = 650, AutoSize = false });
            lstRecent = new ListBox { Left = 14, Top = 382, Width = 656, Height = 80, IntegralHeight = false };
            Anchored(lstRecent, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lstRecent.SelectedIndexChanged += delegate { OnRecentSelected(); };
            lstRecent.DoubleClick += delegate { if (lstRecent.SelectedIndex >= 0) Execute(Collect(), false); };
            t.Controls.Add(lstRecent);

            t.Controls.Add(new Label { Text = "결과", Left = 14, Top = 470, Width = 100, AutoSize = false });
            lblRoot = new Label { Left = 120, Top = 474, Width = 550, AutoSize = false, ForeColor = Color.DimGray };
            t.Controls.Add(lblRoot);
            txtLog = new TextBox { Left = 14, Top = 492, Width = 656, Height = 164, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            Anchored(txtLog, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom);
            t.Controls.Add(txtLog);
        }

        void BuildCloneTab(TabPage t)
        {
            t.Controls.Add(L("폴더명", 14, 14, 90));
            txtName = new TextBox { Left = 110, Top = 14, Width = 220 };
            t.Controls.Add(txtName);
            t.Controls.Add(L("원격 주소", 14, 50, 90));
            txtUrl = new TextBox { Left = 110, Top = 50, Width = 460 };
            Anchored(txtUrl, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(txtUrl);
            // 저장소 주소를 찾으러 GitHub 를 기본 브라우저로 연다
            var btnGitHub = new Button { Text = "GitHub", Left = 580, Top = 48, Width = 80, Height = 28 };
            Anchored(btnGitHub, AnchorStyles.Top | AnchorStyles.Right);
            btnGitHub.Click += delegate
            {
                try { Process.Start("https://github.com"); }
                catch (Exception ex) { MessageBox.Show(this, "브라우저를 열 수 없습니다: " + ex.Message, Text); }
            };
            t.Controls.Add(btnGitHub);
            t.Controls.Add(L("갯수", 14, 86, 90));
            nudCount = new NumericUpDown { Left = 110, Top = 86, Width = 60, Minimum = 1, Maximum = 26, Value = 6 };
            t.Controls.Add(nudCount);
            t.Controls.Add(L("세션 이름 접두사", 200, 86, 120));
            txtTitle = new TextBox { Left = 326, Top = 86, Width = 334 };   // 원격 주소 칸과 오른쪽 끝을 맞춘다
            Anchored(txtTitle, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(txtTitle);
            t.Controls.Add(L("저장 위치", 14, 122, 90));
            txtRoot = new TextBox { Left = 110, Top = 122, Width = 460 };
            Anchored(txtRoot, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(txtRoot);
            var btnBrowse = new Button { Text = "찾아보기", Left = 580, Top = 120, Width = 80, Height = 28 };
            Anchored(btnBrowse, AnchorStyles.Top | AnchorStyles.Right);
            btnBrowse.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    if (Directory.Exists(txtRoot.Text)) d.SelectedPath = txtRoot.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) txtRoot.Text = d.SelectedPath;
                }
            };
            t.Controls.Add(btnBrowse);

            // 미리보기: 저장 폴더 한 줄 + 만들어질 "[세션명] 폴더명" 전부. 줄이 많으면 스크롤된다.
            lblClonePreview = new TextBox
            {
                Left = 110, Top = 158, Width = 550, Height = 204, Multiline = true, ReadOnly = true, WordWrap = false,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false,
                BackColor = SystemColors.Control, ForeColor = Color.DimGray
            };
            Anchored(lblClonePreview, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(lblClonePreview);
            btnClone = new Button { Text = "클론", Left = 110, Top = 368, Width = 140, Height = 34 };
            btnClone.Click += delegate { StartClone(); };
            t.Controls.Add(btnClone);

            txtCloneLog = new TextBox { Left = 14, Top = 414, Width = 656, Height = 240, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            Anchored(txtCloneLog, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom);
            t.Controls.Add(txtCloneLog);

            // 원격 주소 → 폴더명(저장소 이름) → 세션 이름 접두사(대문자 + "-") 순서로 기본값을 채운다.
            // 직접 고친 칸은 건드리지 않고, 그 칸을 비우면 다시 자동으로 채운다.
            txtUrl.TextChanged += delegate
            {
                if (nameEdited) return;
                autoFilling = true;
                txtName.Text = ProjectDef.NameFromRemote(txtUrl.Text);
                autoFilling = false;
            };
            txtName.TextChanged += delegate
            {
                string name = txtName.Text.Trim();
                if (!autoFilling) nameEdited = name.Length > 0;
                if (!titleEdited)
                {
                    bool was = autoFilling;
                    autoFilling = true;
                    txtTitle.Text = name.Length > 0 ? ProjectDef.DefaultTitle(name) : "";
                    autoFilling = was;
                }
                UpdateClonePreview();
            };
            txtTitle.TextChanged += delegate
            {
                if (!autoFilling) titleEdited = txtTitle.Text.Trim().Length > 0;
                UpdateClonePreview();
            };
            nudCount.ValueChanged += delegate { UpdateClonePreview(); };
            nudCount.TextChanged += delegate { UpdateClonePreview(); };   // 입력 칸을 벗어나기 전에도 바로 반영
            txtRoot.TextChanged += delegate
            {
                // 작업 폴더를 고치면 클론하지 않아도 바로 반영한다(실행 탭이 이 값으로 폴더를 찾는다)
                string r = txtRoot.Text.Trim().TrimEnd('\\');
                if (!loading && r.Length > 0) s.Root = r;
                UpdateRootLabel();
                UpdateClonePreview();
            };
        }

        // 이미 클론된 프로젝트를 실행 탭에 등록한다. 프로젝트 폴더(예: C:\workspace\safed)를 고르면
        // 그 아래 {폴더명}-a ~ 를 훑어 갯수·원격 주소·세션 이름 접두사를 채운다. 클론은 하지 않는다.
        void BuildRegisterTab(TabPage t)
        {
            t.Controls.Add(L("프로젝트 폴더", 14, 14, 90));
            txtRegPath = new TextBox { Left = 110, Top = 14, Width = 460 };
            Anchored(txtRegPath, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(txtRegPath);
            var btnBrowse = new Button { Text = "찾아보기", Left = 580, Top = 12, Width = 80, Height = 28 };
            Anchored(btnBrowse, AnchorStyles.Top | AnchorStyles.Right);
            btnBrowse.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "세션 폴더들(예: safed-a, safed-b …)이 들어 있는 프로젝트 폴더(예: safed)를 고르세요.";
                    if (Directory.Exists(txtRegPath.Text.Trim())) d.SelectedPath = txtRegPath.Text.Trim();
                    else if (Directory.Exists(s.Root)) d.SelectedPath = s.Root;
                    if (d.ShowDialog(this) == DialogResult.OK) txtRegPath.Text = d.SelectedPath;
                }
            };
            t.Controls.Add(btnBrowse);

            t.Controls.Add(L("원격 주소", 14, 50, 90));
            txtRegUrl = new TextBox { Left = 110, Top = 50, Width = 550 };
            Anchored(txtRegUrl, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(txtRegUrl);

            t.Controls.Add(L("갯수", 14, 86, 90));
            nudRegCount = new NumericUpDown { Left = 110, Top = 86, Width = 60, Minimum = 1, Maximum = 26, Value = 6 };
            t.Controls.Add(nudRegCount);
            t.Controls.Add(L("세션 이름 접두사", 200, 86, 120));
            txtRegTitle = new TextBox { Left = 326, Top = 86, Width = 334 };
            Anchored(txtRegTitle, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(txtRegTitle);

            t.Controls.Add(new Label
            {
                Text = "폴더를 고르면 갯수·원격 주소를 폴더에서 읽어 채웁니다. 갯수는 고쳐도 됩니다. 원격 주소는 세션 추가(+ 추가)에만 필요합니다.",
                Left = 110, Top = 122, Width = 550, Height = 32, AutoSize = false, ForeColor = Color.DimGray
            });

            lblRegPreview = new TextBox
            {
                Left = 110, Top = 158, Width = 550, Height = 204, Multiline = true, ReadOnly = true, WordWrap = false,
                ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false,
                BackColor = SystemColors.Control, ForeColor = Color.DimGray
            };
            Anchored(lblRegPreview, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            t.Controls.Add(lblRegPreview);
            btnRegister = new Button { Text = "등록", Left = 110, Top = 368, Width = 140, Height = 34 };
            btnRegister.Click += delegate { StartRegister(); };
            t.Controls.Add(btnRegister);

            txtRegLog = new TextBox { Left = 14, Top = 414, Width = 656, Height = 240, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            Anchored(txtRegLog, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom);
            t.Controls.Add(txtRegLog);

            // 폴더 → 갯수·원격 주소·세션 이름 접두사. 직접 고친 칸은 건드리지 않고, 비우면 다시 자동으로 채운다.
            txtRegPath.TextChanged += delegate { OnRegPathChanged(); };
            txtRegTitle.TextChanged += delegate
            {
                if (!regAutoFilling) regTitleEdited = txtRegTitle.Text.Trim().Length > 0;
                UpdateRegPreview();
            };
            txtRegUrl.TextChanged += delegate { if (!regAutoFilling) regUrlEdited = txtRegUrl.Text.Trim().Length > 0; };
            nudRegCount.ValueChanged += delegate { UpdateRegPreview(); };
            nudRegCount.TextChanged += delegate { UpdateRegPreview(); };
            UpdateRegPreview();
        }

        void OnRegPathChanged()
        {
            var scan = Registrar.Scan(txtRegPath.Text);
            regAutoFilling = true;
            try
            {
                if (scan != null && scan.Count > 0)
                {
                    nudRegCount.Value = scan.Count;
                    if (!regTitleEdited) txtRegTitle.Text = ProjectDef.DefaultTitle(scan.Name);
                    if (!regUrlEdited)
                        txtRegUrl.Text = Registrar.ReadRemote(Path.Combine(txtRegPath.Text.Trim().TrimEnd('\\'), scan.Name + "-" + scan.Letters[0]));
                }
                else
                {
                    if (!regTitleEdited) txtRegTitle.Text = "";
                    if (!regUrlEdited) txtRegUrl.Text = "";
                }
            }
            finally { regAutoFilling = false; }
            UpdateRegPreview();
        }

        void UpdateRegPreview()
        {
            string path = txtRegPath.Text.Trim().TrimEnd('\\');
            if (path.Length == 0) { lblRegPreview.Text = "이미 클론된 프로젝트 폴더(세션 폴더 {폴더명}-a, -b … 가 들어 있는 폴더)를 고르세요.\r\n예) C:\\workspace\\safed  (안에 safed-a, safed-b … 가 있는 경우)"; return; }
            var scan = Registrar.Scan(path);
            if (scan == null) { lblRegPreview.Text = "폴더를 찾을 수 없습니다: " + path; return; }
            if (scan.Count == 0) { lblRegPreview.Text = "'" + path + "' 아래에 " + scan.Name + "-a, " + scan.Name + "-b … 폴더가 없습니다.\r\n세션 폴더가 들어 있는 상위 폴더(프로젝트 폴더)를 고르세요."; return; }
            string title = txtRegTitle.Text.Trim();
            if (title.Length == 0) title = ProjectDef.DefaultTitle(scan.Name);
            int n = (int)nudRegCount.Value;
            // 한 줄 = "[세션명] 폴더명". 폴더가 실제로 없는 글자는 표시한다(열 때 건너뛴다).
            var rows = ProjectDef.Letters(n).Select(l => "[" + ProjectDef.SessionName(title, l) + "] " + scan.Name + "-" + l
                + (scan.Letters.Contains(l) ? "" : "   ← 폴더 없음(열 때 건너뜀)")).ToArray();
            int outside = scan.Letters.Count(l => l[0] - 'a' + 1 > n);   // 갯수보다 뒤 글자의 폴더
            string extra = outside > 0 ? "\r\n※ 폴더 " + outside + "개(" + scan.Name + "-" + ProjectDef.Letters(26)[n] + " ~)는 갯수 밖이라 등록되지 않습니다." : "";
            lblRegPreview.Text = "프로젝트 폴더:  " + path + "   (" + n + "개)\r\n" + string.Join("\r\n", rows) + extra;
        }

        void RegLog(string line)
        {
            try { BeginInvoke(new Action(delegate { txtRegLog.AppendText(line + "\r\n"); })); } catch (Exception) { }
        }

        void StartRegister()
        {
            string path = txtRegPath.Text.Trim().TrimEnd('\\');
            var scan = Registrar.Scan(path);
            if (scan == null) { MessageBox.Show(this, "프로젝트 폴더를 찾을 수 없습니다. 폴더를 고르세요.", Text); return; }
            if (!Regex.IsMatch(scan.Name, @"^[A-Za-z0-9._-]+$")) { MessageBox.Show(this, "폴더명은 영문·숫자·. _ - 만 쓸 수 있습니다: " + scan.Name, Text); return; }
            if (scan.Root == null) { MessageBox.Show(this, "드라이브 루트는 프로젝트 폴더로 쓸 수 없습니다. 하위 폴더를 고르세요.", Text); return; }
            if (scan.Count == 0) { MessageBox.Show(this, "'" + path + "' 아래에 " + scan.Name + "-a 같은 세션 폴더가 없습니다.\n세션 폴더가 들어 있는 상위 폴더를 고르세요.", Text); return; }

            int count = (int)nudRegCount.Value;
            string title = txtRegTitle.Text.Trim();
            if (title.Length == 0) title = ProjectDef.DefaultTitle(scan.Name);
            string url = txtRegUrl.Text.Trim();
            bool rootChanged = !string.Equals(scan.Root, s.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            string first = ProjectDef.SessionName(title, "a"), last = ProjectDef.SessionName(title, ((char)('a' + count - 1)).ToString());
            string msg = "'" + scan.Name + "' 을(를) 실행 탭에 등록합니다.\n\n세션: " + first + " ~ " + last + " (" + count + "개)\n폴더: " + path + "\\" + scan.Name + "-a ~\n"
                + (url.Length > 0 ? "원격: " + url : "원격 주소 없음 (세션 추가 불가)");
            if (rootChanged) msg += "\n\n작업 폴더가 바뀝니다:\n" + s.Root + "\n→ " + scan.Root + "\n(다른 프로젝트도 이 위치에서 찾습니다)";
            msg += "\n\n계속할까요?";
            if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, rootChanged ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;

            txtRegLog.Text = "";
            RegisterExisting(scan.Root, new ProjectDef { Name = scan.Name, Title = title, Count = count, Remote = url });
        }

        // 프로젝트를 설정에 등록하고 실행 탭에 반영한다(폴더는 건드리지 않는다)
        internal void RegisterExisting(string root, ProjectDef def)
        {
            bool existed = s.Find(def.Name) != null;
            if (!string.Equals(root, s.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                s.Root = root;
                txtRoot.Text = root;
            }
            s.UpsertProject(def);
            try { s.Save(); } catch (Exception ex) { RegLog("설정 저장 실패: " + ex.Message); }
            RefreshAfterRegister(def.Name, true);
            RegLog("끝: 프로젝트 '" + def.Name + "' " + (existed ? "정보를 갱신했습니다" : "이(가) 실행 탭에 등록되었습니다") + " (" + def.Count + "개).");
        }

        // 프로젝트 목록을 다시 채우고, 방금 등록한 프로젝트(또는 선택된 것이 없으면 그것)의 세션 목록을 새로 그린다
        void RefreshAfterRegister(string name, bool select)
        {
            bool was = loading;
            loading = true;
            string keep = cmbProject.SelectedItem as string;
            RefreshProjects();
            if (select) cmbProject.SelectedItem = name;
            loading = was;
            if (select || keep == null || keep == name) OnProjectChanged();
        }

        static List<Screen> ScreensInOrder()
        {
            return Screen.AllScreens.OrderBy(x => x.Primary ? 0 : 1).ThenBy(x => x.Bounds.X).ToList();
        }

        // ───────── 상태 저장·복원 ─────────
        void LoadState()
        {
            loading = true;
            txtRoot.Text = s.Root;
            chkClose.Checked = s.CloseAfterRun;
            RefreshProjects();
            RefreshRecent();
            RunOptions o = s.Last != null && s.Find(s.Last.Project) != null ? s.Last : null;
            if (o == null && s.Projects.Count > 0)
            {
                o = new RunOptions { Project = s.Projects[0].Name };
                o.Names.AddRange(ProjectDef.Letters(s.Projects[0].Count));
            }
            if (o != null) ApplyOptions(o);
            else Status("등록된 프로젝트가 없습니다. 클론 탭에서 클론하거나, 등록 탭에서 이미 클론된 폴더를 등록하세요.");
            loading = false;
            UpdateRootLabel();
            UpdateClonePreview();
        }

        void RefreshProjects()
        {
            string keep = cmbProject.SelectedItem as string;
            cmbProject.Items.Clear();
            foreach (var p in s.Projects) cmbProject.Items.Add(p.Name);
            if (keep != null && cmbProject.Items.Contains(keep)) cmbProject.SelectedItem = keep;
            else if (cmbProject.Items.Count > 0) cmbProject.SelectedIndex = 0;
        }

        void RefreshRecent()
        {
            lstRecent.Items.Clear();
            foreach (var r in s.Recent) lstRecent.Items.Add(r.Describe());
        }

        void RebuildSessions(ProjectDef p, ICollection<string> selected)
        {
            flpSessions.SuspendLayout();
            var old = flpSessions.Controls.Cast<Control>().ToList();
            flpSessions.Controls.Clear();
            foreach (var c in old) c.Dispose();
            foreach (string l in ProjectDef.Letters(p.Count))
            {
                var cb = new CheckBox { Text = l.ToUpperInvariant(), Tag = l, Width = 52, Checked = selected == null || selected.Contains(l) };
                flpSessions.Controls.Add(cb);
            }
            // 마지막 칸: 세션(폴더)을 더 클론해 늘리는 버튼. Z(26개)까지만.
            if (p.Count < 26)
            {
                var add = new Button { Text = "+ 추가", AutoSize = true, Enabled = !busy };
                add.Click += delegate { AddSessions(); };
                flpSessions.Controls.Add(add);
            }
            flpSessions.ResumeLayout();
        }

        IEnumerable<CheckBox> SessionBoxes() { return flpSessions.Controls.OfType<CheckBox>(); }

        void OnProjectChanged()
        {
            var p = s.Find(cmbProject.SelectedItem as string);
            if (p != null) RebuildSessions(p, null);
        }

        void SetAllSessions(bool on) { foreach (CheckBox cb in SessionBoxes()) cb.Checked = on; }

        void OnRecentSelected()
        {
            if (loading || lstRecent.SelectedIndex < 0 || lstRecent.SelectedIndex >= s.Recent.Count) return;
            bool was = loading;
            loading = true;
            ApplyOptions(s.Recent[lstRecent.SelectedIndex]);
            loading = was;
        }

        // 옵션 값을 화면에 채운다
        void ApplyOptions(RunOptions o)
        {
            var p = s.Find(o.Project);
            if (p == null) return;
            cmbProject.SelectedItem = p.Name;
            RebuildSessions(p, o.Names);
            cmbMonitor.SelectedIndex = o.Monitor >= 0 && o.Monitor < cmbMonitor.Items.Count ? o.Monitor : 0;
            cmbCols.SelectedIndex = o.Cols >= 0 && o.Cols < cmbCols.Items.Count ? o.Cols : 0;
            cmbModel.Text = string.IsNullOrEmpty(o.Model) ? NoneLabel : o.Model;
            int ei = string.IsNullOrEmpty(o.Effort) ? 0 : cmbEffort.Items.IndexOf(o.Effort);
            cmbEffort.SelectedIndex = ei >= 0 ? ei : 0;
            chkBypass.Checked = o.Bypass;
            chkNoLayout.Checked = o.NoLayout;
            chkTabs.Checked = o.Tabs;
        }

        // 화면의 값을 읽는다
        internal RunOptions Collect()
        {
            var o = new RunOptions
            {
                Project = cmbProject.SelectedItem as string ?? "",
                Monitor = Math.Max(0, cmbMonitor.SelectedIndex),
                Cols = Math.Max(0, cmbCols.SelectedIndex),
                Model = cmbModel.Text.Trim() == NoneLabel ? "" : Regex.Replace(cmbModel.Text, @"[^A-Za-z0-9._\[\]-]", ""),
                Effort = cmbEffort.SelectedIndex <= 0 ? "" : (string)cmbEffort.SelectedItem,
                Bypass = chkBypass.Checked,
                NoLayout = chkNoLayout.Checked,
                Tabs = chkTabs.Checked
            };
            foreach (CheckBox cb in SessionBoxes()) if (cb.Checked) o.Names.Add((string)cb.Tag);
            return o;
        }

        // ───────── 실행 ─────────
        void Execute(RunOptions o, bool dryRun)
        {
            if (busy) return;
            var p = s.Find(o.Project);
            if (p == null) { Status("프로젝트를 선택하세요."); return; }
            if (o.Names.Count == 0) { Status("세션을 하나 이상 선택하세요."); return; }
            string script = Launch.FindScript(s.Root);
            if (script == null) { Status("open-sessions.ps1 을 찾을 수 없습니다. 런처 exe 와 같은 폴더에 두세요."); return; }

            s.CloseAfterRun = chkClose.Checked;
            s.Last = o.Copy();
            if (!dryRun) { s.PushRecent(o); RefreshRecentKeepForm(); }
            try { s.Save(); } catch (Exception ex) { Status("설정 저장 실패: " + ex.Message); }

            var psi = new ProcessStartInfo("powershell.exe", Launch.BuildArgs(script, s.Root, p, o, dryRun))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Launch.ConsoleEncoding(),
                StandardErrorEncoding = Launch.ConsoleEncoding()
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var buf = new StringBuilder();
            object lk = new object();
            DataReceivedEventHandler collect = delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (lk) buf.AppendLine(e.Data); };
            proc.OutputDataReceived += collect;
            proc.ErrorDataReceived += collect;
            proc.Exited += delegate
            {
                proc.WaitForExit();
                string text; lock (lk) text = buf.ToString().TrimEnd();
                try { BeginInvoke(new Action(delegate { Finished(dryRun, text); })); } catch (Exception) { }
            };

            txtLog.Text = dryRun ? "── 미리보기 ──\r\n" : "";
            SetBusy(true, dryRun ? "미리보기 실행 중…" : "여는 중… 창 배치까지 수 초 걸립니다");
            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex) { SetBusy(false, "실행 실패: " + ex.Message); return; }

            if (!dryRun && chkClose.Checked) Close();
        }

        void Finished(bool dryRun, string text)
        {
            if (text.Length > 0) txtLog.AppendText(text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
            SetBusy(false, dryRun ? "미리보기 완료" : "열기 완료");
        }

        void SetBusy(bool on, string status)
        {
            busy = on;
            btnPreview.Enabled = !on;
            btnRun.Enabled = !on;
            foreach (Button b in flpSessions.Controls.OfType<Button>()) b.Enabled = !on;
            Status(status);
        }

        // ───────── 세션 추가 ─────────
        // 세션 하나 = 폴더 하나이므로, 다음 글자 폴더({Name}\{Name}-d …)를 클론하고 프로젝트 개수를 늘린다.
        void AddSessions()
        {
            var p = s.Find(cmbProject.SelectedItem as string);
            if (p == null || busy) return;
            if (p.Count >= 26) { Status("세션은 Z(26개)까지만 만들 수 있습니다."); return; }
            if (string.IsNullOrEmpty(p.Remote))
            {
                MessageBox.Show(this, "'" + p.Name + "' 의 원격 주소가 없어 폴더를 클론할 수 없습니다.\n클론 탭에서 같은 폴더명과 원격 주소로 다시 등록하세요.", Text);
                return;
            }
            int n = AskAddCount(p);
            if (n <= 0) return;

            var letters = ProjectDef.Letters(p.Count + n).Skip(p.Count).ToList();
            var keep = Collect().Names;
            string root = s.Root, name = p.Name, url = p.Remote;
            txtLog.Text = "── 세션 추가: " + name + "  " + string.Join(", ", letters.Select(l => l.ToUpperInvariant()).ToArray()) + " ──\r\n";
            SetBusy(true, "세션 추가 중… (클론)");
            var worker = new Thread(delegate()
            {
                var ok = new List<string>();
                try { ok = Cloner.CloneLetters(root, name, url, letters, RunLog); }
                catch (Exception ex) { RunLog("오류: " + ex.Message); }
                try { BeginInvoke(new Action(delegate { AddSessionsDone(name, letters, ok, keep); })); } catch (Exception) { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        internal void AddSessionsDone(string name, List<string> wanted, List<string> ok, List<string> keep)
        {
            var p = s.Find(name);
            string status;
            if (p != null && ok.Count > 0)
            {
                // 가장 뒤 글자까지 늘린다(중간 글자가 실패해도 열 때 '폴더가 없어 건너뜀'으로 처리된다)
                int newCount = ok.Max(l => l[0] - 'a' + 1);
                if (newCount > p.Count) p.Count = newCount;
                try { s.Save(); } catch (Exception ex) { RunLog("설정 저장 실패: " + ex.Message); }
                string added = string.Join(", ", ok.Select(l => l.ToUpperInvariant()).ToArray());
                RunLog("끝: 세션 " + added + " 추가됨 (" + ok.Count + "/" + wanted.Count + ")");
                status = ok.Count == wanted.Count ? "세션 " + added + " 추가 완료" : "일부만 추가됨 — 결과를 확인하세요";
            }
            else
            {
                RunLog("끝: 추가된 세션이 없습니다.");
                status = "세션 추가 실패 — 결과를 확인하세요";
            }
            // 보고 있는 프로젝트면 체크 상태를 유지한 채 새 세션을 체크해서 보여 준다
            if (p != null && (cmbProject.SelectedItem as string) == name) RebuildSessions(p, keep.Concat(ok).ToList());
            SetBusy(false, status);
        }

        void RunLog(string line)
        {
            try { BeginInvoke(new Action(delegate { txtLog.AppendText(line + "\r\n"); })); } catch (Exception) { }
        }

        // 몇 개를 추가할지 묻는다. 취소하면 0.
        int AskAddCount(ProjectDef p)
        {
            using (var f = new Form())
            {
                f.Text = "세션 추가 — " + p.Name;
                f.Font = Font;
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(440, 160);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;

                var nud = new NumericUpDown { Left = 110, Top = 14, Width = 60, Minimum = 1, Maximum = 26 - p.Count, Value = 1 };
                var preview = new Label { Left = 14, Top = 50, Width = 412, Height = 54, AutoSize = false, ForeColor = Color.DimGray };
                Action update = delegate
                {
                    int n = (int)nud.Value;
                    var ls = ProjectDef.Letters(p.Count + n).Skip(p.Count).ToList();
                    string folders = p.Prefix + ls[0] + (ls.Count > 1 ? " ~ " + p.Prefix + ls[ls.Count - 1] : "");
                    preview.Text = "세션 " + string.Join(", ", ls.Select(l => ProjectDef.SessionName(p.Title, l)).ToArray()) + " 을(를) 추가합니다.\n"
                        + s.Root + "\\" + p.Name + "\\  아래에  " + folders + "  를 클론합니다.";
                };
                nud.ValueChanged += delegate { update(); };
                nud.TextChanged += delegate { update(); };
                var ok = new Button { Text = "추가", Left = 246, Top = 116, Width = 86, Height = 30, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "취소", Left = 340, Top = 116, Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { L("추가할 개수", 14, 14, 90), nud, preview, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                update();
                return f.ShowDialog(this) == DialogResult.OK ? (int)nud.Value : 0;
            }
        }

        void Status(string text) { lblStatus.Text = text; }

        void RefreshRecentKeepForm()
        {
            bool was = loading;
            loading = true;
            RefreshRecent();
            loading = was;
        }

        // ───────── 클론 ─────────
        void UpdateRootLabel() { lblRoot.Text = "작업 폴더: " + s.Root + "   (클론 탭의 '저장 위치'에서 바꿈)"; }

        void UpdateClonePreview()
        {
            string name = txtName.Text.Trim();
            if (name.Length == 0) { lblClonePreview.Text = "원격 주소를 넣으면 폴더명과 세션 이름 접두사가 자동으로 채워집니다."; return; }
            int n = (int)nudCount.Value;
            var letters = ProjectDef.Letters(n);
            string root = txtRoot.Text.Trim().TrimEnd('\\');
            string title = txtTitle.Text.Trim();
            if (title.Length == 0) title = ProjectDef.DefaultTitle(name);   // 비워 두면 클론할 때 이 값을 쓴다
            // 한 줄 = "[세션명] 폴더명". 만들어질 것을 전부 보여 준다.
            var rows = letters.Select(l => "[" + ProjectDef.SessionName(title, l) + "] " + name + "-" + l).ToArray();
            lblClonePreview.Text = "저장 폴더:  " + root + "\\" + name + "\\   (" + n + "개)\r\n" + string.Join("\r\n", rows);
        }

        void CloneLog(string line)
        {
            try { BeginInvoke(new Action(delegate { txtCloneLog.AppendText(line + "\r\n"); })); } catch (Exception) { }
        }

        void StartClone()
        {
            string name = txtName.Text.Trim(), url = txtUrl.Text.Trim(), root = txtRoot.Text.Trim().TrimEnd('\\');
            int count = (int)nudCount.Value;
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9._-]+$")) { MessageBox.Show(this, "폴더명은 영문·숫자·. _ - 만 쓸 수 있습니다.", Text); return; }
            if (url.Length == 0) { MessageBox.Show(this, "원격 주소를 입력하세요.", Text); return; }
            if (root.Length == 0) { MessageBox.Show(this, "저장 위치를 입력하세요.", Text); return; }
            string title = txtTitle.Text.Trim();
            if (title.Length == 0) title = ProjectDef.DefaultTitle(name);

            string msg = root + "\\" + name + "\\ 아래에 " + count + "개를 클론합니다.\n\n원격: " + url + "\n\n계속할까요?";
            if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            s.Root = root;
            btnClone.Enabled = false;
            txtCloneLog.Text = "";
            CloneLog("클론 시작: " + name + " × " + count);
            var worker = new Thread(delegate()
            {
                int ok = 0;
                try { ok = Cloner.CloneAll(root, name, url, count, CloneLog); }
                catch (Exception ex) { CloneLog("오류: " + ex.Message); }
                try { BeginInvoke(new Action(delegate { CloneDone(name, title, count, url, ok); })); } catch (Exception) { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void CloneDone(string name, string title, int count, string url, int ok)
        {
            btnClone.Enabled = true;
            if (ok > 0)
            {
                s.UpsertProject(new ProjectDef { Name = name, Title = title, Count = count, Remote = url });
                try { s.Save(); } catch (Exception) { }
                RefreshAfterRegister(name, false);
                CloneLog("끝: " + ok + "/" + count + "개 사용 가능. 프로젝트 '" + name + "' 이(가) 실행 탭에 등록되었습니다.");
            }
            else CloneLog("끝: 클론된 폴더가 없습니다.");
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string home = Path.Combine(appData, "ClaudeMultiSessionLauncher");
            string selftest = null, cloneSrc = null;
            int startTab = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--home" && i + 1 < args.Length) home = args[++i];
                else if (args[i] == "--selftest" && i + 1 < args.Length) selftest = args[++i];
                else if (args[i] == "--clone-src" && i + 1 < args.Length) cloneSrc = args[++i];
                else if (args[i] == "--tab" && i + 1 < args.Length) { string tab = args[++i]; startTab = tab == "clone" ? 1 : tab == "register" ? 2 : 0; }
            }
            if (selftest != null) return SelfTest.Run(selftest, home, cloneSrc);

            SetProcessDPIAware();   // 4K·배율 화면에서 흐리게 늘어나지 않게
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            // 기본 작업 폴더: 이 도구 폴더의 상위(프로젝트 폴더들이 보통 형제로 있다). 다른 곳이면 클론 탭의 '저장 위치'에서 바꾼다.
            string defaultRoot = exeDir;
            try { defaultRoot = Directory.GetParent(exeDir).FullName.TrimEnd('\\'); } catch (Exception) { }
            Application.Run(new MainForm(Settings.Load(home, defaultRoot), startTab));
            return 0;
        }
    }

    // 화면 없이 핵심 로직을 점검한다: ClaudeMultiSessionLauncher.exe --selftest <결과파일> --home <임시폴더> [--clone-src <저장소 경로>]
    static class SelfTest
    {
        static readonly StringBuilder Out = new StringBuilder();
        static int fails;

        static void Check(string name, bool ok, string detail)
        {
            if (!ok) fails++;
            Out.AppendLine((ok ? "OK   " : "FAIL ") + name + (detail.Length > 0 ? "  — " + detail : ""));
        }

        public static int Run(string outFile, string home, string cloneSrc)
        {
            try
            {
                Check("Letters(6)", string.Join("", ProjectDef.Letters(6).ToArray()) == "abcdef", "");
                Check("Letters(30) 은 26 에서 멈춤", ProjectDef.Letters(30).Count == 26, "");
                Check("DefaultTitle", ProjectDef.DefaultTitle("my-app") == "MY-APP-", ProjectDef.DefaultTitle("my-app"));
                Check("SessionName 대소문자는 접두사를 따름",
                    ProjectDef.SessionName("APP-", "a") == "APP-A" && ProjectDef.SessionName("app-", "a") == "app-a"
                    && ProjectDef.SessionName("App-", "a") == "App-A" && ProjectDef.SessionName("1-", "a") == "1-A", "");
                foreach (var kv in new[] {
                    new[] { "https://github.com/org/my_app.git", "my_app" },
                    new[] { "https://github.com/org/my-app/", "my-app" },
                    new[] { "git@github.com:org/repo.git", "repo" },
                    new[] { "D:\\repos\\my.repo", "my.repo" },
                    new[] { "  ", "" } })
                    Check("NameFromRemote(" + kv[0].Trim() + ")", ProjectDef.NameFromRemote(kv[0]) == kv[1], ProjectDef.NameFromRemote(kv[0]));

                var o = new RunOptions { Project = "demo", Monitor = 1, Cols = 2, Bypass = true, Tabs = false, NoLayout = false };
                o.Names.AddRange(new[] { "a", "c" });
                var o2 = RunOptions.Parse(o.Serialize());
                Check("RunOptions 직렬화 왕복", o2 != null && o2.Serialize() == o.Serialize(), o.Serialize());
                Check("RunOptions.Describe", o.Describe().Contains("A,C") && o.Describe().Contains("모니터 2"), o.Describe());
                Check("모델·effort 기본값은 sonnet 5.5 / high", new RunOptions().Model == "claude-sonnet-5-5" && new RunOptions().Effort == "high", "");
                var oldFmt = RunOptions.Parse("demo|a,c|1|2|1|0|0");   // 모델·effort 가 없던 예전 형식
                Check("예전 설정은 기본 모델·effort 로 읽음", oldFmt != null && oldFmt.Model == "claude-sonnet-5-5" && oldFmt.Effort == "high", oldFmt == null ? "null" : oldFmt.Serialize());
                var none = new RunOptions { Project = "demo", Model = "", Effort = "" }; none.Names.Add("a");
                var none2 = RunOptions.Parse(none.Serialize());
                Check("모델·effort 지정 안 함도 왕복", none2 != null && none2.Model == "" && none2.Effort == "", none.Serialize());

                if (Directory.Exists(home)) Directory.Delete(home, true);
                var s = Settings.Load(home, "D:\\workspace");
                Check("처음 실행: 내장 프로젝트 없음", s.Projects.Count == 0, s.Projects.Count.ToString());
                using (var empty = new MainForm(s, 0))
                    Check("프로젝트가 없어도 화면 생성", empty.Collect().Project == "" && empty.Collect().Names.Count == 0, empty.Collect().Serialize());
                for (int i = 0; i < 7; i++) { var r = new RunOptions { Project = "demo", Cols = i }; r.Names.Add("a"); s.PushRecent(r); }
                var same = new RunOptions { Project = "demo", Cols = 4 }; same.Names.Add("a"); s.PushRecent(same);
                Check("최근 실행은 5개까지", s.Recent.Count == 5, s.Recent.Count.ToString());
                Check("같은 옵션은 맨 위로(중복 없음)", s.Recent[0].Cols == 4 && s.Recent.Count(r => r.Cols == 4) == 1, "");
                s.Last = o; s.UpsertProject(new ProjectDef { Name = "demo", Title = "DEMO-", Count = 2, Remote = "x" }); s.Save();
                var s2 = Settings.Load(home, "D:\\workspace");
                Check("설정 저장·복원", s2.Projects.Count == 1 && s2.Recent.Count == 5 && s2.Last != null && s2.Last.Serialize() == o.Serialize() && s2.Find("demo").Count == 2, "");

                // 세션 추가 후 처리(화면은 띄우지 않고 폼만 만들어 확인): a,b,c 중 a,c 를 고른 상태에서 d,e 추가
                var s3 = Settings.Load(home, "D:\\workspace");
                var last3 = new RunOptions { Project = "demo" }; last3.Names.AddRange(new[] { "a", "c" });
                s3.Find("demo").Count = 3; s3.Last = last3;
                using (var form = new MainForm(s3, 0))
                {
                    form.AddSessionsDone("demo", new List<string> { "d", "e" }, new List<string> { "d", "e" }, new List<string> { "a", "c" });
                    string names = string.Join(",", form.Collect().Names.ToArray());
                    Check("세션 추가 후: 개수 5, 기존 선택 유지 + 새 세션 체크", s3.Find("demo").Count == 5 && names == "a,c,d,e", names);
                    form.AddSessionsDone("demo", new List<string> { "f", "g" }, new List<string> { "g" }, new List<string> { "a" });
                    Check("일부 실패해도 성공한 마지막 글자까지 늘림", s3.Find("demo").Count == 7, s3.Find("demo").Count.ToString());

                    // 클론 탭 자동 채우기: 원격 주소 → 폴더명 → 세션 이름 접두사, 미리보기에 세션 이름 예시
                    form.nudCount.Value = 3;
                    form.txtUrl.Text = "https://github.com/org/my_app.git";
                    Check("원격 주소 → 폴더명·접두사", form.txtName.Text == "my_app" && form.txtTitle.Text == "MY_APP-", form.txtName.Text + " / " + form.txtTitle.Text);
                    Check("미리보기: [세션명] 폴더명 목록", form.lblClonePreview.Text.EndsWith("(3개)\r\n[MY_APP-A] my_app-a\r\n[MY_APP-B] my_app-b\r\n[MY_APP-C] my_app-c"), form.lblClonePreview.Text.Replace("\r\n", " | "));
                    form.txtTitle.Text = "app-";
                    Check("소문자 접두사면 세션명도 소문자", form.lblClonePreview.Text.Contains("[app-a] my_app-a") && form.lblClonePreview.Text.Contains("[app-c] my_app-c"), form.lblClonePreview.Text.Replace("\r\n", " | "));
                    form.txtTitle.Text = "";
                    form.txtTitle.Text = "APP-";
                    form.txtUrl.Text = "https://github.com/org/web.git";
                    Check("직접 고친 접두사는 유지, 예시에 반영", form.txtName.Text == "web" && form.txtTitle.Text == "APP-" && form.lblClonePreview.Text.Contains("[APP-A] web-a"), form.txtName.Text + " / " + form.txtTitle.Text);
                    form.txtName.Text = "mine";
                    form.txtUrl.Text = "https://github.com/x/other.git";
                    Check("직접 고친 폴더명은 원격 주소를 바꿔도 유지", form.txtName.Text == "mine", form.txtName.Text);
                    form.txtName.Text = ""; form.txtTitle.Text = "";
                    form.txtUrl.Text = "https://github.com/x/again.git";
                    Check("칸을 비우면 다시 자동으로 채움", form.txtName.Text == "again" && form.txtTitle.Text == "AGAIN-", form.txtName.Text + " / " + form.txtTitle.Text);
                    form.nudCount.Value = 6;
                    Check("6개면 6줄 전부 표시", form.lblClonePreview.Text.EndsWith("(6개)\r\n[AGAIN-A] again-a\r\n[AGAIN-B] again-b\r\n[AGAIN-C] again-c\r\n[AGAIN-D] again-d\r\n[AGAIN-E] again-e\r\n[AGAIN-F] again-f"),form.lblClonePreview.Text.Replace("\r\n", " | "));

                    // 이미 클론된 프로젝트 등록: 가짜 폴더 trial\{ex-a,ex-b,ex-c,ex-f} + 관련 없는 폴더
                    string exRoot = Path.Combine(home, "existing-root"), exDir = Path.Combine(exRoot, "ex");
                    foreach (string sub in new[] { "ex-a", "ex-b", "ex-c", "ex-f", "ex-f-extra", "other" }) Directory.CreateDirectory(Path.Combine(exDir, sub));
                    var scan = Registrar.Scan(exDir + "\\");
                    Check("등록 스캔: 이름·상위·글자", scan != null && scan.Name == "ex" && scan.Root == exRoot && string.Join("", scan.Letters.ToArray()) == "abcf", scan == null ? "null" : scan.Name + " / " + scan.Root + " / " + string.Join("", scan.Letters.ToArray()));
                    Check("등록 스캔: 갯수는 가장 뒤 글자(f=6), ex-f-extra 는 무시", scan != null && scan.Count == 6, scan == null ? "null" : scan.Count.ToString());
                    Check("등록 스캔: 없는 폴더는 null", Registrar.Scan(Path.Combine(exRoot, "nope")) == null, "");
                    Check("등록 스캔: 세션 폴더가 없으면 갯수 0", Registrar.Scan(Path.Combine(exDir, "other")).Count == 0, "");
                    Check("git 저장소가 아니면 원격 주소는 빈 문자열", Registrar.ReadRemote(Path.Combine(exDir, "ex-a")) == "", "");

                    form.txtRegPath.Text = exDir;
                    Check("등록 탭: 폴더를 고르면 갯수·접두사 자동", form.nudRegCount.Value == 6 && form.txtRegTitle.Text == "EX-", form.nudRegCount.Value + " / " + form.txtRegTitle.Text);
                    Check("등록 미리보기: 없는 폴더 표시", form.lblRegPreview.Text.Contains("[EX-A] ex-a\r\n") && form.lblRegPreview.Text.Contains("[EX-D] ex-d   ← 폴더 없음") && form.lblRegPreview.Text.Contains("[EX-F] ex-f"), form.lblRegPreview.Text.Replace("\r\n", " | "));
                    form.nudRegCount.Value = 4;
                    Check("갯수를 줄이면 갯수 밖 폴더 안내", form.lblRegPreview.Text.Contains("갯수 밖") && !form.lblRegPreview.Text.Contains("[EX-F]"), form.lblRegPreview.Text.Replace("\r\n", " | "));
                    form.RegisterExisting(exRoot, new ProjectDef { Name = "ex", Title = form.txtRegTitle.Text, Count = (int)form.nudRegCount.Value, Remote = "" });
                    Check("등록 후 설정에 반영(개수 4)", s3.Find("ex") != null && s3.Find("ex").Count == 4 && s3.Root == exRoot, s3.Find("ex") == null ? "없음" : s3.Find("ex").Serialize());
                    Check("등록 후 실행 탭이 그 프로젝트를 보여 줌", form.Collect().Project == "ex" && form.Collect().Names.Count == 4, form.Collect().Serialize());
                    form.RegisterExisting(exRoot, new ProjectDef { Name = "ex", Title = "NEW-", Count = 6, Remote = "r" });
                    Check("같은 프로젝트를 다시 등록하면 갱신(중복 없음)", s3.Projects.Count(p0 => p0.Name == "ex") == 1 && s3.Find("ex").Title == "NEW-" && s3.Find("ex").Count == 6, "");
                }
                Check("등록이 설정 파일에 저장됨", Settings.Load(home, "D:\\workspace").Find("ex") != null && Settings.Load(home, "D:\\workspace").Find("ex").Count == 6, "");
                Check("세션 추가가 설정 파일에 저장됨",Settings.Load(home, "D:\\workspace").Find("demo").Count == 7, "");

                var p = s2.Find("demo");
                string args =Launch.BuildArgs("C:\\w\\open-sessions.ps1", "D:\\workspace\\", p, o, true);
                Check("스크립트 인자", args.Contains("-Group \"demo\"") && args.Contains("-Prefix \"demo-\"") && args.Contains("-TitlePrefix \"DEMO-\"")
                    && args.Contains("-Names a,c") && args.Contains("-Root \"D:\\workspace\"") && args.Contains("-Monitor 1") && args.Contains("-Cols 2")
                    && args.Contains("-Bypass") && args.Contains("-DryRun") && !args.Contains("-NoLayout")
                    && args.Contains("-Model \"claude-sonnet-5-5\"") && args.Contains("-Effort \"high\""), args);
                string argsNone = Launch.BuildArgs("C:\\w\\open-sessions.ps1", "D:\\workspace\\", p, none, true);
                Check("지정 안 함이면 -Model/-Effort 를 넘기지 않음", !argsNone.Contains("-Model") && !argsNone.Contains("-Effort"), argsNone);

                if (cloneSrc != null)
                {
                    string root = Path.Combine(home, "clone-root");
                    var log = new List<string>();
                    Action<string> add = delegate(string l) { lock (log) log.Add(l); };
                    int ok1 = Cloner.CloneAll(root, "demo", cloneSrc, 3, add);
                    bool all = ok1 == 3;
                    for (char c = 'a'; c <= 'c'; c++) all &= Directory.Exists(Path.Combine(root, "demo", "demo-" + c, ".git"));
                    Check("클론 3개 (폴더 demo\\demo-a~c)", all, "성공 " + ok1);
                    int before = log.Count;
                    int ok2 = Cloner.CloneAll(root, "demo", cloneSrc, 3, add);
                    Check("다시 실행하면 건너뜀", ok2 == 3 && log.Skip(before).Count(l => l.Contains("건너뜁니다")) == 3, "성공 " + ok2);
                    var added = Cloner.CloneLetters(root, "demo", cloneSrc, new[] { "d", "e" }, add);
                    bool de = string.Join("", added.ToArray()) == "de";
                    for (char c = 'd'; c <= 'e'; c++) de &= Directory.Exists(Path.Combine(root, "demo", "demo-" + c, ".git"));
                    Check("세션 추가: d·e 만 클론", de, string.Join(",", added.ToArray()));
                    string remote = Registrar.ReadRemote(Path.Combine(root, "demo", "demo-a"));
                    Check("클론된 폴더의 원격 주소 읽기", remote.Length > 0 && Path.GetFileName(remote.TrimEnd('/', '\\')) == Path.GetFileName(cloneSrc.TrimEnd('\\')), remote);
                    var rs = Registrar.Scan(Path.Combine(root, "demo"));
                    Check("클론 결과를 등록 스캔이 5개(a~e)로 인식", rs != null && rs.Count == 5 && rs.Root == root, rs == null ? "null" : rs.Count.ToString());
                    int ok3 = Cloner.CloneAll(root, "demo2", Path.Combine(home, "no-such-repo"), 1, add);
                    Check("잘못된 주소는 실패로 집계", ok3 == 0 && log.Any(l => l.Contains("실패")), "성공 " + ok3);
                }
            }
            catch (Exception ex) { fails++; Out.AppendLine("FAIL 예외: " + ex); }
            Out.AppendLine(fails == 0 ? "== 전부 통과 ==" : "== 실패 " + fails + "건 ==");
            File.WriteAllText(outFile, Out.ToString(), new UTF8Encoding(true));
            return fails == 0 ? 0 : 1;
        }
    }
}
