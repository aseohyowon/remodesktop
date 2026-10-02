# remodesktop

직접 만드는 원격 데스크톱입니다. **Windows PC**를 Host(원격으로 제어당하는 PC)로 두고, **Windows · iPhone · Android · macOS**에서 같은 프로토콜과 인증으로 접속합니다. 상용 프로그램(AnyDesk, TeamViewer 등)을 쓰지 않습니다.

```text
[Windows PC A] ⇄ [Windows PC B]        Windows는 Host도, Client도 될 수 있습니다
[iPhone] ─┐
[Android] ─┼──▶ [Windows PC]           모바일·맥은 Client
[macOS]  ─┘
```

## 주요 기능

| 영역 | 기능 |
|---|---|
| 화면 | Desktop Duplication 캡처, **H.264 GPU 인코딩**(NVENC/Quick Sync/AMF, 없으면 CPU), JPEG 대체, 1080p, 적응형 해상도·FPS·비트레이트, 프레임 드롭 |
| 입력 | 마우스(이동·클릭·우클릭·더블 클릭·드래그·휠·가운데·앞/뒤 버튼), 키보드(조합키, Windows 키, Alt+Tab, 한/영, F키), 다중 모니터 좌표 |
| 모바일 | 터치패드/직접 터치 모드, 두 손가락 우클릭·스크롤, 길게 눌러 드래그, 핀치 확대, 가상 키보드(한글 조합, Ctrl 등 특수 키) |
| 연결 | **같은 네트워크 PC 자동 검색**, LAN 직접 연결(TLS), **인터넷 연결**(시그널링 서버 + WebRTC P2P, STUN/TURN), 자동 재연결, 연결 품질 표시 |
| 인증 | Host ID, 접속 코드, 비밀번호(PBKDF2), 신뢰된 장치, **2단계 인증(TOTP)**, 접속 승인, 실패 차단, 세션 만료, 접속 기록 |
| 부가 | 클립보드 공유, 파일 보내기/받기(SHA-256 검증), 소리 전송, 모니터 선택, **원격 PC 해상도 변경**(끊으면 복원), 연결 중 절전 방지, **Wake-on-LAN(PC 켜기)**, 화면 잠금·로그아웃·재시작·종료 |
| 앱 | 통합 Windows 앱(내 PC 원격 허용 + 원격 PC에 연결, 트레이, 자동 실행), Flutter 앱(iPhone·Android·macOS) |

## 빠른 시작 (Windows, 같은 네트워크)

준비: .NET 8 SDK (`winget install --id Microsoft.DotNet.SDK.8 -e`)

```powershell
cd C:\Users\kk\github\remodesk
dotnet run --project .\windows\Client\RemoteDesktop.Client
```

1. **원격으로 제어당할 PC**: [내 PC 원격 허용] 탭 → `내 PC를 원격으로 허용` 체크 → 화면의 Host ID, 접속 코드, LAN 주소 확인
2. **접속할 PC**: [원격 PC에 연결] 탭 → `같은 네트워크에서 찾기`(또는 `PC 추가`로 IP 입력) → `연결` → 접속 코드 입력
3. 처음 실행할 때 Windows 방화벽 창이 뜨면 `개인 네트워크`만 체크하고 허용

배포용 실행 파일은 `.\scripts\publish-windows.ps1`로 만듭니다(`dist\`).

## 문서

| 문서 | 내용 |
|---|---|
| [docs/development.md](docs/development.md) | **단계별 실행·테스트·문제 해결** (STEP 1~11), 모바일 빌드, 배포, **집에서 확인할 것** |
| [docs/architecture.md](docs/architecture.md) | 전체 구조, 프로젝트별 역할, 흐름 제어, 코덱 선택, 스레드 |
| [docs/protocol.md](docs/protocol.md) | 프로토콜 명세 (framing, 메시지, 인증 계산, 시그널링, 테스트 벡터) |
| [docs/security.md](docs/security.md) | 보안 설계, 저장 데이터, Windows 제약, 운영 권장 사항, 알려진 한계 |

## 저장소 구조

```text
remodesktop/
├── windows/                  .NET 8 (C#) — RemoteDesktop.sln
│   ├── Client/               통합 Windows 앱 RemoteDesktop.exe
│   ├── Host/                 Host 엔진 + 콘솔 Host
│   ├── Protocol/ Core/ Media/ Transport/   공통 라이브러리
│   └── Tests/                xUnit 테스트 79개
├── client_app/               Flutter 앱 (iPhone · Android · macOS), 테스트 47개
├── signaling/                시그널링 서버 (ASP.NET Core, Dockerfile, coturn 예시)
├── scripts/                  배포 스크립트
└── docs/
```

## 기술 선택

| 항목 | 선택 | 이유 |
|---|---|---|
| Windows | C# / .NET 8, WinForms | Windows API(캡처·입력·DPAPI) 접근이 쉽고 단일 exe 배포 |
| 모바일 | **Flutter** | 하나의 코드로 iOS·Android·macOS, 커스텀 화면/제스처 구현이 쉬움 |
| 캡처 | DXGI Desktop Duplication (+ GDI 대체) | GPU 합성 결과를 직접, 변경이 있을 때만 |
| 영상 | H.264 Baseline (Media Foundation) + JPEG | 하드웨어 가속, 저지연. JPEG는 모바일 호환용 |
| 인터넷 | WebRTC Data Channel (SIPSorcery / flutter_webrtc) | NAT 통과(ICE/STUN/TURN), DTLS 암호화, P2P |
| 시그널링 | ASP.NET Core WebSocket | 최소 역할: 등록·중계·상태 |

## 개발 단계

| 단계 | 내용 | 상태 |
|---|---|---|
| 1 | Windows 화면 캡처·저장 | ✅ |
| 2 | Windows → Windows LAN 화면 전송 (TLS, 접속 코드 상호 인증) | ✅ |
| 3 | 마우스·키보드 입력 | ✅ |
| 4 | Flutter 앱 (iPhone·Android·macOS) | ✅ |
| 5 | 모바일 터치·가상 키보드 | ✅ |
| 6 | 인증: 비밀번호, 신뢰된 장치, 2단계 인증, 승인, 접속 기록 | ✅ |
| 7 | 인터넷: 시그널링 서버, WebRTC, STUN/TURN | ✅ |
| 8 | 성능: Desktop Duplication, H.264 GPU, 적응형 화질 | ✅ |
| 9 | 클립보드, 파일, 소리, 모니터 선택, 전원, 자동 재연결 | ✅ |
| 10 | 통합 Windows 앱, 트레이, 자동 실행, 배포 스크립트 | ✅ |
| 11 | 같은 네트워크 PC 자동 검색 (UDP 브로드캐스트) | ✅ |
| 12 | 원격 PC 해상도 변경(끊으면 복원), 세션 중 절전 방지, Wake-on-LAN | ✅ |
| 13 | Windows 서비스 + 세션 에이전트: 로그인·잠금·UAC 화면, Ctrl+Alt+Del | ✅ |

## Windows 보안상 제약

일반 프로그램(앱의 [내 PC 원격 허용], 콘솔 Host)으로 실행하면:

- **관리자 권한 창**(작업 관리자 등)은 조작할 수 없습니다(UIPI).
- **잠금 화면·로그인 화면·UAC 확인 창**(보안 데스크톱)은 캡처·조작할 수 없습니다.
- **Ctrl+Alt+Del**은 보낼 수 없습니다. 대신 `작업 관리자(Ctrl+Shift+Esc)`와 `화면 잠금`을 제공합니다.

**Windows 서비스로 설치하면(STEP 13, `scripts\install-service.ps1`) 위 제약이 모두 풀립니다.** 로그인 전 화면부터 원격으로 쓸 수 있고 PC를 다시 시작해도 자동으로 실행됩니다. 자세한 내용은 [docs/security.md](docs/security.md)에 있습니다.

## 향후 과제

- 모바일·맥 앱의 H.264 디코딩(네이티브 플러그인)과 소리 재생
- 비밀번호 인증을 PAKE(OPAQUE)로 강화
