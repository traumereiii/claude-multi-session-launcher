# ClaudeMultiSessionLauncher.exe 빌드 — 설치 없이 .NET Framework 4 의 csc.exe 로 컴파일한다.
# 결과물(ClaudeMultiSessionLauncher.exe)은 이 폴더에 만든다. open-sessions.ps1 · window-layout.ps1 과 같은 폴더에 있어야 동작한다.
#   .\build.ps1                 # 빌드
#   .\build.ps1 -SelfTest       # 빌드 후 화면 없이 핵심 로직 점검(임시 폴더 사용)
param([switch] $SelfTest)

$here = $PSScriptRoot
$out  = Join-Path $here 'ClaudeMultiSessionLauncher.exe'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { Write-Error "csc.exe 를 찾을 수 없습니다: $csc"; return }

# 실행 중인 exe 는 덮어쓸 수 없다
if (Test-Path $out) {
    try { [IO.File]::OpenWrite($out).Close() } catch { Write-Error "ClaudeMultiSessionLauncher.exe 가 실행 중이라 덮어쓸 수 없습니다. 런처를 닫고 다시 빌드하세요."; return }
}

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /out:$out `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $here 'ClaudeMultiSessionLauncher.cs')
if ($LASTEXITCODE -ne 0) { Write-Error '컴파일 실패'; return }
"빌드 완료: $out ($([math]::Round((Get-Item $out).Length / 1KB, 1))KB)"

if ($SelfTest) {
    $tmp = Join-Path $env:TEMP 'claude-multi-session-launcher-selftest'
    $res = Join-Path $env:TEMP 'claude-multi-session-launcher-selftest.txt'
    # 클론 시험에 쓸 원본: 이 PC 에 이미 있는 저장소 하나
    $src = Get-ChildItem (Split-Path $here -Parent) -Recurse -Depth 2 -Directory -Filter '.git' -Force -ErrorAction SilentlyContinue |
        Select-Object -First 1 | ForEach-Object { Split-Path $_.FullName -Parent }
    $a = @('--selftest', $res, '--home', $tmp)
    if ($src) { $a += @('--clone-src', $src) }
    $p = Start-Process $out -ArgumentList $a -Wait -PassThru
    Get-Content $res -Encoding UTF8
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0) { Write-Error "자체 점검 실패 (exit $($p.ExitCode))" }
}
