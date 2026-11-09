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
    // 화면 격자의 칸 하나(행·열은 1부터)
    class GridCell
    {
        public int Row, Col;
        public GridCell(int row, int col) { Row = row; Col = col; }
    }

    // 창 배치 격자 계산. window-layout.ps1 의 Get-GridRects 와 같은 규칙이다.
    static class GridLayout
    {
        public const int MaxSize = 8;   // 화면에서 고를 수 있는 최대 행·열 수

        // 열 수 자동: 1→1, 2→2, 3→3, 4→2, 5·6→3, 7·8→4, 그 외 ⌈√n⌉
        public static int AutoCols(int count)
        {
            switch (count)
            {
                case 1: return 1;
                case 2: return 2;
                case 3: return 3;
                case 4: return 2;
                case 5: case 6: return 3;
                case 7: case 8: return 4;
                default: return (int)Math.Ceiling(Math.Sqrt(Math.Max(1, count)));
            }
        }

        // 창 count 개와 지정한 칸들로 실제 행·열 수를 정한다(cols·rows 가 0 이면 자동).
        // 지정한 칸이 격자 밖이거나 칸이 모자라면 격자를 키운다.
        public static void Resolve(int count, int cols, int rows, IEnumerable<GridCell> fixedCells, out int outRows, out int outCols)
        {
            count = Math.Max(1, count);
            int maxR = 0, maxC = 0, nFixed = 0;
            foreach (var c in fixedCells) { maxR = Math.Max(maxR, c.Row); maxC = Math.Max(maxC, c.Col); nFixed++; }
            if (cols <= 0) cols = rows > 0 ? (int)Math.Ceiling(count / (double)rows) : AutoCols(count);
            if (nFixed == 0 && rows <= 0) cols = Math.Min(cols, count);
            cols = Math.Max(cols, maxC);
            if (rows <= 0) rows = (int)Math.Ceiling(count / (double)cols);
            rows = Math.Max(rows, maxR);
            while (rows * cols < count) rows++;
            outRows = rows;
            outCols = cols;
        }
    }

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
        public int Rows;        // 0 = 자동
        // 세션 글자(a, b …) → 배치할 칸. 여기 없는 세션은 빈 칸을 순서대로 채운다.
        public Dictionary<string, GridCell> Cells = new Dictionary<string, GridCell>();
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
                Bypass ? "1" : "0", NoLayout ? "1" : "0", Tabs ? "1" : "0", Model ?? "", Effort ?? "",
                Rows.ToString(), CellsText() });
        }

        // "a:1:2,b:2:3" (글자:행:열). open-sessions.ps1 의 -Cells 와 같은 형식이다.
        public string CellsText() { return CellsText(Cells); }

        public static string CellsText(IDictionary<string, GridCell> cells)
        {
            return string.Join(",", cells.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + ":" + kv.Value.Row + ":" + kv.Value.Col).ToArray());
        }

        public static Dictionary<string, GridCell> ParseCells(string s)
        {
            var map = new Dictionary<string, GridCell>();
            foreach (string item in (s ?? "").Split(','))
            {
                var m = Regex.Match(item, @"^([a-z]):(\d{1,2}):(\d{1,2})$");
                if (!m.Success) continue;
                int r = int.Parse(m.Groups[2].Value), c = int.Parse(m.Groups[3].Value);
                if (r >= 1 && c >= 1) map[m.Groups[1].Value] = new GridCell(r, c);
            }
            return map;
        }

        public static RunOptions Parse(string s)
        {
            string[] p = s.Split('|');
            int m, c;
            if (p.Length < 7 || p[0].Length == 0 || !int.TryParse(p[2], out m) || !int.TryParse(p[3], out c)) return null;
            var o = new RunOptions { Project = p[0], Monitor = m, Cols = c, Bypass = p[4] == "1", NoLayout = p[5] == "1", Tabs = p[6] == "1" };
            // 모델·effort 가 없던 예전 설정은 기본값(sonnet 5.5 / high)을 쓴다
            if (p.Length >= 9) { o.Model = p[7]; o.Effort = p[8]; }
            // 행 수·칸 지정이 없던 예전 설정은 자동 배치
            if (p.Length >= 11) { int rows; if (int.TryParse(p[9], out rows)) o.Rows = Math.Max(0, rows); o.Cells = ParseCells(p[10]); }
            foreach (string n in p[1].Split(',')) if (n.Length > 0) o.Names.Add(n);
            return o;
        }

        public RunOptions Copy() { return Parse(Serialize()); }

        // 목록에 보여 줄 한 줄 설명
        public string Describe()
        {
            var parts = new List<string>();
            parts.Add(Monitor == 0 ? "주 모니터" : "모니터 " + (Monitor + 1));
            if (NoLayout) parts.Add("배치 안 함");
            else if (Cols == 0 && Rows == 0) parts.Add("자동 배치");
            else parts.Add(((Rows > 0 ? Rows + "행 " : "") + (Cols > 0 ? Cols + "열" : "")).Trim());
            if (!NoLayout && !Tabs && Cells.Count > 0) parts.Add("셀 지정 " + Cells.Count + "개");
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
            if (o.Rows > 0) sb.Append(" -Rows ").Append(o.Rows);
            // 실행할 세션의 칸만 넘긴다
            var cellArgs = o.Cells.Where(kv => o.Names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            if (cellArgs.Count > 0) sb.Append(" -Cells ").Append(RunOptions.CellsText(cellArgs));
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

    // 화면 색·글꼴·컨트롤 모양을 한곳에서 정한다
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(0xF3, 0xF4, 0xF6);        // 창 배경
        public static readonly Color CardBg = Color.White;                          // 카드 배경
        public static readonly Color Border = Color.FromArgb(0xD9, 0xDC, 0xE1);    // 테두리
        public static readonly Color Text = Color.FromArgb(0x1F, 0x29, 0x37);      // 본문 글자
        public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);     // 보조 글자
        public static readonly Color Accent = Color.FromArgb(0xC2, 0x60, 0x3F);    // 강조색(주 버튼·선택 탭)
        public static readonly Color AccentDark = Color.FromArgb(0xA6, 0x4C, 0x30);
        public static readonly Color LogBg = Color.FromArgb(0x1E, 0x22, 0x2B);
        public static readonly Color LogText = Color.FromArgb(0xE5, 0xE7, 0xEB);
        public static readonly Font BaseFont = new Font("Segoe UI", 9.5F);
        public static readonly Font BoldFont = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        static readonly Color Hover = Color.FromArgb(0xF3, 0xF4, 0xF6);
        static readonly Color Pressed = Color.FromArgb(0xE5, 0xE7, 0xEB);

        // 플랫 버튼. primary 면 강조색으로 채운다. 비활성일 때는 회색으로 보인다.
        public static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderSize = 1;
            if (primary) b.Font = BoldFont;
            Action apply = delegate
            {
                if (b.Enabled)
                {
                    b.BackColor = primary ? Accent : Color.White;
                    b.ForeColor = primary ? Color.White : Text;
                    b.FlatAppearance.BorderColor = primary ? Accent : Border;
                    b.FlatAppearance.MouseOverBackColor = primary ? AccentDark : Hover;
                    b.FlatAppearance.MouseDownBackColor = primary ? AccentDark : Pressed;
                    b.Cursor = Cursors.Hand;
                }
                else
                {
                    b.BackColor = Hover;
                    b.ForeColor = Color.FromArgb(0x9C, 0xA3, 0xAF);
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = Hover;
                    b.FlatAppearance.MouseDownBackColor = Hover;
                    b.Cursor = Cursors.Default;
                }
            };
            b.EnabledChanged += delegate { apply(); };
            apply();
        }

        // 결과 창: 어두운 바탕에 고정폭 글꼴
        public static void StyleLog(TextBox t)
        {
            t.BackColor = LogBg;
            t.ForeColor = LogText;
            t.Font = new Font("Consolas", 9.5F);
            t.BorderStyle = BorderStyle.FixedSingle;
        }

        public static void StyleDialog(Form f)
        {
            f.BackColor = Bg;
            f.ForeColor = Text;
        }

        // 입력 칸(TextBox·NumericUpDown)의 입체 테두리를 1px 평면 테두리로 바꾼다
        public static void FlattenInputs(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var tb = c as TextBox;
                var nud = c as NumericUpDown;
                if (tb != null && tb.BorderStyle == BorderStyle.Fixed3D) tb.BorderStyle = BorderStyle.FixedSingle;
                if (nud != null) nud.BorderStyle = BorderStyle.FixedSingle;
                FlattenInputs(c);
            }
        }

        // 미리보기 칸: 옅은 바탕의 읽기 전용 상자
        public static void StylePreview(TextBox t)
        {
            t.BackColor = Color.FromArgb(0xF9, 0xFA, 0xFB);
            t.ForeColor = Muted;
            t.BorderStyle = BorderStyle.FixedSingle;
        }
    }

    // 테두리가 있는 흰 상자
    class Card : Panel
    {
        public Card()
        {
            BackColor = Ui.CardBg;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Ui.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // 위쪽 탭 바의 탭 하나: 글자 + 고르면 강조색 밑줄
    class NavTab : Control
    {
        bool selected, hover;

        public NavTab(string text)
        {
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
            BackColor = Color.White;
        }

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(hover && !selected ? Color.FromArgb(0xF9, 0xFA, 0xFB) : BackColor);
            Color fg = selected ? Ui.Accent : (hover ? Ui.Text : Ui.Muted);
            TextRenderer.DrawText(e.Graphics, Text, selected ? Ui.BoldFont : Ui.BaseFont, new Rectangle(0, 0, Width, Height - 3), fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (selected) using (var br = new SolidBrush(Ui.Accent)) e.Graphics.FillRectangle(br, 10, Height - 3, Width - 20, 3);
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
        Panel[] pages;       // 탭마다 한 장씩(실행·클론·등록)
        NavTab[] navs;
        readonly ToolTip tip = new ToolTip();

        // 실행 탭
        ComboBox cmbProject, cmbMonitor, cmbCols, cmbRows, cmbModel, cmbEffort;
        Dictionary<string, GridCell> cellMap = new Dictionary<string, GridCell>();   // 셀 지정(세션 글자 → 칸)
        Label lblCells;
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
            Font = Ui.BaseFont;
            BackColor = Ui.Bg;
            ForeColor = Ui.Text;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 812);
            MinimumSize = new Size(736, 782);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            LoadState();
            Load += delegate
            {
                foreach (var kv in pendingAnchors) kv.Key.Anchor = kv.Value;
                pendingAnchors.Clear();
                // 등록된 프로젝트가 없으면 실행할 것이 없으므로 클론 탭으로 연다
                int tab = s.Projects.Count == 0 ? 1 : this.startTab;
                if (tab > 0) ShowPage(tab);
            };
            FormClosing += delegate { try { s.Last = Collect(); s.Save(); } catch (Exception) { } };
        }

        T Anchored<T>(T c, AnchorStyles a) where T : Control { pendingAnchors.Add(new KeyValuePair<Control, AnchorStyles>(c, a)); return c; }

        // ───────── 화면 구성 ─────────
        // 위: 탭 바(NavTab 셋) / 아래: 탭마다 한 장씩 쓰는 페이지(Panel). 페이지는 모두 Fill 로 겹쳐 두고
        // 고른 페이지만 맨 앞으로 가져온다(숨기면 크기 계산이 어긋나 Anchor 가 틀어진다).
        void BuildUi()
        {
            pages = new Panel[3];
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };
                Controls.Add(pages[i]);
            }
            var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.White };
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Ui.Border)) e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            string[] titles = { "실행", "클론", "등록" };
            navs = new NavTab[titles.Length];
            for (int i = 0; i < titles.Length; i++)
            {
                int index = i;
                navs[i] = new NavTab(titles[i]) { Left = 12 + i * 92, Top = 0, Width = 92, Height = 46 };
                navs[i].Click += delegate { ShowPage(index); };
                header.Controls.Add(navs[i]);
            }
            Controls.Add(header);   // 나중에 넣은 것이 먼저 자리를 잡으므로 헤더가 맨 위에 붙는다
            BuildRunTab(pages[0]);
            BuildCloneTab(pages[1]);
            BuildRegisterTab(pages[2]);
            Ui.FlattenInputs(this);
            ShowPage(0);
        }

        void ShowPage(int index)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                navs[i].Selected = i == index;
                pages[i].Enabled = i == index;   // 가려진 페이지의 컨트롤이 Tab 키로 잡히지 않게
            }
            pages[index].BringToFront();
        }

        static Label L(string text, int x, int y, int w) { return new Label { Text = text, Left = x, Top = y + 4, Width = w, AutoSize = false }; }

        // 흰 카드 하나(제목 + 테두리). 안의 컨트롤은 카드 왼쪽 위를 기준으로 놓는다.
        Card AddCard(Control page, string title, int top, int height)
        {
            var c = new Card { Left = 16, Top = top, Width = 688, Height = height };
            c.Controls.Add(new Label { Text = title, Left = 16, Top = 8, Width = 400, Height = 22, AutoSize = false, Font = Ui.BoldFont, ForeColor = Ui.Text });
            Anchored(c, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            page.Controls.Add(c);
            return c;
        }

        void BuildRunTab(Control t)
        {
            // ── 세션 ──
            var c1 = AddCard(t, "세션", 12, 174);
            c1.Controls.Add(L("프로젝트", 16, 34, 80));
            cmbProject = new ComboBox { Left = 100, Top = 34, Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbProject.SelectedIndexChanged += delegate { if (!loading) OnProjectChanged(); };
            c1.Controls.Add(cmbProject);

            c1.Controls.Add(L("세션", 16, 72, 80));
            flpSessions = new FlowLayoutPanel { Left = 100, Top = 70, Width = 470, Height = 90, WrapContents = true, AutoScroll = true };
            c1.Controls.Add(flpSessions);
            var btnAll = new Button { Text = "전체 선택", Left = 584, Top = 70, Width = 88, Height = 28 };
            var btnNone = new Button { Text = "전체 해제", Left = 584, Top = 102, Width = 88, Height = 28 };
            Ui.StyleButton(btnAll, false);
            Ui.StyleButton(btnNone, false);
            btnAll.Click += delegate { SetAllSessions(true); };
            btnNone.Click += delegate { SetAllSessions(false); };
            c1.Controls.Add(btnAll);
            c1.Controls.Add(btnNone);

            // ── 창 배치 ──
            var c2 = AddCard(t, "창 배치", 194, 112);
            c2.Controls.Add(L("모니터", 16, 34, 80));
            cmbMonitor = new ComboBox { Left = 100, Top = 34, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
            var screens = ScreensInOrder();
            for (int i = 0; i < screens.Count; i++)
                cmbMonitor.Items.Add((i == 0 ? "주 모니터" : "모니터 " + (i + 1)) + " (" + screens[i].Bounds.Width + "x" + screens[i].Bounds.Height + ")");
            c2.Controls.Add(cmbMonitor);
            c2.Controls.Add(L("열 수", 366, 34, 50));
            cmbCols = new ComboBox { Left = 420, Top = 34, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbCols.Items.Add("자동");
            for (int c = 1; c <= GridLayout.MaxSize; c++) cmbCols.Items.Add(c + "열");
            c2.Controls.Add(cmbCols);
            c2.Controls.Add(L("행 수", 526, 34, 44));
            cmbRows = new ComboBox { Left = 574, Top = 34, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbRows.Items.Add("자동");
            for (int r = 1; r <= GridLayout.MaxSize; r++) cmbRows.Items.Add(r + "행");
            cmbRows.SelectedIndex = 0;
            c2.Controls.Add(cmbRows);

            // 셀 지정: 세션마다 놓을 칸(예: 1행 2열에 A, 2행 3열에 B)을 고른다
            c2.Controls.Add(L("셀 배치", 16, 72, 80));
            var btnCells = new Button { Text = "셀 지정…", Left = 100, Top = 70, Width = 110, Height = 30 };
            Ui.StyleButton(btnCells, false);
            btnCells.Click += delegate { AskCells(); };
            c2.Controls.Add(btnCells);
            lblCells = new Label { Left = 222, Top = 76, Width = 442, AutoSize = false, ForeColor = Ui.Muted };
            c2.Controls.Add(lblCells);

            // ── Claude 옵션 ──
            // 모델은 직접 입력도 가능(별칭 sonnet/opus 나 전체 모델 ID). 비우거나 "(지정 안 함)" 이면 --model 을 붙이지 않는다.
            var c3 = AddCard(t, "Claude 옵션", 314, 134);
            c3.Controls.Add(L("모델", 16, 34, 80));
            cmbModel = new ComboBox { Left = 100, Top = 34, Width = 250, DropDownStyle = ComboBoxStyle.DropDown };
            cmbModel.Items.AddRange(new object[] { NoneLabel, "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-haiku-5-5", "sonnet", "opus", "haiku", "fable" });
            cmbModel.Text = RunOptions.DefaultModel;
            c3.Controls.Add(cmbModel);
            c3.Controls.Add(L("Effort", 366, 34, 50));
            cmbEffort = new ComboBox { Left = 420, Top = 34, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbEffort.Items.AddRange(new object[] { NoneLabel, "low", "medium", "high", "xhigh", "max" });
            cmbEffort.SelectedItem = RunOptions.DefaultEffort;
            c3.Controls.Add(cmbEffort);

            chkBypass = new CheckBox { Text = "권한 확인 건너뛰기 (--dangerously-skip-permissions)", Left = 100, Top = 72, Width = 370 };
            chkNoLayout = new CheckBox { Text = "창 위치·크기 배치 안 함", Left = 100, Top = 98, Width = 370 };
            chkTabs = new CheckBox { Text = "한 창의 탭으로 열기", Left = 480, Top = 72, Width = 190 };
            chkClose = new CheckBox { Text = "열기 후 런처 닫기", Left = 480, Top = 98, Width = 190 };
            tip.SetToolTip(chkNoLayout, "Windows Terminal 이 정하는 기본 위치·크기로 엽니다.");
            tip.SetToolTip(chkTabs, "한 창의 탭으로 열면 창 배치(열·행·셀 지정)는 적용되지 않습니다.");
            c3.Controls.AddRange(new Control[] { chkBypass, chkNoLayout, chkTabs, chkClose });

            // ── 실행 ──
            btnPreview = new Button { Text = "미리보기", Left = 16, Top = 456, Width = 120, Height = 38 };
            btnRun = new Button { Text = "열기", Left = 146, Top = 456, Width = 160, Height = 38 };
            Ui.StyleButton(btnPreview, false);
            Ui.StyleButton(btnRun, true);
            btnPreview.Click += delegate { Execute(Collect(), true); };
            btnRun.Click += delegate { Execute(Collect(), false); };
            t.Controls.Add(btnPreview);
            t.Controls.Add(btnRun);
            lblStatus = new Label { Left = 320, Top = 467, Width = 384, AutoSize = false, ForeColor = Ui.Muted };
            t.Controls.Add(lblStatus);

            t.Controls.Add(new Label { Text = "최근 실행 — 클릭하면 옵션을 불러오고, 더블클릭하면 바로 실행합니다", Left = 16, Top = 504, Width = 688, AutoSize = false, ForeColor = Ui.Muted });
            lstRecent = new ListBox { Left = 16, Top = 526, Width = 688, Height = 66, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
            Anchored(lstRecent, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lstRecent.SelectedIndexChanged += delegate { OnRecentSelected(); };
            lstRecent.DoubleClick += delegate { if (lstRecent.SelectedIndex >= 0) Execute(Collect(), false); };
            t.Controls.Add(lstRecent);

            t.Controls.Add(new Label { Text = "결과", Left = 16, Top = 602, Width = 44, AutoSize = false, Font = Ui.BoldFont });
            lblRoot = new Label { Left = 64, Top = 602, Width = 640, AutoSize = false, ForeColor = Ui.Muted };
            t.Controls.Add(lblRoot);
            txtLog = new TextBox { Left = 16, Top = 624, Width = 688, Height = 130, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            Ui.StyleLog(txtLog);
            Anchored(txtLog, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom);
            t.Controls.Add(txtLog);
        }

        void BuildCloneTab(Control t)
        {
            var c = AddCard(t, "원격 저장소 클론", 12, 346);
            c.Controls.Add(L("폴더명", 16, 34, 90));
            txtName = new TextBox { Left = 112, Top = 34, Width = 220 };
            c.Controls.Add(txtName);
            c.Controls.Add(L("원격 주소", 16, 70, 90));
            txtUrl = new TextBox { Left = 112, Top = 70, Width = 450 };
            Anchored(txtUrl, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(txtUrl);
            // 저장소 주소를 찾으러 GitHub 를 기본 브라우저로 연다
            var btnGitHub = new Button { Text = "GitHub", Left = 574, Top = 68, Width = 90, Height = 28 };
            Ui.StyleButton(btnGitHub, false);
            Anchored(btnGitHub, AnchorStyles.Top | AnchorStyles.Right);
            btnGitHub.Click += delegate
            {
                try { Process.Start("https://github.com"); }
                catch (Exception ex) { MessageBox.Show(this, "브라우저를 열 수 없습니다: " + ex.Message, Text); }
            };
            c.Controls.Add(btnGitHub);
            c.Controls.Add(L("갯수", 16, 106, 90));
            nudCount = new NumericUpDown { Left = 112, Top = 106, Width = 60, Minimum = 1, Maximum = 26, Value = 6 };
            c.Controls.Add(nudCount);
            c.Controls.Add(L("세션 이름 접두사", 200, 106, 120));
            txtTitle = new TextBox { Left = 328, Top = 106, Width = 336 };   // 원격 주소 칸과 오른쪽 끝을 맞춘다
            Anchored(txtTitle, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(txtTitle);
            c.Controls.Add(L("저장 위치", 16, 142, 90));
            txtRoot = new TextBox { Left = 112, Top = 142, Width = 450 };
            Anchored(txtRoot, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(txtRoot);
            var btnBrowse = new Button { Text = "찾아보기", Left = 574, Top = 140, Width = 90, Height = 28 };
            Ui.StyleButton(btnBrowse, false);
            Anchored(btnBrowse, AnchorStyles.Top | AnchorStyles.Right);
            btnBrowse.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                {
                    if (Directory.Exists(txtRoot.Text)) d.SelectedPath = txtRoot.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) txtRoot.Text = d.SelectedPath;
                }
            };
            c.Controls.Add(btnBrowse);

            // 미리보기: 저장 폴더 한 줄 + 만들어질 "[세션명] 폴더명" 전부. 줄이 많으면 스크롤된다.
            lblClonePreview = new TextBox
            {
                Left = 112, Top = 182, Width = 552, Height = 150, Multiline = true, ReadOnly = true, WordWrap = false,
                ScrollBars = ScrollBars.Vertical, TabStop = false
            };
            Ui.StylePreview(lblClonePreview);
            Anchored(lblClonePreview, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(lblClonePreview);
            btnClone = new Button { Text = "클론", Left = 16, Top = 370, Width = 140, Height = 38 };
            Ui.StyleButton(btnClone, true);
            btnClone.Click += delegate { StartClone(); };
            t.Controls.Add(btnClone);

            txtCloneLog = new TextBox { Left = 16, Top = 418, Width = 688, Height = 304, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            Ui.StyleLog(txtCloneLog);
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
        void BuildRegisterTab(Control t)
        {
            var c = AddCard(t, "이미 클론된 프로젝트 등록", 12, 346);
            c.Controls.Add(L("프로젝트 폴더", 16, 34, 90));
            txtRegPath = new TextBox { Left = 112, Top = 34, Width = 450 };
            Anchored(txtRegPath, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(txtRegPath);
            var btnBrowse = new Button { Text = "찾아보기", Left = 574, Top = 32, Width = 90, Height = 28 };
            Ui.StyleButton(btnBrowse, false);
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
            c.Controls.Add(btnBrowse);

            c.Controls.Add(L("원격 주소", 16, 70, 90));
            txtRegUrl = new TextBox { Left = 112, Top = 70, Width = 552 };
            Anchored(txtRegUrl, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(txtRegUrl);

            c.Controls.Add(L("갯수", 16, 106, 90));
            nudRegCount = new NumericUpDown { Left = 112, Top = 106, Width = 60, Minimum = 1, Maximum = 26, Value = 6 };
            c.Controls.Add(nudRegCount);
            c.Controls.Add(L("세션 이름 접두사", 200, 106, 120));
            txtRegTitle = new TextBox { Left = 328, Top = 106, Width = 336 };
            Anchored(txtRegTitle, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(txtRegTitle);

            c.Controls.Add(new Label
            {
                Text = "폴더를 고르면 갯수·원격 주소를 폴더에서 읽어 채웁니다. 갯수는 고쳐도 됩니다. 원격 주소는 세션 추가(+ 추가)에만 필요합니다.",
                Left = 112, Top = 138, Width = 552, Height = 34, AutoSize = false, ForeColor = Ui.Muted
            });

            lblRegPreview = new TextBox
            {
                Left = 112, Top = 176, Width = 552, Height = 156, Multiline = true, ReadOnly = true, WordWrap = false,
                ScrollBars = ScrollBars.Vertical, TabStop = false
            };
            Ui.StylePreview(lblRegPreview);
            Anchored(lblRegPreview, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            c.Controls.Add(lblRegPreview);
            btnRegister = new Button { Text = "등록", Left = 16, Top = 370, Width = 140, Height = 38 };
            Ui.StyleButton(btnRegister, true);
            btnRegister.Click += delegate { StartRegister(); };
            t.Controls.Add(btnRegister);

            txtRegLog = new TextBox { Left = 16, Top = 418, Width = 688, Height = 304, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            Ui.StyleLog(txtRegLog);
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
                cb.CheckedChanged += delegate { UpdateCellSummary(); };
                flpSessions.Controls.Add(cb);
            }
            // 마지막 칸: 세션(폴더)을 더 클론해 늘리는 버튼. Z(26개)까지만.
            if (p.Count < 26)
            {
                var add = new Button { Text = "+ 추가", AutoSize = true, Enabled = !busy };
                Ui.StyleButton(add, false);
                add.Click += delegate { AddSessions(); };
                flpSessions.Controls.Add(add);
            }
            flpSessions.ResumeLayout();
            UpdateCellSummary();
        }

        IEnumerable<CheckBox> SessionBoxes() { return flpSessions.Controls.OfType<CheckBox>(); }

        void OnProjectChanged()
        {
            var p = s.Find(cmbProject.SelectedItem as string);
            cellMap = new Dictionary<string, GridCell>();   // 다른 프로젝트의 칸 지정은 이어받지 않는다
            if (p != null) RebuildSessions(p, null);
        }

        void SetAllSessions(bool on) { foreach (CheckBox cb in SessionBoxes()) cb.Checked = on; }

        // 체크된 세션 중 칸이 지정된 것만(세션 글자 → 칸)
        Dictionary<string, GridCell> SelectedCells()
        {
            var r = new Dictionary<string, GridCell>();
            foreach (CheckBox cb in SessionBoxes())
            {
                GridCell g;
                string l = (string)cb.Tag;
                if (cb.Checked && cellMap.TryGetValue(l, out g)) r[l] = g;
            }
            return r;
        }

        void UpdateCellSummary()
        {
            if (lblCells == null) return;
            var cells = SelectedCells();
            lblCells.Text = cells.Count == 0
                ? "지정 안 함 — 선택한 세션을 빈 칸에 순서대로 놓습니다"
                : string.Join(", ", cells.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => kv.Key.ToUpperInvariant() + "→" + kv.Value.Row + "행" + kv.Value.Col + "열").ToArray());
        }

        // 세션마다 놓을 칸을 고르는 창. 확인하면 행 수·열 수·칸 지정을 실행 탭에 반영한다.
        void AskCells()
        {
            var names = SessionBoxes().Where(cb => cb.Checked).Select(cb => (string)cb.Tag).ToList();
            if (names.Count == 0) { Status("셀을 지정하려면 세션을 먼저 선택하세요."); return; }
            var cur = SelectedCells();

            int rows0, cols0;
            GridLayout.Resolve(names.Count, Math.Max(0, cmbCols.SelectedIndex), Math.Max(0, cmbRows.SelectedIndex), cur.Values, out rows0, out cols0);

            using (var f = new Form())
            {
                f.Text = "셀 지정 — 세션을 놓을 칸 고르기";
                f.Font = Font;
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(680, 470);
                Ui.StyleDialog(f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;

                var nudRows = new NumericUpDown { Left = 70, Top = 14, Width = 60, Minimum = 1, Maximum = GridLayout.MaxSize, Value = Math.Min(GridLayout.MaxSize, rows0) };
                var nudCols = new NumericUpDown { Left = 206, Top = 14, Width = 60, Minimum = 1, Maximum = GridLayout.MaxSize, Value = Math.Min(GridLayout.MaxSize, cols0) };
                var hint = new Label
                {
                    Left = 14, Top = 48, Width = 652, Height = 36, AutoSize = false, ForeColor = Ui.Muted,
                    Text = "칸마다 놓을 세션을 고르세요. 칸에 고르지 않은 세션은 비어 있는 칸을 위→아래, 왼→오른 순서로 채웁니다. 같은 세션을 두 칸에 둘 수는 없습니다."
                };
                var pnl = new Panel { Left = 14, Top = 88, Width = 652, Height = 330, AutoScroll = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };

                // "행,열" → 세션 글자. 다시 그리기 전에 화면의 선택을 여기에 모은다.
                var chosen = new Dictionary<string, string>();
                foreach (var kv in cur) chosen[kv.Value.Row + "," + kv.Value.Col] = kv.Key;

                Action collect = delegate
                {
                    foreach (ComboBox cb in pnl.Controls.OfType<ComboBox>())
                    {
                        int[] pos = (int[])cb.Tag;
                        string key = pos[0] + "," + pos[1];
                        if (cb.SelectedIndex > 0) chosen[key] = names[cb.SelectedIndex - 1]; else chosen.Remove(key);
                    }
                };
                Action build = delegate
                {
                    collect();
                    pnl.SuspendLayout();
                    var old = pnl.Controls.Cast<Control>().ToList();
                    pnl.Controls.Clear();
                    foreach (var c in old) c.Dispose();
                    for (int r = 1; r <= (int)nudRows.Value; r++)
                        for (int c = 1; c <= (int)nudCols.Value; c++)
                        {
                            int x = 8 + (c - 1) * 78, y = 6 + (r - 1) * 50;
                            pnl.Controls.Add(new Label { Text = r + "행 " + c + "열", Left = x, Top = y, Width = 72, Height = 16, AutoSize = false, ForeColor = Ui.Muted });
                            var combo = new ComboBox { Left = x, Top = y + 18, Width = 72, DropDownStyle = ComboBoxStyle.DropDownList, Tag = new[] { r, c } };
                            combo.Items.Add("(자동)");
                            foreach (string n in names) combo.Items.Add(n.ToUpperInvariant());
                            string letter;
                            combo.SelectedIndex = chosen.TryGetValue(r + "," + c, out letter) ? names.IndexOf(letter) + 1 : 0;
                            // 한 세션은 한 칸에만: 다른 칸에 있던 같은 세션은 (자동)으로 돌린다
                            ComboBox self = combo;
                            self.SelectionChangeCommitted += delegate
                            {
                                if (self.SelectedIndex <= 0) return;
                                foreach (ComboBox other in pnl.Controls.OfType<ComboBox>())
                                    if (other != self && other.SelectedIndex == self.SelectedIndex) other.SelectedIndex = 0;
                            };
                            pnl.Controls.Add(combo);
                        }
                    pnl.ResumeLayout();
                };
                nudRows.ValueChanged += delegate { build(); };
                nudCols.ValueChanged += delegate { build(); };

                var clear = new Button { Text = "모두 비우기", Left = 14, Top = 428, Width = 110, Height = 30 };
                clear.Click += delegate
                {
                    chosen.Clear();
                    foreach (ComboBox cb in pnl.Controls.OfType<ComboBox>()) cb.SelectedIndex = 0;
                };
                var ok = new Button { Text = "확인", Left = 484, Top = 428, Width = 86, Height = 30, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "취소", Left = 580, Top = 428, Width = 86, Height = 30, DialogResult = DialogResult.Cancel };
                Ui.StyleButton(clear, false);
                Ui.StyleButton(ok, true);
                Ui.StyleButton(cancel, false);
                f.Controls.AddRange(new Control[] { L("행 수", 14, 14, 50), nudRows, L("열 수", 150, 14, 50), nudCols, hint, pnl, clear, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                Ui.FlattenInputs(f);
                build();

                if (f.ShowDialog(this) != DialogResult.OK) return;

                collect();
                int rows = (int)nudRows.Value, cols = (int)nudCols.Value;
                var result = new Dictionary<string, GridCell>();
                for (int r = 1; r <= rows; r++)
                    for (int c = 1; c <= cols; c++)
                    {
                        string letter;
                        if (chosen.TryGetValue(r + "," + c, out letter)) result[letter] = new GridCell(r, c);
                    }
                cmbRows.SelectedIndex = rows;
                cmbCols.SelectedIndex = cols;
                cellMap = result;
                UpdateCellSummary();
            }
        }

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
            cmbRows.SelectedIndex = o.Rows >= 0 && o.Rows < cmbRows.Items.Count ? o.Rows : 0;
            cellMap = o.Cells.ToDictionary(kv => kv.Key, kv => new GridCell(kv.Value.Row, kv.Value.Col));
            UpdateCellSummary();
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
                Rows = Math.Max(0, cmbRows.SelectedIndex),
                Cells = SelectedCells(),
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
                Ui.StyleDialog(f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;

                var nud = new NumericUpDown { Left = 110, Top = 14, Width = 60, Minimum = 1, Maximum = 26 - p.Count, Value = 1 };
                var preview = new Label { Left = 14, Top = 50, Width = 412, Height = 54, AutoSize = false, ForeColor = Ui.Muted };
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
                Ui.StyleButton(ok, true);
                Ui.StyleButton(cancel, false);
                f.Controls.AddRange(new Control[] { L("추가할 개수", 14, 14, 90), nud, preview, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                Ui.FlattenInputs(f);
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

                // 셀 지정(1행2열에 A, 2행3열에 B)
                var cellOpt = new RunOptions { Project = "demo", Rows = 2, Cols = 3 };
                cellOpt.Names.AddRange(new[] { "a", "b", "c" });
                cellOpt.Cells["a"] = new GridCell(1, 2);
                cellOpt.Cells["b"] = new GridCell(2, 3);
                var cellOpt2 = RunOptions.Parse(cellOpt.Serialize());
                Check("셀 지정 직렬화 왕복", cellOpt2 != null && cellOpt2.Rows == 2 && cellOpt2.Cells.Count == 2
                    && cellOpt2.Cells["a"].Row == 1 && cellOpt2.Cells["a"].Col == 2 && cellOpt2.Cells["b"].Row == 2 && cellOpt2.Cells["b"].Col == 3
                    && cellOpt2.Serialize() == cellOpt.Serialize(), cellOpt.Serialize());
                Check("예전 설정(행 수·셀 없음)은 자동 배치", oldFmt != null && oldFmt.Rows == 0 && oldFmt.Cells.Count == 0, "");
                var badCells = RunOptions.ParseCells("a:1:2,zz,b:0:1,c:x:1,D:1:1,e:3:4");
                Check("잘못된 셀 지정은 무시", badCells.Count == 2 && badCells.ContainsKey("a") && badCells.ContainsKey("e"), RunOptions.CellsText(badCells));
                Check("Describe: 행·열·셀 지정", cellOpt.Describe().Contains("2행 3열") && cellOpt.Describe().Contains("셀 지정 2개"), cellOpt.Describe());
                int gr, gc;
                GridLayout.Resolve(6, 0, 0, new GridCell[0], out gr, out gc);
                Check("격자: 6개 자동 → 2행 3열", gr == 2 && gc == 3, gr + "x" + gc);
                GridLayout.Resolve(2, 5, 0, new GridCell[0], out gr, out gc);
                Check("격자: 칸 지정이 없으면 창보다 많은 열은 줄임", gr == 1 && gc == 2, gr + "x" + gc);
                GridLayout.Resolve(2, 3, 2, new GridCell[0], out gr, out gc);
                Check("격자: 행·열을 모두 지정하면 그대로(2행 3열)", gr == 2 && gc == 3, gr + "x" + gc);
                GridLayout.Resolve(2, 0, 0, new[] { new GridCell(1, 2), new GridCell(2, 3) }, out gr, out gc);
                Check("격자: 지정한 칸이 밖이면 격자를 키움(2행 3열)", gr == 2 && gc == 3, gr + "x" + gc);
                GridLayout.Resolve(5, 2, 1, new GridCell[0], out gr, out gc);
                Check("격자: 칸이 모자라면 행을 늘림", gr == 3 && gc == 2, gr + "x" + gc);

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

                // 마지막 실행값의 셀 지정이 화면에 복원되고, 선택하지 않은 세션의 칸은 빠지는지
                var s4 = Settings.Load(home, "D:\\workspace");
                var last4 = new RunOptions { Project = "demo", Rows = 2, Cols = 3 };
                last4.Names.AddRange(new[] { "a", "b" });
                last4.Cells["a"] = new GridCell(1, 2);
                last4.Cells["b"] = new GridCell(2, 3);
                last4.Cells["c"] = new GridCell(1, 1);
                s4.Last = last4;
                using (var form4 = new MainForm(s4, 0))
                {
                    var got = form4.Collect();
                    Check("폼이 행 수·열 수·셀 지정을 복원", got.Rows == 2 && got.Cols == 3 && got.Cells.Count == 2
                        && got.Cells["a"].Row == 1 && got.Cells["a"].Col == 2 && got.Cells["b"].Row == 2 && got.Cells["b"].Col == 3 && !got.Cells.ContainsKey("c"), got.Serialize());
                }

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
                string argsCells = Launch.BuildArgs("C:\\w\\open-sessions.ps1", "D:\\workspace\\", p, cellOpt, true);
                Check("스크립트 인자: 행 수·셀 지정", argsCells.Contains("-Rows 2") && argsCells.Contains("-Cols 3") && argsCells.Contains("-Cells a:1:2,b:2:3"), argsCells);
                string argsNoCell = Launch.BuildArgs("C:\\w\\open-sessions.ps1", "D:\\workspace\\", p, o, true);
                Check("셀 지정·행 수가 없으면 -Rows/-Cells 를 넘기지 않음", !argsNoCell.Contains("-Rows") && !argsNoCell.Contains("-Cells"), argsNoCell);
                cellOpt.Cells["z"] = new GridCell(1, 1);   // 선택하지 않은 세션의 칸은 넘기지 않는다
                Check("선택하지 않은 세션의 칸은 넘기지 않음", !Launch.BuildArgs("C:\\w\\open-sessions.ps1", "D:\\workspace\\", p, cellOpt, true).Contains("z:1:1"), "");
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
