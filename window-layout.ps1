# Windows Terminal 창을 화면에 바둑판으로 배치하는 함수 모음. open-sessions.ps1 이 불러 쓴다(dot-source).
#
#   Get-GridRects  -Count 6 [-Cols 3] [-Monitor 0]   # 모니터 작업 영역을 칸으로 나눈 픽셀 사각형 목록
#   Get-TerminalWindows                              # 지금 열려 있는 Windows Terminal 창 핸들
#   Wait-NewTerminalWindow -Title X -Before $h ...   # 방금 연 창(제목 X)이 나타나길 기다려 핸들을 돌려준다
#   Set-WindowRect -Handle $h -Rect $r               # 창을 사각형에 맞춘다(보이지 않는 테두리 보정 포함)
#   Close-Window   -Handle $h                        # 창 닫기(WM_CLOSE)

if (-not ('WinApi' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class WinApi {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    public static List<long> FindByClass(string cls) {
        var list = new List<long>();
        EnumWindows((h, l) => {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == cls && IsWindowVisible(h)) list.Add(h.ToInt64());
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Title(long h) {
        var sb = new StringBuilder(512);
        GetWindowText(new IntPtr(h), sb, sb.Capacity);
        return sb.ToString();
    }

    // 눈에 보이는 영역이 (x, y, w, hgt) 에 맞도록 창을 옮긴다.
    // Windows 10/11 창은 좌우·아래에 보이지 않는 테두리(약 7px)가 있어서, 그만큼 보정하지 않으면 창 사이가 벌어지거나 겹친다.
    public static RECT Place(long handle, int x, int y, int w, int hgt) {
        var h = new IntPtr(handle);
        const uint FLAGS = 0x0004 | 0x0010;   // SWP_NOZORDER | SWP_NOACTIVATE
        ShowWindow(h, 9);                      // SW_RESTORE: 최대화돼 있으면 풀어야 크기를 줄 수 있다
        SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, FLAGS);   // 먼저 한 번 놓고 테두리 크기를 잰다
        RECT win, vis;
        GetWindowRect(h, out win);
        DwmGetWindowAttribute(h, 9, out vis, Marshal.SizeOf(typeof(RECT)));   // 9 = DWMWA_EXTENDED_FRAME_BOUNDS
        int l = vis.Left - win.Left, t = vis.Top - win.Top, r = win.Right - vis.Right, b = win.Bottom - vis.Bottom;
        SetWindowPos(h, IntPtr.Zero, x - l, y - t, w + l + r, hgt + t + b, FLAGS);
        GetWindowRect(h, out win);
        DwmGetWindowAttribute(h, 9, out vis, Marshal.SizeOf(typeof(RECT)));
        return vis;                            // 실제로 보이는 사각형(검증용)
    }

    public static void Close(long h) { PostMessage(new IntPtr(h), 0x0010, IntPtr.Zero, IntPtr.Zero); }   // WM_CLOSE
}
"@
}

# 픽셀 좌표를 그대로 쓰려면 DPI 가상화를 꺼야 한다(안 끄면 배율만큼 줄어든 값이 나온다)
[void][WinApi]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms

function Get-GridRects {
    param(
        [Parameter(Mandatory)][int] $Count,
        [int] $Cols = 0,        # 0 이면 개수에 맞춰 자동
        [int] $Monitor = 0      # 0 = 주 모니터, 1~ = 나머지 모니터
    )
    $screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { -not $_.Primary }, { $_.Bounds.X })
    if ($Monitor -ge $screens.Count) {
        Write-Warning "모니터 $Monitor 가 없어 주 모니터를 씁니다(모니터 수: $($screens.Count))."
        $Monitor = 0
    }
    $area = $screens[$Monitor].WorkingArea      # 작업 표시줄을 뺀 영역

    if ($Cols -le 0) {
        $Cols = switch ($Count) { 1 { 1 } 2 { 2 } 3 { 3 } 4 { 2 } 5 { 3 } 6 { 3 } 7 { 4 } 8 { 4 } default { [int][math]::Ceiling([math]::Sqrt($Count)) } }
    }
    $Cols = [math]::Min($Cols, $Count)
    $rows = [int][math]::Ceiling($Count / $Cols)

    $cellW = [int][math]::Floor($area.Width / $Cols)
    $cellH = [int][math]::Floor($area.Height / $rows)
    for ($i = 0; $i -lt $Count; $i++) {
        [pscustomobject]@{
            X = $area.X + ($i % $Cols) * $cellW
            Y = $area.Y + [int][math]::Floor($i / $Cols) * $cellH
            W = $cellW
            H = $cellH
        }
    }
}

function Get-TerminalWindows { [WinApi]::FindByClass('CASCADIA_HOSTING_WINDOW_CLASS') }

# 방금 연 창을 찾는다: 연 적 없는 핸들(Before 에 없음)이고 제목이 Title 인 것. 같은 이름의 기존 창과 헷갈리지 않게 한다.
function Wait-NewTerminalWindow {
    param([string] $Title, [long[]] $Before, [long[]] $Taken = @(), [int] $TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($h in (Get-TerminalWindows)) {
            if ($Before -contains $h -or $Taken -contains $h) { continue }
            if ([WinApi]::Title($h) -eq $Title) { return $h }
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    $null
}

function Set-WindowRect {
    param([Parameter(Mandatory)][long] $Handle, [Parameter(Mandatory)] $Rect)
    [WinApi]::Place($Handle, $Rect.X, $Rect.Y, $Rect.W, $Rect.H)
}

function Close-Window { param([long] $Handle) [WinApi]::Close($Handle) }
