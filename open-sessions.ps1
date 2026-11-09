<#
.SYNOPSIS
  작업 폴더 묶음({Group}\{Group}-a ~)을 Windows Terminal 창으로 한 번에 열고,
  각 창에서 claude 세션을 이어서(resume) 연다. 같은 이름의 세션이 아직 없으면 그 이름으로 새로 시작한다.

  런처 exe 가 이 스크립트를 부른다. 프로젝트별 래퍼(open-<프로젝트>.ps1, 저장소에는 올리지 않음)를 만들어 불러도 된다.

.EXAMPLE
  .\open-sessions.ps1 -Group my-app                      # my-app-a~f(기본 6개), 각각 별도 창
  .\open-sessions.ps1 -Group my-app -Count 3             # my-app-a~c
  .\open-sessions.ps1 -Group my-app -Names a,b           # 일부만
  .\open-sessions.ps1 -Group my-app -TitlePrefix APP-    # 세션 이름 APP-A ~ (기본은 MY-APP-A ~)
  .\open-sessions.ps1 -Group my-app -Tabs                # 한 창의 탭으로
  .\open-sessions.ps1 -Group my-app -Model claude-sonnet-5-5 -Effort high   # 모델·effort 지정(생략하면 claude 기본값)
  .\open-sessions.ps1 -Group my-app -DryRun              # 실행하지 않고 명령만 출력
  .\open-sessions.ps1 -Group my-app -Monitor 1           # 두 번째 모니터에 바둑판 배치
  .\open-sessions.ps1 -Group my-app -Cols 2              # 2열로 배치(6개면 2열 x 3행)
  .\open-sessions.ps1 -Group my-app -NoLayout            # 창 위치·크기는 Windows Terminal 기본값 그대로
#>
param(
    [Parameter(Mandatory = $true)]
    [string]   $Group,         # 프로젝트 폴더 이름(예: my-app) → {Root}\{Group}\{Prefix}{a~}
    [string]   $Prefix,        # 하위 폴더 접두사. 생략하면 "{Group}-"
    [string]   $TitlePrefix,   # 세션 이름 접두사(예: MY-APP-). 생략하면 Group 을 대문자로 + "-"
    [int]      $Count = 6,     # -Names 를 생략했을 때 열 개수(a~)
    [string[]] $Names,
    [switch]   $Tabs,
    [switch]   $Bypass,
    [ValidatePattern('^[A-Za-z0-9._\[\]-]*$')]
    [string]   $Model,         # claude --model 에 넘길 값(별칭 sonnet/opus 또는 전체 모델 ID). 생략하면 지정하지 않음
    [ValidateSet('', 'low', 'medium', 'high', 'xhigh', 'max')]
    [string]   $Effort,        # claude --effort 에 넘길 값. 생략하면 지정하지 않음
    [switch]   $DryRun,
    [switch]   $NoLayout,      # 창 위치·크기를 건드리지 않는다(기본은 화면에 바둑판으로 배치)
    [int]      $Cols = 0,      # 배치 열 수. 0 이면 개수에 맞춰 자동(6개 → 3열 x 2행)
    [int]      $Monitor = 0,   # 배치할 모니터. 0 = 주 모니터, 1 = 다음 모니터
    [string]   $Root = (Split-Path $PSScriptRoot -Parent)   # 프로젝트 폴더들이 있는 곳. 기본은 이 도구 폴더의 상위
)

# 폴더 구조와 세션 이름: {Root}\{Group}\{Prefix}{a~} 폴더, 세션 이름 {Title}{A~} (접두사가 모두 소문자면 {Title}{a~})
$cfg = @{
    Group   = $Group
    Prefix  = if ($Prefix) { $Prefix } else { "$Group-" }
    Title   = if ($TitlePrefix) { $TitlePrefix } else { "$($Group.ToUpper())-" }
    Letters = 1..$Count | ForEach-Object { [string][char](96 + $_) }
}

# powershell -File 로 부르면 -Names a,c 가 "a,c" 한 덩어리로 오므로 쉼표로 다시 나눈다
if (-not $Names) { $Names = $cfg.Letters }
$Names = $Names | ForEach-Object { $_ -split ',' } | Where-Object { $_ }

$claudeArgs = ''
if ($Model)  { $claudeArgs += " --model $Model" }
if ($Effort) { $claudeArgs += " --effort $Effort" }
if ($Bypass) { $claudeArgs += ' --dangerously-skip-permissions' }

# claude 는 세션을 "작업 폴더 경로" 기준 폴더(~\.claude\projects\D--workspace-...)에 저장하고,
# /rename 한 이름은 그 안의 *.jsonl 에 "customTitle" 로 남긴다. 그걸로 세션이 있는지 본다.
function Test-SessionExists([string] $dir, [string] $title) {
    $projDir = Join-Path "$env:USERPROFILE\.claude\projects" ($dir -replace '[^A-Za-z0-9]', '-')
    if (-not (Test-Path $projDir)) { return $false }
    # 세션 이름은 대소문자를 구분한다(APP-A 와 app-a 는 다른 세션)
    [bool](Select-String -Path (Join-Path $projDir '*.jsonl') -SimpleMatch -CaseSensitive "`"customTitle`":`"$title`"" -List -ErrorAction SilentlyContinue)
}

$targets = foreach ($n in $Names) {
    $name   = $n.ToLower()
    $folder = "$($cfg.Prefix)$name"
    # 새 위치({Group}\{폴더})를 먼저 보고, 없으면 예전 위치({Root}\{폴더})
    $dir = @((Join-Path $Root "$($cfg.Group)\$folder"), (Join-Path $Root $folder)) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $dir) { Write-Warning "폴더가 없어 건너뜁니다: $folder"; continue }

    # 글자의 대소문자는 접두사를 따른다: 접두사가 모두 소문자면 소문자(app-a), 아니면 대문자(APP-A). 런처 exe 와 같은 규칙.
    $lowerTitle = ($cfg.Title -cmatch '\p{Ll}') -and ($cfg.Title -cnotmatch '\p{Lu}')
    $title  = "$($cfg.Title)$(if ($lowerTitle) { $name } else { $name.ToUpper() })"
    $resume = Test-SessionExists $dir $title
    [pscustomobject]@{
        Dir   = $dir
        Title = $title
        Mode  = if ($resume) { '이어서' } else { '새로' }
        Cmd   = if ($resume) { "claude --resume $title$claudeArgs" } else { "claude --name $title$claudeArgs" }
    }
}
if (-not $targets) { Write-Error '열 폴더가 없습니다.'; return }

if (Get-Command wt.exe -ErrorAction SilentlyContinue) {
    # 탭 하나가 하는 일: 폴더로 이동한 뒤 claude 실행
    function Get-TabArgs($x) {
        "new-tab -d `"$($x.Dir)`" --title $($x.Title) --suppressApplicationTitle powershell -NoExit -Command $($x.Cmd)"
    }

    if ($Tabs) {
        # 한 창에 탭으로 (탭 사이는 `;`)
        $wtArgs = ($targets | ForEach-Object { Get-TabArgs $_ }) -join ' ; '
        if ($DryRun) { "wt $wtArgs"; return }
        Start-Process wt.exe -ArgumentList $wtArgs
    }
    else {
        # 각각 새 창으로 (-w -1 = 항상 새 창)
        $layout = -not $NoLayout
        if ($layout) {
            . "$PSScriptRoot\window-layout.ps1"
            $rects  = @(Get-GridRects -Count @($targets).Count -Cols $Cols -Monitor $Monitor)
            $before = @(Get-TerminalWindows)    # 이미 열려 있던 창은 건드리지 않으려고 기억해 둔다
        }

        foreach ($x in $targets) {
            $wtArgs = "-w -1 $(Get-TabArgs $x)"
            if ($DryRun) { "[$($x.Mode)] wt $wtArgs"; continue }
            Start-Process wt.exe -ArgumentList $wtArgs
            Start-Sleep -Milliseconds 400   # 창이 순서대로 뜨도록 간격을 둔다
        }

        if ($layout) {
            if ($DryRun) {
                $i = 0
                foreach ($x in $targets) { $r = $rects[$i++]; "[배치] $($x.Title)  X=$($r.X) Y=$($r.Y)  $($r.W)x$($r.H)" }
            }
            else {
                # 창이 뜨면 제목으로 찾아 칸에 맞춘다. 뜬 직후엔 wt 가 크기를 다시 잡을 수 있어 잠깐 기다린 뒤 두 번 맞춘다.
                $taken = @(); $i = 0
                foreach ($x in $targets) {
                    $rect = $rects[$i++]
                    $h = Wait-NewTerminalWindow -Title $x.Title -Before $before -Taken $taken
                    if (-not $h) { Write-Warning "창을 찾지 못해 배치하지 못했습니다: $($x.Title)"; continue }
                    $taken += $h
                    Start-Sleep -Milliseconds 500
                    [void](Set-WindowRect -Handle $h -Rect $rect)
                    Start-Sleep -Milliseconds 300
                    [void](Set-WindowRect -Handle $h -Rect $rect)
                }
            }
        }
    }
}
else {
    # Windows Terminal 이 없으면 PowerShell 창을 따로 연다
    foreach ($x in $targets) {
        $cmd = "`$Host.UI.RawUI.WindowTitle='$($x.Title)'; $($x.Cmd)"
        if ($DryRun) { "[$($x.Mode)] powershell -NoExit -Command $cmd  (cwd: $($x.Dir))"; continue }
        Start-Process powershell.exe -WorkingDirectory $x.Dir -ArgumentList '-NoExit', '-Command', $cmd
    }
}
