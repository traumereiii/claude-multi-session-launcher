# ClaudeMultiSessionLauncher

여러 작업 폴더(`my-app-a~f` 등)를 Windows Terminal 창으로 한 번에 열고, 각 창에서 `claude` 세션을 이어서(resume) 여는 도구 모음이다.
**이 폴더째 복사하면 다른 PC·다른 위치에서도 쓸 수 있다.**

## 처음 설치

exe 는 저장소에 들어 있지 않다(`.gitignore`). 받은 뒤 한 번 빌드한다.

```powershell
git clone https://github.com/traumereiii/claude-multi-session-launcher.git
cd claude-multi-session-launcher
.\build.ps1        # 같은 폴더에 ClaudeMultiSessionLauncher.exe 가 생긴다
```

## 파일

| 파일 | 하는 일 |
|---|---|
| `ClaudeMultiSessionLauncher.exe` | 화면에서 클론·실행·최근 실행을 다루는 프로그램(더블클릭). `build.ps1` 로 만들며 저장소에는 올리지 않는다(`.gitignore`) |
| `open-sessions.ps1` | 실제로 창을 열고 세션을 이어 여는 스크립트(exe 와 터미널 모두 이걸 쓴다) |
| `window-layout.ps1` | 창을 모니터에 바둑판으로 배치하는 함수(`open-sessions.ps1` 이 불러 쓴다) |
| `open-<프로젝트>.ps1` | (선택) 프로젝트별 바로 실행용 래퍼. PC마다 직접 만들며 저장소에는 올리지 않는다(`.gitignore`) |
| `ClaudeMultiSessionLauncher.cs` · `build.ps1` | exe 소스와 빌드 스크립트 |

**exe 와 `.ps1` 은 같은 폴더에 있어야 한다.** exe 는 화면만 담당하고 실제 일은 스크립트가 하므로, 스크립트를 고치면 exe 를 다시 만들 필요가 없다.

## 필요한 것

- Windows 10/11 (PowerShell 5.1 · .NET Framework 4 는 기본 포함)
- Windows Terminal(`wt`) — 없으면 PowerShell 창으로 열리고 창 배치는 적용되지 않는다
- `claude` (PATH 에 있어야 한다), `git` (클론할 때만)

## 폴더 구조

작업 폴더는 이 도구 폴더의 **상위 폴더**를 기본으로 본다.

```
D:\workspace\                    ← 작업 폴더(기본: 도구 폴더의 상위)
├─ claude-multi-session-launcher\ ← 이 폴더
├─ my-app\my-app-a ~ my-app-f    ← 프로젝트 my-app (세션 MY-APP-A ~ F)
└─ web\web-a ~ web-c             ← 프로젝트 web (세션 WEB-A ~ C)
```

다른 곳에 두었다면 exe 의 **클론 탭 → 저장 위치**를 고치면 바로 반영되고 저장된다(실행 탭 아래에 현재 값이 표시된다).
터미널 스크립트는 `-Root <경로>` 로 지정한다.

## exe 사용법

**내장 프로젝트는 없다.** 처음 실행하면 등록된 프로젝트가 없으므로 클론 탭이 열린다. 클론 탭에서 클론하거나, 이미 클론된 폴더는 등록 탭에서 등록하면 그 프로젝트가 실행 탭에 나타난다.

**클론 탭** — 폴더명 · 원격 주소 · 갯수를 넣고 `클론`. 예) 폴더명 `my-app`, 갯수 `6` → `저장위치\my-app\my-app-a ~ my-app-f`
- 원격 주소 옆 `GitHub` 버튼은 https://github.com 을 기본 브라우저로 연다(저장소 주소를 찾아 복사할 때).
- 원격 주소를 넣으면 폴더명이 저장소 이름으로 자동으로 채워진다(`https://github.com/org/my_app.git` → `my_app`).
- 세션 이름 접두사는 폴더명을 대문자로 바꿔 자동으로 채워진다(`my_app` → `MY_APP-` → 세션 이름 `MY_APP-A`).
- 폴더명·접두사는 직접 고쳐도 되며, 고친 칸은 더 이상 자동으로 바뀌지 않는다(칸을 비우면 다시 자동으로 채워진다).
- 아래 미리보기에 저장 폴더와 만들어질 세션·폴더가 `[세션명] 폴더명` 형식으로 전부 표시된다(많으면 스크롤). 예)
  ```
  저장 폴더:  D:\workspace\my-app\   (3개)
  [MY-APP-A] my-app-a
  [MY-APP-B] my-app-b
  [MY-APP-C] my-app-c
  ```
- 세션 이름 = 접두사 + 글자. 글자의 대소문자는 접두사를 따른다: `APP-` → `APP-A`, `app-` → `app-a`(접두사가 모두 소문자일 때만 소문자). 세션 이름은 대소문자를 구분한다.
- 이미 내용이 있는 폴더는 건너뛴다(갯수를 늘려 다시 실행해도 안전).
- 끝나면 그 프로젝트가 실행 탭에 등록된다.

**등록 탭** — 이미 클론돼 있는 프로젝트를 클론 없이 실행 탭에 등록한다. 예) `C:\workspace\safed\safed-a ~ safed-f` 가 있으면 `프로젝트 폴더` 에 `C:\workspace\safed` 를 고르고 `등록`.
- `프로젝트 폴더` 는 세션 폴더(`safed-a`, `safed-b` …)가 **들어 있는** 폴더다(`safed-a` 자체가 아니다). 폴더명은 그 폴더의 이름(`safed`), 작업 폴더는 그 상위(`C:\workspace`)로 정해진다.
- 폴더를 고르면 `{폴더명}-a ~ z` 를 훑어 **갯수**(가장 뒤 글자까지), **원격 주소**(첫 폴더의 `origin`), **세션 이름 접두사**(폴더명 대문자 + `-`)를 채운다. 모두 고칠 수 있고, 고친 칸은 폴더를 다시 골라도 유지된다.
- 갯수는 폴더에 있는 것보다 작게 줄일 수 있다(예: `safed-a ~ l` 이 있어도 6 으로 두면 A~F 만 등록). 미리보기에 폴더가 없는 글자는 `← 폴더 없음(열 때 건너뜀)` 으로, 갯수 밖 폴더는 개수로 표시된다.
- 원격 주소는 비워도 등록되지만, 세션 추가(`+ 추가`)를 쓰려면 필요하다.
- 폴더 위치가 지금 작업 폴더와 다르면 확인 창에서 알려 주고, 등록하면 작업 폴더가 그 상위로 바뀐다(작업 폴더는 모든 프로젝트가 공유한다).
- 같은 폴더명을 다시 등록하면 갯수·접두사·원격 주소가 갱신된다. 폴더는 건드리지 않는다.

**실행 탭** — 프로젝트 · 세션(A~) · 모니터 · 열 수 · 옵션을 고르고 `열기`(`미리보기` 는 실행하지 않고 명령만 보여 준다).
- 같은 이름의 세션이 있으면 이어서(`--resume`), 없으면 그 이름으로 새로(`--name`) 시작한다.
- **모델 · Effort**: 기본값은 `claude-sonnet-5-5` · `high`. 모델은 목록에서 고르거나 직접 입력할 수 있고(별칭 `sonnet` `opus` 또는 전체 모델 ID), Effort 는 `low` `medium` `high` `xhigh` `max` 중에서 고른다. `(지정 안 함)` 으로 두면 해당 옵션을 붙이지 않아 claude 의 기본값·기존 설정을 그대로 쓴다. 이어서(`--resume`) 여는 세션에도 같은 값이 적용된다.
- 마지막 실행값은 자동 저장·복원된다.
- **세션 추가**: 세션 목록 끝의 `+ 추가` 를 누르고 개수를 고르면 다음 글자 폴더를 클론해 세션을 늘린다. 예) `web` 이 A~C 일 때 2개 → `web\web-d`, `-e` 를 클론하고 D·E 가 생긴다(새로 생긴 세션은 체크된 상태). 프로젝트에 원격 주소가 있어야 하며, Z(26개)까지 늘릴 수 있다.
- **최근 실행 5개**: 클릭하면 옵션을 불러오고, 더블클릭하면 바로 실행한다.

설정은 `%APPDATA%\ClaudeMultiSessionLauncher\settings.txt` 에 저장된다(지우면 처음 상태). 잘못 등록한 프로젝트는 `project=` 줄을 지운다.

## 터미널에서 쓰기

```powershell
.\open-sessions.ps1 -Group my-app                  # my-app-a~f(기본 6개), 각각 별도 창(바둑판 배치)
.\open-sessions.ps1 -Group my-app -Count 3         # my-app-a~c
.\open-sessions.ps1 -Group my-app -Names a,c       # 일부만
.\open-sessions.ps1 -Group my-app -TitlePrefix APP- # 세션 이름 APP-A ~ (기본은 MY-APP-A ~)
.\open-sessions.ps1 -Group my-app -Monitor 1       # 두 번째 모니터에
.\open-sessions.ps1 -Group my-app -Cols 2          # 2열 배치
.\open-sessions.ps1 -Group my-app -Tabs            # 한 창의 탭으로
.\open-sessions.ps1 -Group my-app -NoLayout        # 창 위치·크기는 기본값 그대로
.\open-sessions.ps1 -Group my-app -Bypass          # claude 에 --dangerously-skip-permissions
.\open-sessions.ps1 -Group my-app -Model claude-sonnet-5-5 -Effort high   # claude 에 --model / --effort (생략하면 붙이지 않음)
.\open-sessions.ps1 -Group my-app -DryRun          # 실행하지 않고 명령만 출력
```
실행 정책에 막히면 `powershell -ExecutionPolicy Bypass -File .\open-sessions.ps1 ...` 로 부른다.

자주 여는 프로젝트는 아래처럼 래퍼(`open-<프로젝트>.ps1`)를 만들어 두면 옵션만 넘겨 바로 열 수 있다. PC마다 다른 설정이라 저장소에는 올리지 않는다.
```powershell
# open-my-app.ps1
param([string[]] $Names, [switch] $Tabs, [switch] $Bypass, [switch] $DryRun, [switch] $NoLayout, [int] $Cols = 0, [int] $Monitor = 0)
& "$PSScriptRoot\open-sessions.ps1" -Group my-app -TitlePrefix APP- -Count 6 @PSBoundParameters
```

## exe 만들기

```powershell
.\build.ps1              # 같은 폴더에 ClaudeMultiSessionLauncher.exe 를 만든다(설치 불필요, .NET Framework 의 csc.exe 사용)
.\build.ps1 -SelfTest    # 빌드 후 화면 없이 핵심 로직을 점검한다(임시 폴더만 사용)
```

## 알아 둘 점

- 이미 실행 중인 세션을 같은 이름으로 다시 열면 이어 쓰는 게 아니라 복사본이 생긴다.
- 폴더를 옮기면 claude 가 세션을 폴더 경로 기준으로 저장하기 때문에 `--resume` 이 세션을 못 찾는다.
  `%USERPROFILE%\.claude\projects\` 의 해당 폴더 이름(경로의 `\`·`:`·`_` 를 `-` 로 바꾼 이름)도 같이 옮겨야 한다.
- 직접 빌드한 exe 라 다른 PC 로 복사해 처음 실행할 때 SmartScreen 경고가 뜰 수 있다(`추가 정보 → 실행`). 복사본이 걱정되면 `build.ps1` 로 그 PC 에서 다시 빌드한다.

---

# 개발자 노트 (수정할 때 읽는 부분)

2026-10-24, Claude Code 세션에서 사용자 요청으로 만들었다. 아래는 **무엇을 왜 그렇게 했는지**와 고칠 때의 함정이다.

## 한눈에 보는 구조

```
ClaudeMultiSessionLauncher.exe (화면)  ──인자로 호출──▶  open-sessions.ps1 (일) ──dot-source──▶ window-layout.ps1 (창 배치)
open-*.ps1 (터미널용 얇은 래퍼) ──────────▶  open-sessions.ps1
```

- **일은 전부 `open-sessions.ps1` 이 한다.** exe 는 선택값을 인자로 바꿔 숨긴 PowerShell 로 부를 뿐이다. 그래서 열기 동작을 바꾸려면 스크립트만 고치면 되고 exe 는 다시 빌드하지 않는다.
- **exe 가 하는 일:** 화면, 설정 저장(마지막 실행값·최근 5개·프로젝트 목록), `git clone`.

### `open-sessions.ps1` 의 흐름
1. 프로젝트 정의를 정한다: `-Group`(필수) + `-Prefix`·`-TitlePrefix`·`-Count`(생략하면 `{Group}-`, `{GROUP}-`, 6). 내장 프로젝트 표는 없다.
2. 세션마다 폴더를 찾는다: `{Root}\{Group}\{Prefix}{a}` 먼저, 없으면 `{Root}\{Prefix}{a}`(예전 위치).
3. 세션이 있는지 본다 → `Test-SessionExists`. 있으면 `claude --resume <이름>`, 없으면 `claude --name <이름>`.
4. Windows Terminal(`wt`)로 연다. 기본은 창마다(`wt -w -1 new-tab …`), `-Tabs` 면 한 창에 탭(`;` 로 이음).
5. (창 모드, `-NoLayout` 아니면) 뜬 창을 제목으로 찾아 칸에 맞춘다 → `window-layout.ps1`.

주요 파라미터: `-Group` `-Prefix` `-TitlePrefix` `-Count` `-Names` `-Tabs` `-Bypass` `-Model` `-Effort` `-DryRun` `-NoLayout` `-Cols` `-Monitor` `-Root`.
`-DryRun` 은 `[이어서]/[새로]` 와 `[배치] 제목 X= Y= 크기` 를 출력만 한다 — **고친 뒤 먼저 이걸로 확인한다.**

### `window-layout.ps1`
- `Get-GridRects -Count n [-Cols c] [-Monitor m]`: 모니터 작업 영역(작업 표시줄 제외)을 칸으로 나눈다. 열 수 자동: 1→1, 2→2, 3→3, 4→2, 5·6→3, 7·8→4, 그 외 ⌈√n⌉.
- `Wait-NewTerminalWindow`: **방금 연 창**을 찾는다. 열기 전에 기억해 둔 핸들(`$before`)은 제외하고, 창 클래스 `CASCADIA_HOSTING_WINDOW_CLASS` + 제목이 같은 것만 본다(같은 이름의 기존 창과 헷갈리지 않게).
- `Set-WindowRect`: Win32(`SetWindowPos`)로 맞춘다. Windows 10/11 창의 **보이지 않는 테두리**(보통 좌우·아래 7px 안팎)를 `DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS)` 로 재서 보정한다.
- 모니터 번호: 0 = 주 모니터, 그 다음은 X 좌표 순. 번호가 없으면 경고하고 주 모니터를 쓴다.

### `ClaudeMultiSessionLauncher.cs` 의 구성 (한 파일)
| 클래스 | 역할 |
|---|---|
| `ProjectDef` | 프로젝트 하나(폴더명·세션 접두사·개수·원격). 폴더 접두사는 `폴더명 + "-"` 로 고정 |
| `RunOptions` | 실행 옵션 한 벌. 마지막 실행값·최근 실행에 쓰인다. `Serialize/Parse/Describe` |
| `Settings` | 설정 파일 읽기·쓰기, `PushRecent`(중복은 맨 위로, 5개까지). 내장 프로젝트는 없다(처음엔 빈 목록) |
| `Launch` | 스크립트에 넘길 인자 만들기(`BuildArgs`), 스크립트 찾기, 출력 인코딩 |
| `Cloner` | `git clone -q` 를 폴더마다 스레드로 동시에 실행. 내용 있는 폴더는 건너뜀 |
| `Registrar` · `ExistingScan` | 이미 클론된 프로젝트 폴더를 훑어(`Scan`) 있는 글자·갯수를 알아내고, `ReadRemote` 로 `origin` 주소를 읽는다. 클론은 하지 않는다 |
| `MainForm` | 화면 세 탭(실행·클론·등록). 등록 탭은 `BuildRegisterTab` → `StartRegister` → `RegisterExisting`(설정 반영) → `RefreshAfterRegister`(클론 후에도 같이 씀). 실행 탭의 `+ 추가`(세션 추가)는 `AddSessions` → `Cloner.CloneLetters` 로 다음 글자 폴더만 클론한 뒤 프로젝트 개수를 늘린다 |
| `Program` | 시작 인자(`--home` `--tab clone\|register` `--selftest` `--clone-src`) |
| `SelfTest` | 화면 없이 핵심 로직 점검 |

설정 파일(`%APPDATA%\ClaudeMultiSessionLauncher\settings.txt`, `key=value` 줄):
```
root=D:\workspace
closeAfterRun=0
project=이름|세션접두사|개수|원격주소          (여러 줄)
last=프로젝트|a,b,c|모니터|열수|Bypass|NoLayout|Tabs|모델|effort      (1/0, 모델·effort 가 없는 예전 줄은 기본값 sonnet 5.5 / high 로 읽음)
recent=위와 같은 형식                          (최대 5줄, 새것이 위)
```

## 어디를 고치면 되나

| 하고 싶은 것 | 고칠 곳 |
|---|---|
| 창 여는 방식·`claude` 인자 바꾸기 | `open-sessions.ps1` (`Get-TabArgs`, `$targets` 만드는 부분) |
| 배치 규칙(열 수 자동 표, 여백) | `window-layout.ps1` 의 `Get-GridRects` |
| 화면 옵션 추가 | ① `RunOptions` 필드·`Serialize/Parse/Describe` ② `Launch.BuildArgs` ③ 화면 컨트롤 + `Collect/ApplyOptions` ④ `open-sessions.ps1` 파라미터 ⑤ `SelfTest` (래퍼를 쓴다면 그 `param` 도) |
| 최근 실행 개수 | `Settings.MaxRecent` |
| 세션 이름 규칙 | `ProjectDef.DefaultTitle`, 스크립트의 `$cfg.Title` |
| 클론 방식 | `Cloner.CloneOne` |

**옵션을 추가하면 설정 파일 형식이 바뀐다.** `RunOptions.Parse` 는 칸 수가 모자라면 그 줄을 버리므로(`p.Length < 7`), 칸을 늘릴 때는 예전 줄을 읽을 수 있게 하거나 이전 설정이 사라져도 되는지 확인한다.

## 작업 히스토리 (2026-10-24)

1. **세션 열기 스크립트** — 작업 폴더 6개(a~f)를 `wt` 탭으로 열고 각 탭에서 `claude --resume <접두사>A~F`. 이름으로 resume 이 되는지 `claude --help` 로 확인(`-r, --resume [value]`: 세션 ID 또는 이름).
2. **창 분리** — 요청에 따라 기본을 "각각 별도 창"(`wt -w -1`)으로 바꾸고 탭은 `-Tabs` 옵션으로 남김.
3. **여러 프로젝트로 확장** — `open-sessions.ps1` 공통 + 프로젝트별 래퍼. 세션이 없으면 `claude --name` 으로 새로 시작하도록 `Test-SessionExists` 추가. 작업 폴더를 `{Group}\` 아래로 옮길 때 **claude 세션 기록 폴더도 같이 옮겨야 한다**(아래 함정 참고).
4. **창 위치·크기** — `wt --size` 는 글자 칸 수라 모니터마다 결과가 달라서 Win32 로 픽셀 단위 배치. 빈 PowerShell 창 4개로 시험해 목표와 **픽셀 단위로 일치**, 1.3초 뒤에도 wt 가 되돌리지 않음을 확인.
5. **런처 exe** — 요구: ① 폴더명·원격 주소·갯수로 클론 ② 실행 옵션을 화면에서 선택하고 이전 값 저장 ③ 최근 실행 5개 빠른 실행. WinForms, `csc.exe`(C# 5)로 빌드. 스크립트는 exe 옆에 두는 방식 선택(스크립트 수정이 바로 반영).
6. **한 폴더로 정리** — 다른 PC에서도 쓰도록 하드코딩 제거: 스크립트 `-Root` 기본값을 도구 폴더의 상위로, exe 의 기본 작업 폴더도 같게. 작업 폴더를 고치면 클론 없이도 즉시 저장되게 수정.
7. **내장 프로젝트 제거(2026-11-08)** — 특정 프로젝트(폴더명·원격 주소)를 코드에 넣지 않도록 `open-sessions.ps1` 의 `-Project`·내장 표와 exe 의 기본 프로젝트 시드를 없앴다. 프로젝트는 클론 탭으로만 등록하고, 등록된 것이 없으면 클론 탭으로 연다. 프로젝트별 래퍼(`open-*.ps1`)는 저장소에서 빼고 `.gitignore` 에 넣었다.
8. **exe 를 저장소에서 제외(2026-11-08)** — 빌드 결과물은 올리지 않고 `.gitignore` 에 넣었다. 받은 쪽에서 `build.ps1` 로 만든다.
9. **이미 클론된 프로젝트 등록(2026-11-09)** — 클론만 있고 기존 폴더를 등록할 방법이 없어 `등록` 탭을 추가했다. 폴더를 고르면 `Registrar.Scan` 이 `{폴더명}-a ~ z` 를 훑어 갯수를 정하고, 갯수는 "가장 뒤 글자"까지로 잡는다(중간이 비어도 열 때 건너뜀). 이 PC 의 `safed` 에는 `safed-a ~ l` 과 `safed-f-ce` 같은 변형 폴더가 섞여 있어, 정확히 `{폴더명}-{한 글자}` 인 것만 세고 갯수는 사용자가 고칠 수 있게 했다. 화면은 UIA 로 조작해 보지 못했고 `SelfTest`(폼 생성 후 필드 값 확인)까지만 확인했다.

### 개발 중 찾아 고친 버그
| 증상 | 원인 → 해결 |
|---|---|
| 최근 실행 목록·결과 창이 폭 1148px 로 늘어남(설계 656px) | 탭 페이지가 아직 작을 때 `Anchor` 를 주면 나중에 커질 때 과하게 늘어남 → 앵커를 `Load` 이벤트에서 적용(`Anchored()`/`pendingAnchors`) |
| 처음 실행하면 세션이 모두 해제됨 | 저장된 값이 없을 때 빈 선택으로 시작 → 첫 프로젝트의 세션 전부 선택 |
| 갯수를 입력해도 미리보기 문구가 그대로 | `NumericUpDown` 은 칸을 벗어나야 `ValueChanged` → `TextChanged` 도 연결 |
| 클론 탭에서 저장 위치를 바꿔도 실행 탭이 모름 | 클론할 때만 `s.Root` 를 갱신 → `TextChanged` 에서 즉시 반영 |
| `powershell -File x.ps1 -Names a,c` 에서 `"a,c"` 한 덩어리로 받음 | 스크립트에서 쉼표로 다시 나눔(`-split ','`) |

## 겪은 함정 (고칠 때 다시 밟지 않게)

**claude 세션**
- 세션은 **작업 폴더 경로 기준**으로 `%USERPROFILE%\.claude\projects\<폴더 이름>\*.jsonl` 에 저장된다. 폴더 이름 = 경로에서 영문·숫자가 아닌 문자를 전부 `-` 로 바꾼 것(`D:\workspace\my_app\my_app-a` → `D--workspace-my-app-my-app-a`, `_` 도 `-`).
  → 폴더를 옮기면 `--resume` 이 세션을 못 찾는다. 세션 폴더도 같은 규칙의 새 이름으로 옮겨야 한다(메모리 파일도 그 안에 있다).
- `/rename` 한 이름은 jsonl 안에 `{"type":"custom-title","customTitle":"MY-APP-B",…}` 줄로 남는다. `Test-SessionExists` 가 이 줄을 `"customTitle":"이름"` 으로 정확히 찾는다.
- 이미 실행 중인 세션을 같은 이름으로 `--resume` 하면 이어 쓰는 게 아니라 **복사본**이 생긴다.

**Windows Terminal**
- 창 제목으로 새 창을 찾으려면 `--title` 과 `--suppressApplicationTitle` 가 둘 다 필요하다(앱이 제목을 덮어쓰지 못하게).
- `wt` 는 GUI 앱이라 `wt --help` 가 터미널에 출력되지 않고 대화상자로 뜰 수 있다 — 확인해 보지 않고 피했다. 옵션은 문서를 따로 본다.
- 새 창이 뜬 직후엔 wt 가 크기를 다시 잡을 수 있어 0.5초 기다린 뒤 두 번 맞춘다(`open-sessions.ps1`).

**PowerShell 5.1**
- 한글이 든 `.ps1` 은 **UTF-8 BOM** 이 있어야 깨지지 않는다. `Set-Content -Encoding UTF8` 는 BOM 을 붙이지만, 편집 도구로 고친 뒤에는 BOM 이 빠질 수 있으니 확인한다:
  `(Get-Content f.ps1 -Encoding Byte -TotalCount 3)` 가 `239 187 191` 이어야 한다.
- 창 좌표를 픽셀 그대로 쓰려면 `SetProcessDPIAware()` 를 먼저 불러야 한다(안 부르면 배율만큼 줄어든 값).
- 시험 코드에서 함수 이름을 `H` 로 짓지 말 것 — `Get-History` 의 별칭이다.

**C# / 빌드**
- `csc.exe`(.NET Framework 4)는 **C# 5** 라서 `$"…"`, `?.`, `nameof`, 식 본문 멤버, 자동 속성 초기값을 못 쓴다. 문자열은 `+` 로 잇는다.
- 한글이 든 소스는 `/codepage:65001` 로 컴파일한다(`build.ps1` 에 들어 있다).
- 4K·배율 화면에서 흐려지지 않게 `SetProcessDPIAware()` + `AutoScaleMode.Dpi`.
- `powershell` 이 리다이렉트된 출력에 쓰는 인코딩은 콘솔 코드 페이지(OEM)다 → `GetOEMCP()` 로 읽는다(`Launch.ConsoleEncoding`).
- 실행 중인 exe 는 덮어쓸 수 없다 — `build.ps1` 이 먼저 알려 준다.

**git clone**
- `git clone -q`(조용히) + `GIT_TERMINAL_PROMPT=0`(인증을 묻다 멈추는 것 방지). 진행률을 켜면 `\r` 로 로그가 폭주한다.

## 시험하는 법

1. **스크립트**: 항상 `-DryRun` 먼저(창을 열지 않고 명령·배치 좌표만 출력).
2. **핵심 로직(화면 없음)**: `.\build.ps1 -SelfTest` — 설정 저장·복원, 최근 5개, 인자 생성, **실제 `git clone`**(임시 폴더, 이 PC 의 저장소를 원본으로 사용), 재실행 시 건너뛰기, 잘못된 주소 실패를 점검한다. 44개 항목, 전부 통과해야 한다(등록 탭의 폴더 스캔·자동 채우기·재등록 포함).
3. **화면**: UI Automation 으로 실제 창을 조작해 시험했다. 시험용 시작 인자: `ClaudeMultiSessionLauncher.exe --home <임시폴더> [--tab clone]` (설정을 실제 `%APPDATA%` 가 아닌 곳에 쓰고, 클론 탭으로 바로 연다).
   - WinForms 컨트롤은 UIA 에서 모두 `Pane` 으로 보인다. `NativeWindowHandle` 로 Win32 메시지를 보내 조작한다: 버튼 `BM_CLICK`(0xF5), 입력칸 `WM_SETTEXT`(0x0C), 읽기 `WM_GETTEXT`(0x0D).
   - **클릭은 `PostMessage`(비동기)로 했다.** 동기 `SendMessage` 는 `MessageBox`(클론 확인 창)가 뜨는 동안 호출이 막힐 수 있다(그렇게 해서 얻은 첫 결과가 일관되지 않아 바꿨다). 확인 창의 `예(Y)` 버튼은 별도 최상위 창이 아니라 **메인 창의 하위 트리**에서 찾아진다.
   - 목록 상자 클릭은 `WM_LBUTTONDOWN/UP`(더블클릭은 `WM_LBUTTONDBLCLK`)을 컨트롤에 직접 보내면 마우스를 움직이지 않는다.
   - **`열기` 를 시험하면 진짜 `claude` 세션이 열린다.** 임시 프로젝트(`uidemo`)로 하고 `Close-Window`(`WM_CLOSE`)로 닫은 뒤 임시 폴더를 지운다.
4. 시험이 끝나면 `%TEMP%` 의 임시 폴더·남은 `ClaudeMultiSessionLauncher`·`claude` 프로세스를 정리한다.

## 시험하지 못한 것 / 알려진 한계
- **`wt` 가 없는 PC 의 대체 경로**(PowerShell 창 직접 열기, 배치 없음)는 코드만 있고 실행해 보지 않았다.
- **`열기 후 런처 닫기`**, **`-Bypass` 가 실제 세션에 적용되는지**, **`-Tabs` 로 실제로 열기**는 화면에서 실행해 보지 않았다(인자 생성과 드라이런까지만 확인).
- **보조 모니터(`-Monitor 1`)로의 실제 배치**는 좌표 계산만 확인했다(주 모니터는 실제 배치까지 확인).
- 프로젝트 **삭제·이름 변경** 화면이 없다(설정 파일의 `project=` 줄을 직접 고친다). 클론 후 세션 접두사·개수를 바꾸려면 같은 폴더명으로 클론 탭에서 다시 등록하면 갱신된다(폴더는 건너뜀).
- 모니터는 **번호**(0, 1…)로 저장한다. 모니터 구성이 바뀌면 다른 모니터를 가리키거나, 없으면 주 모니터로 돌아간다.
- 이미 열려 있는 세션을 화면에 표시하는 기능이 없다(중복으로 열면 복사본이 생김).
- exe 에 아이콘이 없다.

## 앞으로 하면 좋을 것 (아이디어)
- 열려 있는 세션 감지(같은 제목의 `wt` 창이 있으면 체크박스 옆에 표시, 기본 해제).
- 프로젝트 삭제·편집 버튼, 모니터를 이름으로 기억.
- 클론 후 `.claude/settings.local.json` 등 gitignore 대상 설정을 원본 폴더에서 복사하는 옵션.
- 폴더 이동 기능을 exe 에 통합(폴더 + 세션 기록을 같이 옮기는 로직 — 위 "claude 세션" 항목의 폴더 이름 규칙).
