# 개발 가이드

예시 경로는 저장소를 `C:\Users\kk\github\remodesk`에 clone한 기준입니다. 다른 위치에 clone했다면 그 경로로 바꿔 실행하세요.

## 준비

- Windows 10 1703 이상 또는 Windows 11
- .NET 8 SDK

  ```powershell
  winget install --id Microsoft.DotNet.SDK.8 -e
  ```

  설치한 뒤 **새 PowerShell 창**을 열고 `dotnet --list-sdks`에 `8.0.x`가 보이는지 확인합니다.

## 프로젝트 구조

자세한 구조와 클래스별 역할은 [architecture.md](architecture.md)를 보세요.

| 프로젝트 | 실행 파일 / 역할 |
|---|---|
| `windows/Client/RemoteDesktop.Client` | **RemoteDesktop.exe** — 통합 Windows 앱 ([원격 PC에 연결] + [내 PC 원격 허용]) |
| `windows/Host/RemoteDesktop.Host` | **RemoteDesktop.Host.exe** — 콘솔 Host + 관리 명령 |
| `windows/Host/RemoteDesktop.Host.Engine` | Host 엔진 (앱과 콘솔이 같이 사용) |
| `windows/Protocol`, `Core`, `Media`, `Transport` | 공통 라이브러리 |
| `windows/Tests/RemoteDesktop.Tests` | xUnit 테스트 (단위 + 실제 Host/Client 통합) |
| `signaling/Signaling.Server` | 시그널링 서버 |
| `client_app` | Flutter 앱 (iPhone · Android · macOS) |

Java에 비유하면 `.sln`은 Maven 멀티 모듈 루트의 `pom.xml`, `.csproj`는 모듈별 `pom.xml`, `ProjectReference`는 모듈 간 dependency입니다.

## 전체 테스트

```powershell
cd C:\Users\kk\github\remodesk\windows
dotnet test .\Tests\RemoteDesktop.Tests      # C# 79개 (실제 화면 캡처, GPU 인코더, TLS, WebRTC 포함)

cd C:\Users\kk\github\remodesk\client_app
flutter test                                 # Dart 47개 (실제 Host가 필요한 5개는 환경 변수가 없으면 건너뜀)
```

C# 테스트는 실제 사용자 설정(`%LOCALAPPDATA%\RemoteDesktop`)과 Windows 인증서 저장소를 건드리지 않고, 임시 폴더와 임시 인증서를 씁니다. 클립보드와 전원 동작은 가짜 구현으로 시험합니다.

## 빌드

```powershell
cd C:\Users\kk\github\remodesk
dotnet build .\windows\RemoteDesktop.sln
```

`경고 0개, 오류 0개`가 나오면 성공입니다.

## STEP 1 - 화면 캡처 저장

```powershell
dotnet run --project .\windows\Host\RemoteDesktop.Host -- capture (Join-Path $PWD "captures\step1.bmp")
Start-Process .\captures\step1.bmp
```

## STEP 2 - Windows → Windows LAN 화면 전송

### 1) Host PC (화면을 보여 줄 PC)

```powershell
cd C:\Users\kk\github\remodesk
dotnet run --project .\windows\Host\RemoteDesktop.Host
```

옵션:

```powershell
dotnet run --project .\windows\Host\RemoteDesktop.Host -- --monitor 2 --fps 30 --quality 60 --verbose
```

예상 출력:

```text
10:39:53.104 [INFO] Monitor 1: 1920x1080 at (0,0) [주 모니터]
10:39:53.110 [INFO] Host started
10:39:53.110 [INFO] Host ID: HOST-939E16
============================================================
  Host ID     : HOST-939E16
  접속 코드   : K7MPQ-2XRTA   (Host를 다시 시작하면 바뀝니다)
  주소        : 192.168.123.108:50505
  인증서 지문 : E24C 0A66 8A1B 5244 ...
============================================================
10:39:53.230 [INFO] Listening on TCP 50505 (Ctrl+C로 종료)
```

**처음 실행하면 Windows Defender 방화벽 창이 뜹니다.** `개인 네트워크`만 체크하고 `액세스 허용`을 누르세요. `공용 네트워크`는 체크하지 않습니다.

### 2) Client PC (원격 화면을 볼 PC)

```powershell
cd C:\Users\kk\github\remodesk
dotnet run --project .\windows\Client\RemoteDesktop.Client
```

STEP 10부터 이 명령은 통합 앱 **RemoteDesktop.exe**를 실행합니다.

1. [원격 PC에 연결] 탭에서 `PC 추가`를 누르고 `같은 네트워크 (IP)`, Host 주소(예: `192.168.123.108`)를 입력합니다.
2. 목록에서 PC를 선택하고 `연결`을 누릅니다.
3. 로그인 창에서 `접속 코드`를 고르고 Host 화면의 코드를 입력합니다. 하이픈과 대소문자는 상관없습니다.
4. 처음이면 `새 PC 확인` 창에 인증서 지문이 나옵니다. Host 화면의 지문과 같으면 `예`를 누릅니다.
5. 원격 화면 창이 열립니다. `Ctrl+Alt+Enter`를 누르면 전체 화면으로 바뀝니다.

PC가 한 대뿐이면 같은 PC에서 Host와 Client를 각각 실행하고 주소에 `127.0.0.1`을 입력해 테스트할 수 있습니다. 이때는 화면 안에 화면이 반복되어 보이는 것이 정상입니다.

### 3) 테스트 항목

| 테스트 | 예상 결과 |
|---|---|
| 올바른 코드로 연결 | 원격 화면 표시, Host 로그에 `Authentication successful`, `Video stream started` |
| Host에서 창을 움직임 | Client 화면이 따라 움직임 (마우스 커서 포함) |
| 틀린 코드 입력 | `접속 코드가 올바르지 않습니다.` 표시, Host 로그에 `Authentication failed` |
| 틀린 코드 5번 | 해당 PC에서 5분간 연결 거부 (`Rejected ... 차단됨`) |
| 연결 중 두 번째 Client | `다른 Client가 이미 연결되어 있습니다.` |
| Client 창 닫기 | Host 로그 `Client said bye`, `Client disconnected` |
| Host Ctrl+C | Client에 연결 끊김 메시지 후 연결 화면으로 돌아감 |
| 5초 이상 연결 유지 | Host 로그 `Stream: xx fps, xxxx kbps, RTT xx ms` |

STEP 2 당시(GDI 캡처 + JPEG)는 1080p에서 약 15~25 fps였습니다. STEP 8부터는 Desktop Duplication + H.264(GPU) + 적응형 화질을 사용합니다(Windows Client 기준).

### 4) 문제 해결

| 증상 | 원인 / 해결 |
|---|---|
| `연결할 수 없습니다 (ConnectionRefused)` | Host가 실행 중이 아니거나 포트가 다릅니다. |
| `연결할 수 없습니다 (TimedOut)` / 시간 초과 | 방화벽 차단. `제어판 → Windows Defender 방화벽 → 앱 허용`에서 `RemoteDesktop.Host`의 `개인`을 체크합니다. Wi-Fi 프로필이 `공용`이면 `개인`으로 바꿉니다. 두 PC가 같은 공유기에 연결되어 있는지도 확인합니다. |
| 주소가 여러 개 표시됨 | `172.x.x.x`는 보통 WSL/Hyper-V 가상 어댑터입니다. 공유기 대역(`192.168.x.x`)을 사용하세요. |
| `인증서가 이전과 다릅니다` | Host의 인증서가 새로 만들어졌습니다. 직접 지운 것이 맞으면 Client PC의 `%LOCALAPPDATA%\RemoteDesktop\known_hosts.json`에서 해당 Host ID 줄을 지웁니다. |
| `화면 캡처 불가` 경고 | Host가 잠금 화면이나 UAC 확인 창 상태입니다. 일반 앱은 보안 데스크톱을 캡처할 수 없습니다. 풀리면 자동으로 다시 전송합니다. |
| 화면이 검게 나옴 | 일부 DRM 영상/보호된 창은 캡처되지 않습니다. |
| 화면 일부만 보임 | 디스플레이 배율 문제일 수 있습니다. Host를 최신 코드로 다시 빌드했는지 확인합니다. |

### 저장되는 파일

| 위치 | 내용 |
|---|---|
| `%LOCALAPPDATA%\RemoteDesktop\host.json` | Host ID (비밀 아님) |
| 인증서 저장소 `현재 사용자\개인` (`certmgr.msc`) | `RemoteDesktop Host HOST-xxxxxx` TLS 인증서와 개인 키 |
| `%LOCALAPPDATA%\RemoteDesktop\known_hosts.json` | Client가 신뢰한 Host ID와 인증서 지문 |

Host ID를 초기화하려면 `host.json`을 지우고, 인증서도 `certmgr.msc`에서 지웁니다.

## STEP 3 - Windows → Windows 마우스·키보드

실행 방법은 STEP 2와 같습니다. Host는 기본적으로 입력을 받으며, 화면만 공유하려면 `--view-only`를 붙입니다.

```powershell
# Host PC
dotnet run --project .\windows\Host\RemoteDesktop.Host

# 화면만 보여 주기
dotnet run --project .\windows\Host\RemoteDesktop.Host -- --view-only

# Client PC
dotnet run --project .\windows\Client\RemoteDesktop.Client
```

Host 시작 로그에 `Input: enabled (mouse, keyboard)`가 보여야 합니다.

### 조작 방법 (Client 원격 창)

| 동작 | 원격 PC 결과 |
|---|---|
| 마우스 이동 / 클릭 / 우클릭 / 가운데 버튼 / 뒤로·앞으로 버튼 | 같은 동작 |
| 더블 클릭, 드래그 | 같은 동작 (창 밖으로 끌어도 가장자리에서 계속됨) |
| 휠, 가로 휠 | 스크롤 |
| 일반 키, Ctrl/Alt/Shift 조합, F1~F12 | 같은 키 |
| Windows 키, Alt+Tab, Alt+F4 | **원격 PC에서** 실행됨 (원격 창이 활성화된 동안) |
| 한/영 키 | 원격 PC의 한/영 전환 (한글은 원격 IME가 조합) |
| `Ctrl+Alt+Enter` | 내 PC에서 전체 화면 전환 (원격으로 보내지 않음) |
| `Ctrl+Alt+Del` | 내 PC의 보안 화면이 열림. 원격으로는 보낼 수 없음 |

원격 창에서 빠져나오려면 마우스로 다른 창을 클릭합니다. 창이 비활성화되면 눌려 있던 키를 모두 원격에서 뗍니다.

### 테스트 (PC 두 대 권장)

| 테스트 | 예상 결과 |
|---|---|
| Client에서 마우스 이동 | Host의 실제 커서가 같은 위치로 이동 |
| 바탕 화면 아이콘 더블 클릭 | 프로그램 실행 |
| 창 제목 표시줄 드래그 | 창 이동 |
| 메모장에 `Hello` 입력, 한/영 후 `안녕` 입력 | Host 메모장에 그대로 입력 |
| 메모장에서 Ctrl+A, Ctrl+C, Ctrl+V | 전체 선택, 복사, 붙여넣기 |
| Windows 키 | Host의 시작 메뉴가 열림 (Client 시작 메뉴는 안 열림) |
| Alt+Tab | Host에서 창 전환 |
| Client 창을 닫음 | Host 로그 `... frames sent, N input events` |
| Host `--view-only` | 화면은 보이고 입력은 무시됨 |

**같은 PC에서 Host와 Client를 함께 실행하면 입력 테스트가 제대로 되지 않습니다.** Client 창에서 마우스를 움직이면 실제 커서가 원격 좌표로 이동하고, 그 움직임이 다시 Client 창에 들어오는 루프가 생깁니다. PC가 한 대면 `--view-only`로 화면만 확인하세요.

### 문제 해결

| 증상 | 원인 / 해결 |
|---|---|
| 작업 관리자 등에서만 입력이 안 됨 | 관리자 권한 창입니다(UIPI). Host를 관리자 권한 PowerShell에서 실행합니다. |
| Host 로그 `입력 전달 실패` | 관리자 권한 창이 앞에 있거나 화면이 잠겨 있거나 UAC 창이 떠 있습니다. |
| 커서 위치가 어긋남 | Host와 Client를 모두 최신 코드로 다시 빌드합니다. 다중 모니터라면 `--monitor` 번호를 확인합니다. |
| Windows 키가 내 PC에서 열림 | 원격 창이 활성화되어 있지 않습니다. 원격 화면을 한 번 클릭합니다. Client 로그에 `키보드 훅 설치 실패`가 있는지도 확인합니다. |
| 키가 눌린 채로 남음 | Client 창을 클릭했다가 다른 창을 클릭하면 모두 떼어집니다. 연결을 끊어도 Host가 자동으로 뗍니다. |
| 한글이 영어로 입력됨 | 원격 PC의 입력기가 영문 상태입니다. 한/영 키를 누릅니다. |

## STEP 4 - Flutter 앱 (iPhone · Android · macOS)

하나의 Flutter 프로젝트 `client_app/`에서 iOS, Android, macOS 앱을 함께 관리합니다. Windows Client와 같은 프로토콜을 Dart로 구현했습니다. TLS, 접속 코드 상호 인증, JPEG 프레임, 입력 메시지가 모두 같습니다.

```text
client_app/
├── pubspec.yaml                    # 의존성: crypto(HMAC), async(StreamQueue), shared_preferences(PC 목록 저장)
├── lib/
│   ├── main.dart                   # 앱 진입점
│   ├── protocol/
│   │   ├── protocol.dart           # framing, 영상 헤더, 메시지 생성 (C# Protocol과 동일)
│   │   ├── auth.dart               # 접속 코드 HMAC 증명 (C# AuthProof와 동일)
│   │   └── connection.dart         # TLS 연결, 인증 순서, 프레임 수신, 입력 전송
│   ├── storage/host_store.dart     # 등록된 PC 목록, 신뢰한 인증서 지문
│   ├── input/key_map.dart          # Flutter 물리 키 → 프로토콜 키 이름
│   └── screens/
│       ├── host_list_screen.dart   # 첫 화면: PC 목록, Online/Offline, 추가·편집·삭제
│       └── remote_screen.dart      # 원격 화면, 마우스·키보드, 메뉴
├── test/
│   ├── protocol_test.dart          # 단위 테스트 (C#과 같은 인증값인지 검증 포함)
│   └── host_e2e_test.dart          # 실제 Windows Host 연결 테스트
└── android/  ios/  macos/          # 플랫폼별 설정 (flutter create로 생성)
```

플랫폼별로 추가한 권한은 다음과 같습니다.

| 플랫폼 | 파일 | 내용 |
|---|---|---|
| Android | `android/app/src/main/AndroidManifest.xml` | `INTERNET` 권한 (release 빌드에 필요) |
| iOS | `ios/Runner/Info.plist` | `NSLocalNetworkUsageDescription` (같은 Wi-Fi의 PC에 접속할 때 표시할 권한 요청 문구) |
| macOS | `macos/Runner/DebugProfile.entitlements`, `Release.entitlements` | `com.apple.security.network.client` (샌드박스에서 외부 연결 허용) |

### 기능 (STEP 4 기준)

| 기능 | iPhone / Android | macOS |
|---|---|---|
| PC 목록, Online/Offline 표시, 추가·편집·삭제 | O | O |
| 접속 코드 인증, 처음 연결 시 인증서 지문 확인 | O | O |
| 원격 화면 표시, FPS 표시, 전체 화면 | O | O |
| 핀치 확대 / 두 손가락 이동 | O | - |
| 마우스 (이동, 좌/우/가운데 클릭, 드래그, 휠, 트랙패드 스크롤) | 마우스 연결 시 | O |
| 키보드 (조합키 포함) | 하드웨어 키보드 연결 시 | O (⌘ → Ctrl 변환 기본 켜짐) |
| 메뉴: 한/영, Windows 키, Alt+Tab, Esc | O | O |
| 터치 마우스, 가상 키보드 | O (STEP 5) | - |

### 준비

```powershell
# Flutter (이 PC에는 C:\Users\kk\dev\flutter 에 설치되어 있고 사용자 PATH에 등록됨)
flutter --version
flutter doctor
```

| 대상 | 필요한 것 |
|---|---|
| Android | Android Studio 설치 → 처음 실행할 때 SDK 설치 마법사 완료 → `flutter doctor --android-licenses`로 라이선스 동의 |
| iPhone | **Mac + Xcode**, Apple ID(무료 개인 팀 가능). Windows에서는 iOS 빌드가 불가능합니다. |
| macOS 앱 | **Mac + Xcode** |

### 실행

```powershell
cd C:\Users\kk\github\remodesk\client_app
flutter pub get

# Android 폰(USB 디버깅 켜기) 또는 에뮬레이터
flutter devices
flutter run -d <기기 ID>

# APK 파일 만들기 → build\app\outputs\flutter-apk\app-release.apk
flutter build apk --release
```

Mac에서는 저장소를 clone한 뒤 실행합니다.

```bash
cd ~/github/remodesk/client_app
flutter pub get
flutter run -d macos
open ios/Runner.xcworkspace
flutter run -d <iPhone 기기 ID>
```

`open ios/Runner.xcworkspace`로 Xcode를 열고 `Runner → Signing & Capabilities`에서 Team을 선택합니다. 그다음 `flutter run`으로 iPhone에서 실행합니다.

### 테스트

```powershell
cd C:\Users\kk\github\remodesk\client_app
flutter analyze
flutter test test/protocol_test.dart

# 실제 Host 연동 테스트 (다른 창에서 Host가 실행 중이어야 함)
$env:REMODESK_HOST = "127.0.0.1"
$env:REMODESK_CODE = "Host에 표시된 접속 코드"
flutter test test/host_e2e_test.dart
```

| 앱 테스트 | 예상 결과 |
|---|---|
| [PC 추가]에 Host 주소 입력 | 목록에 `● Online` 표시 (Host가 꺼져 있으면 `○ Offline`) |
| PC 선택 → 접속 코드 입력 | 처음이면 인증서 지문 확인 창 → `같음, 연결` → 원격 화면 |
| 틀린 접속 코드 | `접속 코드가 올바르지 않습니다.` |
| 모바일: 두 손가락 벌리기 | 화면 확대 |
| macOS: 마우스 이동·클릭·드래그·휠 | Host에서 같은 동작 |
| macOS: ⌘C / ⌘V | Host에서 Ctrl+C / Ctrl+V |
| 메뉴 → 한/영 전환 | Host 입력기 한/영 전환 |
| Host 종료 | 앱이 목록으로 돌아가고 `연결이 끊겼습니다` 표시 |

### 문제 해결

| 증상 | 해결 |
|---|---|
| Android에서 연결 안 됨 | 폰과 PC가 같은 Wi-Fi인지 확인합니다. 에뮬레이터에서 같은 PC의 Host에 접속할 때는 주소를 `10.0.2.2`로 입력합니다. |
| iPhone에서 연결 안 됨 | `설정 → 개인정보 보호 및 보안 → 로컬 네트워크`에서 Remote Desktop을 켭니다. |
| macOS에서 `Operation not permitted` | entitlements에 `network.client`가 있는지 확인하고 `flutter clean` 후 다시 빌드합니다. |
| `인증서가 이전과 다릅니다` | Host 인증서가 바뀐 경우입니다. 확실하면 PC 목록에서 삭제 후 다시 추가합니다. |
| 화면이 느림 | STEP 2의 JPEG 방식 한계입니다. Host를 `--quality 50` 등으로 실행하거나 STEP 8(H.264)을 기다립니다. |
| Mac에서 한/영 키가 원격으로 안 감 | Mac의 한/영 전환 키는 macOS가 먼저 처리합니다. 메뉴의 `한/영 전환`을 사용합니다. |

## STEP 5 - 모바일 터치 마우스 · 가상 키보드

iPhone/Android 원격 화면 하단에 `[터치패드] [키보드] [메뉴]` 바가 생겼습니다.

### 터치 조작

| 동작 | 터치패드 모드 (기본) | 직접 터치 모드 |
|---|---|---|
| 한 손가락 이동 | 커서 이동 (노트북 터치패드처럼) | 누른 채 끌기 = 드래그 |
| 한 손가락 탭 | 현재 커서 위치 클릭 | 누른 곳 클릭 |
| 두 번 탭 | 더블 클릭 | 더블 클릭 |
| 두 손가락 탭 | 우클릭 | 우클릭 |
| 두 손가락 위/아래 | 스크롤 (휴대폰과 같은 방향) | 스크롤. 확대 상태에서는 화면 이동 |
| 길게 누르기 (0.5초) | 버튼을 누른 채 유지 → 이동하면 드래그 → 떼면 놓기 | 우클릭 |
| 두 손가락 벌리기/모으기 | 확대/축소 (최대 5배). 커서를 따라 화면이 움직임 | 확대/축소 |

모드는 하단 `터치패드` / `직접 터치` 버튼으로 바꿉니다. `메뉴 → 터치 사용법`에서도 볼 수 있습니다.

### 가상 키보드

- `키보드` 버튼을 누르면 휴대폰 키보드와 특수 키 줄이 나타납니다.
- 특수 키 줄: `Esc` `Tab` `Ctrl` `Alt` `Shift` `Win` `한/영` `← ↑ ↓ →` `Del` `Home` `End` `PgUp` `PgDn` `F1~F12`
- `Ctrl` `Alt` `Shift` `Win`은 한 번 누르면 켜지고, 다음 키 하나에 함께 적용된 뒤 꺼집니다. 예: `Ctrl` → `c` = Ctrl+C
- 한글은 휴대폰 키보드에서 조합이 끝난 글자만 보냅니다. 조합 중인 글자는 화면 왼쪽 아래에 `입력 중: 하`처럼 표시됩니다.
- 일반 글자는 `text_input`으로 보내므로 원격 PC의 한/영 상태와 관계없이 그대로 입력됩니다.

### 구현 파일

| 파일 | 내용 |
|---|---|
| `lib/input/touch_gestures.dart` | 터치 제스처 상태 기계 (탭, 이동, 길게 누르기, 두 손가락 탭/스크롤, 핀치). UI와 분리되어 있어 단위 테스트가 가능합니다. |
| `lib/input/soft_keyboard.dart` | 가상 키보드 입력 → `text_input` / `Backspace` / `Enter` 변환, 글자 → 키 위치 변환(Ctrl 조합용) |
| `lib/screens/remote_screen.dart` | 확대/이동 변환, 두 모드의 제스처 처리, 하단 바, 특수 키 줄, 보이지 않는 입력란 |

### 테스트

```powershell
cd C:\Users\kk\github\remodesk\client_app
flutter test
```

| 테스트 파일 | 내용 |
|---|---|
| `test/touch_gestures_test.dart` | 제스처 9가지 판정 (탭/느린 누르기/이동/길게 누르기/두 손가락 탭/스크롤/핀치/전환/취소) |
| `test/soft_keyboard_test.dart` | 글자, Backspace, Enter, 조합키 변환 |
| `test/remote_screen_test.dart` | **실제 원격 화면 위젯**에 터치와 키보드 입력을 주고, 테스트용 가짜 Host가 받은 메시지를 확인합니다. 실제 PC를 클릭하지 않습니다. 가짜 Host의 인증서는 `test/fixtures/`에 있는 **테스트 전용** 인증서입니다. |

휴대폰에서 확인할 항목:

| 테스트 | 예상 결과 |
|---|---|
| 터치패드 모드에서 한 손가락으로 문지르기 | 원격 커서가 따라 움직임 |
| 바탕 화면 아이콘 위에서 두 번 탭 | 프로그램 실행 |
| 길게 누른 뒤 창 제목 표시줄 끌기 | 창 이동 |
| 웹 페이지에서 두 손가락 위로 | 아래로 스크롤 |
| 메모장에서 키보드 → `안녕하세요` | 원격 메모장에 한글 입력 |
| `Ctrl` → `a`, `Ctrl` → `c` | 전체 선택, 복사 |
| 앱을 백그라운드로 보냄 | 눌려 있던 버튼/키가 원격에서 해제됨 |

### 문제 해결

| 증상 | 해결 |
|---|---|
| 커서가 너무 빠름/느림 | `remote_screen.dart`의 `_trackpadSpeed`(기본 1.6)를 조절합니다. 설정 화면은 이후 단계에서 추가합니다. |
| 스크롤 방향이 반대로 느껴짐 | `_wheelPerPixel` 부호를 바꿉니다. |
| 한글이 두 번 입력됨 | 블루투스 키보드와 가상 키보드를 함께 쓰면 그럴 수 있습니다. 가상 키보드를 닫으면 하드웨어 키보드는 키 위치로 전송됩니다. |
| 키보드가 안 올라옴 | `키보드` 버튼을 한 번 더 누릅니다. iOS에서 하드웨어 키보드가 연결되어 있으면 가상 키보드가 표시되지 않습니다. |

## STEP 6 - 인증 (PC ID · 비밀번호 · 신뢰된 장치 · 2단계 인증 · 승인)

### Host 설정

통합 앱의 [내 PC 원격 허용] 탭이나 콘솔 명령으로 설정합니다.

```powershell
cd C:\Users\kk\github\remodesk\windows
dotnet run --project .\Host\RemoteDesktop.Host -- password set         # 비밀번호 설정 (입력이 화면에 보이지 않음)
dotnet run --project .\Host\RemoteDesktop.Host -- totp enable          # 2단계 인증: 인증 앱에 키 등록 후 코드 확인
dotnet run --project .\Host\RemoteDesktop.Host -- devices              # 신뢰된 장치 목록
dotnet run --project .\Host\RemoteDesktop.Host -- devices revoke all   # 모두 해제 (실행 중인 Host에도 바로 적용)
dotnet run --project .\Host\RemoteDesktop.Host -- log 50               # 최근 접속 기록
dotnet run --project .\Host\RemoteDesktop.Host -- --require-approval --no-access-code   # 승인 필요, 비밀번호/장치만
```

| 로그인 방식 | 설명 |
|---|---|
| 접속 코드 | Host 실행마다 바뀌는 10자리. 잠깐 도움을 받을 때 |
| 비밀번호 | 내 PC에 자주 접속할 때. PBKDF2(200,000회)로만 저장 |
| 신뢰된 장치 | 로그인할 때 `이 PC(기기) 기억하기`를 켜면 다음부터 비밀번호 없이 연결. 90일 미사용 시 만료 |

- **2단계 인증**을 켜면 접속 코드·비밀번호 로그인에 6자리 코드가 필요합니다. 신뢰된 장치는 생략합니다.
- **접속 승인**을 켜면 Host 화면에 [허용]/[거부] 창이 뜹니다. 30초 후 자동으로 거부합니다.
- 실패가 반복되면 차단됩니다: IP당 5회 → 5분, 전체 30회/10분 → 2분.

### 테스트

| 테스트 | 예상 결과 |
|---|---|
| 비밀번호 로그인 | 연결됨. Host 로그 `method=password` |
| `이 PC 기억하기` 후 다시 연결 | 로그인 창 없이 연결 (`method=device`) |
| Host에서 `devices revoke all` 후 연결 | "신뢰된 장치 등록이 해제되었습니다" → 로그인 창 |
| 2단계 인증 켜고 코드 없이 | "2단계 인증 코드가 필요합니다" |
| 승인 켜고 Host에서 [거부] | "Host 사용자가 접속을 거부했습니다" |
| 틀린 비밀번호 5번 | 5분 동안 그 PC에서 연결 거부 |

자동 테스트: `AuthIntegrationTests`(C#), `auth_flow_test.dart`(Dart), `host_e2e_test.dart`(Dart ↔ 실제 C# Host).

## STEP 7 - 인터넷 연결 (시그널링 + WebRTC + STUN/TURN)

```text
Client ─wss─▶ 시그널링 서버 ◀─wss─ Host   (연결 협상만)
Client ◀═════ WebRTC Data Channel ═════▶ Host   (화면·입력, P2P. 안 되면 TURN 중계)
```

### 1) 시그널링 서버 실행

개발용 (같은 PC):

```powershell
cd C:\Users\kk\github\remodesk\signaling\Signaling.Server
dotnet run          # http://0.0.0.0:8080, WebSocket: ws://<서버IP>:8080/ws
```

운영 (Linux VM 예시): Docker로 실행하고, 앞에 HTTPS 리버스 프록시를 둡니다.

```bash
docker build -f signaling/Dockerfile -t remodesktop-signaling .
docker run -d --restart unless-stopped -p 127.0.0.1:8080:8080 -v remodesktop-signal:/app/data remodesktop-signaling
# Caddy 예: signal.example.com { reverse_proxy 127.0.0.1:8080 }  → wss://signal.example.com/ws
```

TURN(방화벽/대칭 NAT 환경용): coturn을 설치하고 `signaling/turnserver.conf.example`을 참고해 설정한 뒤,
시그널링 서버 `appsettings.json`의 `Signaling:TurnUrls`, `Signaling:TurnSecret`을 채웁니다.

### 2) Host

- 앱: [내 PC 원격 허용] → `인터넷 연결 허용` 체크. 시그널링 서버 주소는 [원격 PC에 연결] 탭 아래 칸에 입력합니다.
- 콘솔: `dotnet run --project .\Host\RemoteDesktop.Host -- --signal wss://signal.example.com/ws`

로그에 `시그널링 서버에 등록됨: HOST-XXXXXX`가 나오면 준비가 끝난 것입니다. Host는 서버로 **나가는** 연결만 하므로 공유기 포트 포워딩이 필요 없습니다.

### 3) Client

- Windows 앱: `PC 추가` → `인터넷 (Host ID)` → Host ID 입력. 아래 칸에 시그널링 서버 주소.
- 모바일/맥 앱: ⚙ 설정에 시그널링 서버 주소 → `PC 추가` → `인터넷` → Host ID.
- 목록의 Online/Offline은 시그널링 서버의 `/api/status`로 확인합니다.

### 문제 해결

| 증상 | 해결 |
|---|---|
| `오프라인입니다` | Host에서 인터넷 연결이 켜져 있고 등록 로그가 있는지 확인 |
| `WebRTC 연결 실패` / 시간 초과 | 회사망·모바일 데이터(대칭 NAT)면 TURN 서버가 필요합니다 |
| `이 Host ID는 다른 PC가 사용 중입니다` | 다른 PC가 같은 Host ID를 먼저 등록함. `host.json`을 지워 새 ID를 만들거나 서버 `data/hosts.json`에서 해당 항목 삭제 |
| 시그널링 서버 연결 실패 | 주소가 `wss://.../ws` 형식인지, 인증서가 유효한지 확인 |

자동 테스트: `InternetIntegrationTests` — 실제 시그널링 서버(Kestrel) + Host + WebRTC + Client로 1080p 프레임 전송, Host ID 탈취 방지, 오프라인 처리.

## STEP 8 - 성능 (Desktop Duplication · H.264 GPU 인코딩 · 적응형 화질)

| 항목 | 내용 |
|---|---|
| 캡처 | DXGI Desktop Duplication (GPU). 화면이 바뀔 때만 프레임. 실패하면 GDI |
| 인코딩 | H.264 Baseline, 저지연. GPU(NVENC/Quick Sync/AMF) → CPU(Microsoft) → JPEG 순서로 시도 |
| 디코딩 | Windows Client: Microsoft H.264 디코더. 모바일/맥: JPEG |
| 적응형 화질 | RTT·대기 비율로 5단계 (해상도 100→50%, 30→10 fps, 8→0.8 Mbps) |
| 상태 표시 | Host가 2초마다 FPS·전송량·RTT·단계·인코더를 보내고, Client 화면에 표시 |

이 PC(AMD Ryzen 5 5625U)에서 측정한 값:

| 측정 | 결과 |
|---|---|
| 캡처 (화면 변화 없을 때) | Desktop Duplication 0 ms / GDI 47 ms |
| 720p H.264 인코딩 | AMD GPU 9.6 ms, CPU 4.2 ms (프레임당) |
| 720p 디코딩 | 5~6 ms |
| 화질 (PSNR) | 약 33 dB |
| 1080p 같은 PC 왕복 | RTT 15 ms |

Host 옵션: `--codec auto|h264|jpeg`, `--bitrate 8000`, `--no-adaptive`, `--cpu-encoder`

자동 테스트: `MediaTests`(인코딩→디코딩 왕복), `CaptureTests`(DXGI vs GDI), `AdaptiveQualityTests`, `StreamingIntegrationTests`.

## STEP 9 - 고급 기능

| 기능 | Windows 앱 (원격 창 도구 모음) | 모바일/맥 앱 (메뉴) |
|---|---|---|
| 모니터 선택 / 전체 모니터 | `모니터` | `모니터 선택` |
| 클립보드 | `클립보드 공유` (양방향 자동) | PC → 기기 자동, `내 클립보드를 PC로 보내기` |
| 파일 보내기 | `파일 보내기` 또는 원격 화면에 끌어다 놓기 | `파일 보내기` |
| 파일 받기 | `파일 받기` → 내 PC의 다운로드 폴더 | `파일 받기` → 기기 다운로드/문서 폴더 |
| 소리 | `소리` | (향후) |
| 잠금 / 로그아웃 / 다시 시작 / 종료 | `전원` (확인 창) | `PC 화면 잠금` 등 |
| 특수 키 | `특수 키` (작업 관리자, Windows 키, Alt+Tab, 한/영) | 메뉴, 가상 키보드 특수 키 줄 |
| 연결 품질 | 도구 모음 오른쪽 | 제목 아래 |
| 자동 재연결 | 끊기면 최대 60초 동안 재시도 | 같음 |

- 파일은 Host의 **공유 폴더**(기본 `%USERPROFILE%\RemoteDesktop`)에서만 주고받습니다. 받은 파일은 `공유 폴더\Received`에 저장됩니다.
- 로그아웃·재시작·종료는 Host가 허용해야 합니다(앱 옵션 또는 `--allow-power`). 화면 잠금은 항상 허용합니다.

자동 테스트: `FeatureTests` — 클립보드 양방향·echo 없음, 300 KB 업로드/다운로드·SHA-256, 경로 공격 거부, 전원 권한, 오디오 시작, 보기 전용 모드.

## STEP 10 - 통합 Windows 앱 · 배포

### 통합 앱 RemoteDesktop.exe

```text
┌ Remote Desktop ───────────────────────────────────────────┐
│ [원격 PC에 연결] [내 PC 원격 허용]                           │
│                                                            │
│  ☑ 내 PC를 원격으로 허용                                     │
│  Host ID    HOST-939E16                                    │
│  접속 코드  K7MPQ-2XRTA  복사                                │
│  LAN 주소   192.168.123.108:50505                          │
│  현재 연결  PC-HOME (windows, lan)  [연결 끊기]               │
│  보안       [비밀번호 설정] [신뢰된 장치] [2단계 인증] [접속 기록] │
│  옵션       ☑ 접속 승인 ☐ 화면만 ☑ 클립보드 ☑ 파일 ...         │
└────────────────────────────────────────────────────────────┘
```

- 원격 허용이 켜져 있으면 창을 닫아도 알림 영역(트레이)에서 계속 실행됩니다. 완전히 끄려면 트레이 아이콘 → `종료`.
- `Windows 시작 시 자동 실행`을 켜면 로그인할 때 트레이에서 시작합니다(`--background`).
- 로그: `%LOCALAPPDATA%\RemoteDesktop\app.log`

### 배포 파일 만들기

```powershell
cd C:\Users\kk\github\remodesk
.\scripts\publish-windows.ps1
```

| 결과 (`dist\`) | 설명 |
|---|---|
| `RemoteDesktop-app-win-x64.zip` | 통합 앱 (단일 exe, .NET 설치 불필요, 약 70 MB) |
| `RemoteDesktop-host-win-x64.zip` | 콘솔 Host (무인 PC, 스크립트용) |
| `RemoteDesktop-signaling.zip` | 시그널링 서버 (`dotnet Signaling.Server.dll`) |

모바일/맥 앱:

```powershell
cd C:\Users\kk\github\remodesk\client_app
flutter build apk --release        # Android: build\app\outputs\flutter-apk\app-release.apk
```

```bash
flutter build ios --release         # Mac + Xcode, 서명 필요
flutter build macos --release       # Mac: build/macos/Build/Products/Release/Remote Desktop.app
```

### 처음 실행할 때 확인

1. Windows 방화벽 창: `개인 네트워크`만 체크하고 `액세스 허용`.
2. 관리자 권한 창(작업 관리자 등)까지 조작하려면 앱을 "관리자 권한으로 실행".
3. 인터넷으로 쓰려면: 비밀번호 + 2단계 인증을 켜고, 시그널링 서버(HTTPS)와 필요하면 TURN을 준비합니다. 자세한 내용은 [security.md](security.md)를 보세요.

## STEP 11 - 같은 네트워크에서 PC 자동 검색

Host가 켜져 있으면 UDP 50506으로 검색 요청에 응답합니다. Client는 버튼 하나로 같은 네트워크의 PC를 찾아 목록에 추가합니다.

| 앱 | 사용법 |
|---|---|
| Windows | [원격 PC에 연결] → `같은 네트워크에서 찾기` → 추가할 PC 선택 |
| 모바일/맥 | 위쪽 📶(Wi-Fi 찾기) 버튼 → 추가할 PC 선택 |
| Host 끄기 | 앱: "같은 네트워크에서 이 PC를 찾을 수 있게" 해제, 콘솔: `--no-discovery` |

| 증상 | 해결 |
|---|---|
| 찾은 PC가 없음 | 두 기기가 같은 공유기(Wi-Fi)에 있는지, Host의 원격 허용이 켜져 있는지 확인. Windows 방화벽에서 `RemoteDesktop`/`RemoteDesktop.Host`의 "개인 네트워크"를 허용했는지 확인 |
| iPhone에서만 안 됨 | iOS는 브로드캐스트에 별도 권한이 필요합니다. IP 주소로 직접 추가하세요 |
| 게스트 Wi-Fi / 회사망 | "단말 간 통신 차단(AP 격리)"이 켜져 있으면 검색과 연결이 모두 안 됩니다 |

자동 테스트: `DiscoveryTests`(C#: 형식, 실제 Host 검색, 끔 상태, 초당 제한), `discovery_test.dart`(Dart: 형식이 C#과 같은지, UDP 왕복, 실제 Host 브로드캐스트 검색).

## 이 PC에서 자동/실기 테스트로 확인한 것

| 항목 | 방법 | 결과 |
|---|---|---|
| 통합 앱 실제 사용 흐름 | UI 자동화(실제 마우스 클릭): 연결 → 로그인 → 지문 확인 → 원격 화면 → 도구 모음 → 닫기 | 통과, 원격 화면 1080p H.264 표시, "연결 좋음 · H264 · 16 ms" |
| GPU H.264 | AMD `AMDh264Encoder` → Windows 디코더 | 통과 (PSNR 약 33 dB) |
| 소리 | 톤 재생 → Client가 받은 PCM | 통과 (1.5초 재생 → 1.8초 분량 수신, 조용할 때는 0) |
| 파일·클립보드·전원·검색 | C# 79개 + Dart 47개 테스트 | 통과 |
| Dart 앱 ↔ C# Host | 접속 코드·비밀번호·신뢰 장치·파일·브로드캐스트 검색 | 통과 |
| 배포 exe | `dist\host\RemoteDesktop.Host.exe` 실행 후 연결 | 통과 |

## 집에서 직접 확인할 것 (이 PC 한 대로는 할 수 없는 것)

### A. Windows PC 두 대 (가장 먼저)

1. 두 PC에 저장소를 받아 `dotnet run --project .\windows\Client\RemoteDesktop.Client` 실행
2. 방화벽 창: **개인 네트워크만** 체크하고 허용 (두 PC 모두)
3. PC B: [내 PC 원격 허용] 켜기
4. PC A: `같은 네트워크에서 찾기` → PC B가 보이는지 → 추가 → 연결 → 접속 코드

| 확인 | 기대 결과 |
|---|---|
| 마우스 이동·클릭·드래그·휠 | PC B에서 같은 동작, 지연이 거의 없음 |
| 키보드, 한/영, Windows 키, Alt+Tab | PC B에서 동작 (원격 창이 활성일 때) |
| 동영상(유튜브) 재생 | 끊김 없이 보이는지, 도구 모음의 Mbps·ms 값 |
| `소리` 켜기 | PC B의 소리가 PC A 스피커로 나오는지 |
| PC A에서 복사 → PC B에서 붙여넣기 (반대도) | 텍스트가 옮겨짐 |
| `파일 보내기` / 원격 화면에 파일 끌어다 놓기 | PC B의 `%USERPROFILE%\RemoteDesktop\Received`에 저장 |
| `파일 받기` | PC B의 `%USERPROFILE%\RemoteDesktop` 파일이 PC A 다운로드 폴더로 |
| `전원 → 화면 잠금` | PC B가 잠김 (잠긴 화면은 볼 수 없음 → 안내 문구) |
| PC B Wi-Fi를 잠깐 껐다 켜기 | "다시 연결하는 중..." 후 자동 재연결 |
| 모니터가 여러 대면 `모니터` 메뉴 | 선택한 모니터로 바뀌고, 클릭 위치가 맞는지 |
| 로그인 시 `이 PC 기억하기` → 다시 연결 | 비밀번호 없이 연결 |

### B. Android 폰

1. 이 PC: Android Studio 설치(관리자 승인) → 처음 실행해 SDK 설치 → `flutter doctor --android-licenses`
2. 폰: 개발자 옵션 → USB 디버깅 켜기 → USB 연결
3. `cd C:\Users\kk\github\remodesk\client_app` → `flutter run`

| 확인 | 기대 결과 |
|---|---|
| 📶 버튼으로 PC 찾기 | 같은 Wi-Fi의 PC가 보임 |
| 터치패드 모드: 문지르기/탭/두 손가락 탭/두 손가락 스크롤/길게 눌러 끌기 | 커서 이동, 클릭, 우클릭, 스크롤, 드래그 |
| 핀치 확대 후 커서 이동 | 화면이 커서를 따라감 |
| 키보드: `안녕하세요`, `Ctrl` → `c` | 한글 입력, 복사 |
| 메뉴: 파일 보내기/받기, 클립보드 보내기 | 동작 |
| 커서 속도/스크롤 방향이 어색함 | `remote_screen.dart`의 `_trackpadSpeed`, `_wheelPerPixel` 조절 후 알려 주세요 |

### C. iPhone · 맥북 (Mac 필요)

```bash
git clone https://github.com/aseohyowon/remodesktop && cd remodesktop/client_app
flutter pub get
flutter run -d macos                 # 맥 앱
open ios/Runner.xcworkspace          # Xcode에서 Signing & Capabilities → Team 선택
flutter run -d <iPhone 기기 ID>
```

- macOS: 마우스·트랙패드 스크롤·키보드, ⌘C/⌘V → 원격 Ctrl+C/V
- iPhone: 처음 연결할 때 "로컬 네트워크" 권한 허용. PC 찾기(📶)는 iOS 권한 문제로 안 될 수 있으니 IP로 추가

### D. 인터넷 연결 (서로 다른 네트워크)

1. 공인 IP가 있는 서버(클라우드 VM 등)에 시그널링 서버 실행 + HTTPS(`wss://`) 구성 (STEP 7 참고)
2. PC B: [내 PC 원격 허용] → `인터넷 연결 허용`, 시그널링 서버 주소 입력
3. 휴대폰을 **모바일 데이터**로 바꾸고 Host ID로 연결
4. 연결이 안 되면 TURN(coturn) 설정 후 다시 시도

