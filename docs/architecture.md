# 아키텍처

## 1. 전체 구성

```text
                         ┌──────────────────────────┐
                         │  시그널링 서버 (선택)       │  Host 등록(ECDSA), 연결 중계(SDP/ICE),
                         │  signaling/Signaling.Server│  온라인 상태, TURN 임시 자격 증명
                         └─────────▲─────────▲──────┘
                    WebSocket(wss) │         │ WebSocket(wss)
                                   │         │
┌──────────────────────┐   ① LAN: TLS/TCP 50505    ┌──────────────────────────┐
│ Client               │◀────────────────────────▶│ Host (Windows)            │
│ - Windows 앱          │   ② 인터넷: WebRTC        │ - 화면 캡처 (DXGI / GDI)    │
│ - iPhone/Android/Mac │◀════ Data Channel ══════▶│ - H.264(GPU/CPU) / JPEG    │
│   (Flutter)          │   (P2P, 안 되면 TURN)      │ - 입력 실행 (SendInput)     │
└──────────────────────┘                          │ - 인증, 클립보드, 파일, 소리 │
                                                  └──────────────────────────┘
```

- **Host**: 원격으로 제어당하는 Windows PC. 통합 앱(`RemoteDesktop.exe`)의 [내 PC 원격 허용] 또는 콘솔 `RemoteDesktop.Host.exe`.
- **Client**: Windows 앱, iPhone/Android/macOS 앱(Flutter). 모두 같은 프로토콜을 씁니다.
- **전송 계층은 두 가지**지만 그 위의 프로토콜(인증·화면·입력·부가 기능)은 하나입니다.
  - LAN: TCP + TLS 1.2/1.3. Host 인증서를 TOFU로 확인.
  - 인터넷: 시그널링 서버로 협상한 WebRTC Data Channel (DTLS로 암호화). Host의 포트를 열 필요가 없습니다.

## 2. 연결 순서

```text
Client                               Host
  | -- 전송 연결 (TLS 또는 WebRTC) -----> |
  | -- hello (nonce, 지원 코덱) ---------> |
  | <- auth_challenge (nonce, 인증 방식) -- |
  | -- auth_response (HMAC 증명) -------> |   접속 코드 / 비밀번호(PBKDF2) / 신뢰된 장치
  |                                      |   (+ 2단계 인증, Host 사용자 승인)
  | <- auth_result (Host 증명, 모니터, 기능) |
  | <- 영상 프레임 / stream_stats ---------- |
  | -- frame_ack / 입력 / 기능 메시지 ----> |
```

## 3. 저장소 구조

```text
remodesktop/
├── windows/                                   .NET 8 (C#)
│   ├── RemoteDesktop.sln
│   ├── Core/RemoteDesktop.Core                로그, 설정 경로, DPAPI, 원자적 파일 저장
│   ├── Protocol/RemoteDesktop.Protocol        framing, 메시지, 인증 증명, 키 변환표, 시그널링 메시지
│   ├── Transport/RemoteDesktop.Transport.WebRtc  WebRTC(SIPSorcery) Data Channel ↔ Stream, 시그널링 소켓
│   ├── Media/RemoteDesktop.Media              H.264 인코더/디코더(Media Foundation), NV12 변환
│   ├── Host/RemoteDesktop.Host.Engine         Host 엔진 (라이브러리): 인증, 세션, 캡처, 입력, 기능
│   ├── Host/RemoteDesktop.Host                콘솔 Host + 관리 명령 (password, devices, totp, log)
│   ├── Client/RemoteDesktop.Client            통합 Windows 앱 RemoteDesktop.exe (Host + Client)
│   └── Tests/RemoteDesktop.Tests              xUnit 단위/통합 테스트
├── client_app/                                Flutter: iPhone · Android · macOS Client
├── signaling/Signaling.Server                 ASP.NET Core 시그널링 서버 (+ Dockerfile, coturn 예시)
├── scripts/publish-windows.ps1                배포 파일 만들기
└── docs/                                      architecture / protocol / security / development
```

## 4. Host 엔진 내부

| 클래스 | 역할 |
|---|---|
| `HostServer` | LAN 리스너(사설망만, 동시 핸드셰이크 제한), 시그널링 연결, 세션 슬롯(1명), 모니터 목록 |
| `DiscoveryResponder` | LAN 자동 검색 응답 (UDP 50506, 사설망만, 초당 5회 제한) |
| `DisplayAndPower` | 해상도 변경(`ChangeDisplaySettingsEx`, CDS_TEST 확인 → 동적 변경 → 세션 끝에 복원), 세션 중 절전 방지(`SleepBlocker`) |
| `WakeOnLan` (Protocol) | MAC 주소 확인, 매직 패킷 만들기·보내기 |
| `SignalingHost` | 시그널링 서버 등록(ECDSA 서명), 연결 요청마다 WebRTC offer 생성, 재접속(최대 60초 간격) |
| `HostAuthenticator` | 인증 절차, 2단계 인증, 승인, 장치 등록, 실패 지연/차단, 코덱 협상 |
| `HostSession` | 세션 실행: 영상·입력·기능, 세션 만료, Host 사용자의 연결 끊기 |
| `VideoStreamer` | 캡처 → 축소 → H.264/JPEG → 전송, ack 기반 흐름 제어, 적응형 화질, 상태 전송 |
| `DxgiCapturer` / `ScreenCapturer` | Desktop Duplication(기본) / GDI(대체) 캡처 |
| `AdaptiveQuality` | RTT·대기 비율로 해상도·FPS·비트레이트 5단계 조절 |
| `InputInjector` | 입력 메시지 → `SendInput` (모니터 좌표 변환, 끊길 때 키 해제) |
| `SessionFeatures` | 클립보드(`ClipboardSync`), 파일(`FileReceiver`, 공유 폴더), 소리(`AudioStreamer`), 전원 |
| `HostSettings` | Host ID, 비밀번호 키, 장치, TOTP, 시그널링 키 (DPAPI 암호화, 외부 변경 자동 반영) |
| `AuthThrottle` | IP별 5회/5분, 전체 30회/10분 → 2분 차단 |
| `DesktopThread` | 입력 데스크톱(Default ↔ Winlogon)을 따라가는 전용 스레드. 서비스 모드에서 캡처·입력을 여기서 실행 |
| `AgentSupervisor` | 서비스: 콘솔 세션에 에이전트 하나 유지 (세션 변경 시 교체, 죽으면 1~30초 간격 재시작) |
| `SessionAgentLauncher` | SYSTEM 토큰 복제 + 세션 번호 변경 → `CreateProcessAsUser`, Job Object(서비스 종료 시 에이전트도 종료) |
| `HostMediaSession` / `MediaTrackSender` | (STEP 14) 모바일·맥용 WebRTC 미디어 트랙 협상, H.264 RTP/Opus 송신, RTCP 보고서·PLI 처리, 실패 시 채널로 복귀 |
| `OpusPacketizer` | 시스템 소리(float, 장치 샘플레이트) → 48 kHz 스테레오 Opus 20 ms 패킷 (Concentus) |
| `SasPipe` | 에이전트 → 서비스 Ctrl+Alt+Del 요청 (이름 있는 파이프, 같은 계정만, 에이전트 PID 확인, 2초 제한) → `SendSAS` |

### Windows 서비스 모드 (STEP 13)

```text
 세션 0 (화면 없음)                          사용자 세션 (콘솔)
┌──────────────────────────────┐           ┌──────────────────────────────────────────┐
│ 서비스 RemoteDesktopHost      │  실행/감시 │ 에이전트 (SYSTEM)                          │
│  RemoteDesktop.Host.exe       ├──────────►│  RemoteDesktop.Host.exe agent --pipe ...  │
│  service run                  │           │  = HostServer (LAN/인터넷, 인증, 영상)      │
│  - AgentSupervisor            │◄──────────┤  - DesktopThread: Default ↔ Winlogon      │
│  - SasPipeServer → SendSAS    │ 파이프"sas"│    (로그인·잠금·UAC 화면 캡처/입력)         │
└──────────────────────────────┘           └──────────────────────────────────────────┘
 설정: C:\ProgramData\RemoteDesktop (SYSTEM·Administrators만, DPAPI 이 PC 범위)
```

- 서비스는 화면이 없는 세션 0에서 실행되므로 직접 캡처할 수 없습니다. 그래서 사용자 세션에 에이전트를 띄웁니다.
- 에이전트는 SYSTEM 권한이라 Winlogon 데스크톱(로그인/잠금/UAC 화면)을 열 수 있습니다. 화면이 바뀌면 `DesktopThread`가 `SetThreadDesktop`으로 옮기고 캡처 장치를 다시 만듭니다.
- Ctrl+Alt+Del(`SendSAS`)은 서비스만 호출할 수 있어 파이프로 서비스에 요청합니다.
- 서비스 모드에는 승인 창을 띄울 사람이 없을 수 있으므로 접속 코드와 승인 기능을 쓰지 않고 비밀번호/신뢰된 장치/2단계 인증으로만 접속합니다.

## 5. 흐름 제어와 지연

- Host는 ack를 받지 못한 프레임을 **최대 2개**까지만 보냅니다. Client는 **화면에 표시한 뒤** ack를 보냅니다.
- 네트워크·Client가 느리면 Host는 캡처 자체를 늦춥니다. 그래서 오래된 화면이 쌓이지 않습니다.
  H.264는 프레임끼리 의존하므로 인코딩한 프레임은 버리지 않습니다.
- `AdaptiveQuality`는 1초마다 RTT(전송 시간 포함)와 credit 대기 비율을 보고 단계를 바꿉니다.

| 단계 | 해상도 | FPS | JPEG 품질 | H.264 |
|---|---|---|---|---|
| 0 | 100% | 30 | 75 | 8 Mbps |
| 1 | 100% | 24 | 65 | 5 Mbps |
| 2 | 75% | 20 | 55 | 3 Mbps |
| 3 | 50% | 15 | 45 | 1.5 Mbps |
| 4 | 50% | 10 | 35 | 0.8 Mbps |

- 입력 메시지는 영상과 같은 연결을 쓰지만 크기가 작고, Client는 연속된 마우스 이동을 합쳐서 보냅니다.

## 6. 코덱 선택

| Client | 코덱 | 이유 |
|---|---|---|
| Windows | **H.264** (Media Foundation 디코더) | 대역폭이 JPEG의 약 1/5~1/10 |
| iPhone/Android/macOS | JPEG | Flutter에서 바로 디코딩. H.264는 네이티브 디코더 플러그인이 필요 (향후) |

Host는 `hello.codecs`를 보고 고릅니다. GPU 인코더(NVENC/Quick Sync/AMF)를 먼저 시도하고, 실패하면 CPU 인코더, 그래도 실패하면 JPEG로 바꿉니다.

## 6-1. 모바일·맥 영상 경로 (STEP 14)

```text
 Host                                                     앱 (Flutter, flutter_webrtc = libwebrtc)
 캡처 → H.264 인코더 ─┬─ 채널(TLS/DataChannel) 프레임 ──► (JPEG만 표시 가능, 대체 경로)
                      └─ WebRTC 영상 트랙(RTP, SRTP) ─────► 하드웨어 디코더 → RTCVideoView
 WASAPI → Opus ────────── WebRTC 오디오 트랙 ────────────► 기기 스피커
           ▲ RTCP RR(RTT·손실) / PLI ◄────────────────────
```

앱은 연결 직후 JPEG로 화면을 보다가, 미디어 연결이 되면(`media_state` active) 영상 트랙으로 바꿉니다.
미디어 연결이 안 되거나 끊기면 다시 JPEG로 돌아가므로 어떤 네트워크에서도 화면은 보입니다.

## 7. 스레드

| 위치 | 스레드 |
|---|---|
| Host 세션 | 수신 루프(입력·기능), 전송 루프(`Task.Run`), 클립보드 STA 스레드, 오디오 캡처 스레드. 서비스 모드는 캡처용·입력용 `DesktopThread` 추가 |
| Windows Client | 수신 루프(디코딩) → UI 스레드(`BeginInvoke`)에서 그리기/ack, 입력 전송 큐, 키보드 훅(UI 스레드) |
| Flutter | 소켓/WebRTC 이벤트 → 이미지 디코딩(엔진) → setState. PBKDF2는 별도 isolate |
