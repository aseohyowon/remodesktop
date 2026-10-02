# remodesktop

Windows PC를 Host(원격 제어 대상) 또는 Client(원격 제어 장치)로 사용하고, Windows·iPhone·Android에서 같은 서비스와 프로토콜로 연결하는 원격 데스크톱 프로젝트입니다.

> **개발 원칙:** 우선 같은 LAN에서 Windows 화면을 전송하고 마우스 입력을 처리합니다. 그 뒤 모바일, 인증, 인터넷 연결 순서로 확장합니다. STEP 1의 현재 구현은 캡처 동작 확인용 Windows Host 프로토타입입니다.

## 1. 전체 시스템 아키텍처

Host는 Windows 화면을 캡처·인코딩하고 인증된 Client의 입력을 처리합니다. Client는 Host에 연결해 영상을 표시하고 입력 이벤트를 보냅니다. 제어 메시지와 영상은 분리된 논리 채널로 다루되 공통 Protocol을 사용합니다.

```text
Windows Client ─┐                 ┌─ Windows Host
iPhone Client ──┼── 연결/전송 ────┤  캡처·인코딩·입력·인증
Android Client ─┘                 └─ 필요 시 Signaling 및 TURN
```

한 Windows 설치본에서 Host와 Client 기능을 모두 제공할 수 있습니다. Host 기능은 사용자가 명시적으로 켜고 끌 수 있게 하고, 첫 MVP는 로그인된 대화형 사용자 세션에서 실행합니다.

## 2. Windows Host와 Client

**Host**는 화면 캡처, H.264 인코딩, 영상 전송, 인증 및 세션 관리, 입력 메시지 검증과 Windows 입력 API 호출을 담당합니다. **Client**는 Host 검색·연결·인증, 영상 디코딩·표시, 마우스·키보드 이벤트 전송을 담당합니다. 공통 메시지 타입·좌표 규칙·세션 상태는 `Protocol`에 둡니다.

초기에는 Host와 Client를 별도 실행 대상으로 개발하고, 기능이 안정화되면 한 Windows 앱에서 둘 다 실행할 수 있도록 구성합니다. 로그인 화면이나 UAC 보안 데스크톱 등 다른 보안 세션을 제어하는 기능은 일반 사용자 앱과 분리해 검토해야 합니다.

## 3. iPhone Client

iPhone 앱은 PC 목록과 온라인 상태, 인증, 화면 표시, 터치 기반 포인터, 가상 키보드, 확대/축소, 스크롤, 전체 화면 및 연결 종료를 제공합니다. 화면 탭은 클릭, 두 손가락 탭은 우클릭, 두 손가락 이동은 휠 입력으로 매핑할 수 있습니다. iOS 백그라운드 실행에는 제약이 있으므로 원격 Host가 아니라 연결을 시작하는 Client로 설계합니다.

## 4. Android Client

Android도 iPhone과 동일한 Protocol·화면 흐름·기본 입력 제스처를 사용합니다. 기기별 화면 비율과 키보드, 백그라운드 네트워크 동작은 플랫폼별 테스트가 필요합니다. Android 앱 역시 기본적으로 Client 역할을 합니다.

## 5. 모바일 프레임워크 추천

**Flutter를 우선 추천합니다.** 하나의 Dart 코드베이스에서 iOS와 Android UI 및 연결 흐름을 공유하기 쉽고, 원격 화면처럼 커스텀 렌더링과 입력 UI가 많은 앱에 적합합니다. 네이티브 영상 디코딩·키보드 동작이 필요한 부분은 플러그인 또는 플랫폼 채널로 보완합니다.

React Native는 JavaScript/TypeScript 생태계와 네이티브 모듈을 활용할 수 있지만, 고성능 화면 표시를 위해 네이티브 연결 작업이 필요할 수 있습니다. Kotlin Multiplatform은 비즈니스 로직 공유에는 강점이 있지만 UI까지 최대한 공유하려는 목표에는 Flutter보다 구성이 복잡할 수 있습니다. MVP는 Flutter로 시작하고 실제 WebRTC 플러그인의 iOS/Android 지원 범위를 초기에 검증합니다.

## 6. Windows 화면 캡처

Windows 10 버전 1903 이상을 우선 대상으로 **Windows Graphics Capture (WGC)** 를 검토합니다. 최신 Windows 그래픽 경로와 프레임 업데이트를 활용하기 적합합니다. 모니터 단위 캡처와 낮은 지연이 더 중요한 경우 **Desktop Duplication API**를 대안으로 평가합니다. 캡처 경로는 인터페이스 뒤에 두어 OS 버전·드라이버별 대체가 가능하도록 합니다.

초기 캡처 검증은 화면을 파일로 저장해 정확성을 확인하고, 이후 프레임을 인코더로 전달합니다. STEP 1은 외부 패키지 없이 동작하는 GDI 캡처 프로토타입이며, 전체 가상 화면을 BMP로 저장합니다. 영상 스트리밍에 진입하기 전 WGC를 구현해 프레임 캡처 경로를 교체·검증합니다. 캡처 프레임과 사용자 세션이 분리되는 서비스 구조는 피하고, 첫 구현은 로그인된 데스크톱에서 실행합니다.

## 7. 영상 인코딩

MVP 영상 코덱은 **H.264**로 고정합니다. 하드웨어 인코더(NVIDIA NVENC, Intel Quick Sync, AMD AMF)를 사용할 수 있으면 우선 사용하고, 사용할 수 없거나 초기화에 실패하면 CPU 소프트웨어 인코더로 전환합니다. 실제 지원 여부는 GPU/드라이버/Windows 환경에서 확인해야 합니다.

H.265는 압축률이 좋지만 기기별 지원·라이선스·호환성 고려가 더 필요하고, VP8/VP9 및 AV1은 첫 MVP에서 지원 범위를 늘리는 비용이 큽니다. 우선 H.264로 낮은 지연을 검증한 뒤 네트워크 상태에 따라 bitrate·FPS·해상도를 조절하고, 필요하면 keyframe 요청과 프레임 드롭을 적용합니다.

## 8. 네트워크 프로토콜

LAN MVP는 Host가 명시적으로 시작하는 **TLS 보호 TCP 연결**로 구현할 수 있습니다. 프레임 길이와 메시지 유형이 포함된 framing을 정의하고, 영상 프레임과 제어 메시지는 별도 논리 스트림으로 분리합니다. TCP의 재전송으로 영상이 밀릴 수 있으므로 오래된 프레임은 버리고, 전송 계층을 이후 WebRTC로 교체할 수 있게 Protocol과 transport를 분리합니다.

인터넷 연결 단계에서는 영상은 WebRTC Video Track, 마우스·키보드 및 세션 제어는 WebRTC Data Channel을 우선 검토합니다. Data Channel은 입력 메시지에 순서가 필요한지에 따라 reliable/ordered 설정을 구분합니다.

좌표는 화면 내 위치를 0~1로 정규화해 보내고 Host 측에서 선택된 모니터의 실제 좌표로 변환합니다. 예시 제어 메시지:

```json
{"type":"mouse_move","x":0.52,"y":0.31}
{"type":"mouse_click","button":"left"}
{"type":"key_down","key":"CTRL"}
```

메시지 버전, 세션 ID, 입력 타입 검증, 메시지 크기 제한과 sequence/timestamp 규칙도 Protocol에 정의합니다. 입력 메시지는 신뢰된 세션 안에서만 허용합니다.

## 9. WebRTC 사용 여부

**인터넷 단계에서 WebRTC를 권장합니다.** 영상은 Video Track의 RTP/SRTP 경로로 보내고, 입력 및 제어 이벤트는 Data Channel로 보냅니다. WebRTC는 ICE를 통한 연결 후보 수집과 DTLS-SRTP 암호화를 제공해 낮은 지연의 실시간 미디어 전송에 적합합니다.

WebRTC는 Host의 캡처·인코더 연결, 각 플랫폼의 API 차이, 연결 협상 구현이 필요하므로 LAN 첫 화면 검증을 복잡하게 만들 수 있습니다. 따라서 초기 LAN은 단순한 TLS 전송으로 검증하되 공통 Protocol과 전송 인터페이스를 유지한 뒤, LAN에서도 WebRTC를 검증하고 인터넷 전송으로 확장합니다. TCP 기반 MVP를 최종 실시간 전송으로 간주하지 않습니다.

## 10. Signaling Server가 필요한 이유

WebRTC 양측이 세션을 찾고 SDP offer/answer와 ICE 후보를 교환할 통로가 필요합니다. Signaling Server는 인증된 Host ID와 Client를 연결하고 이 협상 정보를 전달합니다. 서버는 가능한 한 최소한의 연결 중개 역할만 하며, 영상·입력 데이터는 직접 전달 경로를 우선합니다. Signaling 메시지에도 세션 권한과 만료를 적용합니다.

## 11. STUN과 TURN이 필요한 이유

STUN은 NAT 뒤의 장치가 외부에서 도달 가능한 주소 후보를 확인하는 데 도움을 주며, ICE가 직접 연결을 시도할 수 있게 합니다. 라우터 정책이나 대칭 NAT 등으로 직접 연결이 실패하면 **TURN relay**가 데이터를 중계합니다. 따라서 TURN은 연결 성공률을 높이는 fallback이지만 트래픽 비용과 지연이 발생합니다. 실패 시 상태를 사용자에게 알리고 재시도·relay fallback을 처리합니다.

## 12. 인증 구조

각 Host에 추측하기 어려운 고유 UUID/Host ID를 부여하되 ID 자체를 비밀이나 인증 수단으로 취급하지 않습니다. 등록된 Client가 Host를 선택하면 Host가 접속을 승인하고, 이후 만료되는 세션 자격 증명으로 연결합니다. 장치 등록·신뢰 장치·접속 기록·선택적 2FA는 이후 단계에서 추가합니다.

비밀번호를 평문으로 저장하거나 로그에 남기지 않습니다. 비밀번호 기반 인증이 필요하면 서버 측에는 적절한 salt와 비용 설정을 적용한 Argon2id 같은 password hash를 저장하고, 네트워크에서는 TLS 또는 WebRTC의 암호화된 경로를 사용합니다. 세션 토큰은 짧은 만료, 폐기 및 재발급 정책을 갖도록 하고, 반복 실패에는 rate limit과 지연/일시 차단을 적용합니다.

## 13. 보안 구조

인터넷에서 Host의 원격 제어 포트를 직접 노출하는 것을 기본값으로 하지 않습니다. 전송 암호화 외에도 사용자 승인, 최소 권한, 세션 만료, replay 방지용 nonce/sequence, 입력 검증, 크기 제한, rate limit 및 민감 정보 제거 로그를 적용합니다. Host ID 검색 결과만으로 접속을 승인하지 않습니다.

Windows `SendInput`은 일반 데스크톱 입력에 쓸 수 있지만, 보안 데스크톱·로그온 화면·UAC 화면은 일반 앱에서 제어할 수 없습니다. `Ctrl+Alt+Delete`의 Secure Attention Sequence도 일반 앱이 키 입력을 합성해 대체할 수 없습니다. 이를 우회하지 않고 지원 범위를 명확히 표시합니다. 화면 잠금·로그아웃·종료 등은 별도 권한과 사용자 확인을 요구하는 후속 기능으로 설계합니다.

## 14. LAN 개발 방법

1. Windows Host에서 전체 가상 화면 캡처 결과를 BMP 파일로 저장해 화면과 다중 모니터 동작을 확인합니다. 현재는 GDI 프로토타입이며 영상 전송 전 WGC로 교체합니다.
2. Host와 Windows Client를 같은 Wi-Fi/LAN에 두고 TLS 연결로 프레임 전송·표시를 확인합니다.
3. 제어 메시지를 분리해 정규화 좌표의 마우스 이동과 클릭을 추가하고, Host에서 허용된 사용자 세션에만 적용합니다.
4. 전송 계층과 Protocol을 분리한 상태에서 Flutter Client를 연결하고 iPhone·Android 실기기에서 표시·터치 입력을 검증합니다.
5. LAN에서도 WebRTC 연결 및 H.264 하드웨어/소프트웨어 인코딩을 비교한 뒤 인터넷 연결로 확장합니다.

LAN 검색에는 UDP Broadcast 또는 mDNS/Bonjour를 사용할 수 있습니다. 검색은 발견 편의 기능일 뿐 인증이 아닙니다. Host는 사용자 동의 없이 제어 연결을 허용하지 않고, 네트워크 인터페이스와 방화벽 규칙을 제한합니다.

## 15. 인터넷 원격접속 방법

Client와 Host가 Signaling Server에서 인증·세션 협상을 수행하고, SDP와 ICE 후보를 교환합니다. ICE는 STUN을 사용해 직접 경로를 시도하고, 실패하면 TURN relay를 사용합니다. 미디어와 입력 채널은 WebRTC의 암호화된 전송을 사용하며, Host가 오프라인·절전·재부팅 상태면 온라인 상태와 재연결 안내를 표시합니다.

## 16. Windows → Windows 기능

Windows Client에서 전체 화면, 실제 포인터, 키보드, 다중 모니터 선택, 연결 품질 표시와 자동 재연결을 구현할 수 있습니다. 후속 기능으로 원격 해상도 변경, 클립보드, 파일 전송, 오디오, 잠금·로그아웃·재부팅·종료를 고려합니다. 입력 지연을 줄이기 위해 별도 제어 채널, 입력 우선순위, 전송 큐에서 오래된 영상 프레임 제거를 적용합니다.

모니터별 선택과 좌표 변환을 우선 구현하고, 전체 모니터 보기 및 해상도 변경은 Host 환경별 지원을 확인한 뒤 추가합니다. 로그아웃·재부팅·종료 같은 기능은 세션 단절과 권한 영향이 있으므로 별도 사용자 확인이 필요합니다.

## 17. iPhone·Android 기능

두 모바일 Client에서 PC 목록/상태, 인증, 원격 화면, 터치 포인터, 왼쪽 클릭, 우클릭, 드래그, 휠, 더블 탭, 확대/축소, 가상 키보드, 전체 화면, 연결 종료와 자동 재연결을 목표로 합니다. 텍스트 클립보드는 후속 기능입니다.

모바일 OS 키보드와 원격 Windows 키의 차이 때문에 모든 조합키를 동일하게 보장할 수는 없습니다. 일반 키·수정키부터 제공하고 특수키는 별도 UI와 플랫폼별 테스트로 지원 범위를 정합니다. 특히 `Ctrl+Alt+Delete`는 Windows 보안 제약상 일반 원격 입력으로 제공할 수 없습니다.

## 18. 단계별 개발 계획 및 난이도

| 단계 | 범위 | 예상 난이도 |
|---|---|---|
| 1 | Windows Host, 화면 캡처 및 파일 저장 | 중 |
| 2 | Windows ↔ Windows LAN 영상 전송·표시 | 중상 |
| 3 | LAN 마우스·키보드 입력, 좌표·권한 처리 | 상 |
| 4 | Flutter 프로젝트와 iOS/Android 화면 수신 | 상 |
| 5 | 모바일 터치·가상 키보드 입력 | 상 |
| 6 | Host ID, 인증, 세션 및 접속 승인 | 상 |
| 7 | Signaling, ICE/STUN/TURN, 인터넷 WebRTC 연결 | 매우 상 |
| 8 | H.264 하드웨어 인코딩, 적응형 화질·지연 최적화 | 매우 상 |
| 9 | 파일·클립보드·오디오·다중 모니터·전원 기능 | 상~매우 상 |

각 단계는 작은 기능으로 나눠 검증합니다. 모바일 앱과 인터넷 연결은 LAN에서 Windows 영상·입력 경로가 확인된 다음 시작합니다. 파일 전송과 오디오는 MVP 범위에 포함하지 않습니다.

## 19. 프로젝트 폴더 구조

```text
remodesktop/
├── windows/
│   ├── RemoteDesktop.sln
│   ├── Host/
│   │   └── RemoteDesktop.Host/
│   │       ├── RemoteDesktop.Host.csproj
│   │       └── Program.cs
│   ├── Client/
│   │   └── RemoteDesktop.Client/
│   ├── Core/
│   │   └── RemoteDesktop.Core/
│   └── Protocol/
│       └── RemoteDesktop.Protocol/
├── mobile/
│   └── remodesktop_mobile/       # Flutter iOS/Android 앱
├── signaling/
│   └── Signaling.Server/
├── docs/
│   ├── architecture.md
│   ├── protocol.md
│   ├── security.md
│   └── development.md
└── README.md
```

## 20. STEP 1 - Windows Host 화면 캡처

### 생성한 프로젝트와 파일

```text
windows/
├── RemoteDesktop.sln
└── Host/
    └── RemoteDesktop.Host/
        ├── RemoteDesktop.Host.csproj
        └── Program.cs
```

`RemoteDesktop.Host.csproj`는 .NET 8 콘솔 프로젝트 설정입니다. `Program.cs`는 Windows GDI API로 현재 로그인된 데스크톱의 전체 가상 화면을 한 번 캡처해 32비트 BMP 파일로 저장합니다. 별도 NuGet 패키지는 사용하지 않습니다. `windows/RemoteDesktop.sln`은 Visual Studio에서 솔루션 전체를 여는 파일입니다.

현재 작업 폴더가 저장소의 최상위 폴더인 PowerShell에서 실행합니다. 저장소를 다른 위치에 clone했어도 같은 명령을 사용할 수 있습니다.

```powershell
dotnet build ".\windows\RemoteDesktop.sln"
dotnet run --project ".\windows\Host\RemoteDesktop.Host\RemoteDesktop.Host.csproj" -- (Join-Path $PWD "captures\step1.bmp")
Get-Item ".\captures\step1.bmp" | Select-Object FullName, Length
Start-Process ".\captures\step1.bmp"
```

`dotnet run`은 프로젝트를 빌드한 다음 캡처 프로그램을 실행합니다. 출력 폴더가 없으면 자동으로 만듭니다. 파일 인수를 생략하면 실행 중인 현재 폴더의 `captures`에 시간 정보를 붙인 파일명을 사용합니다. 캡처 대상은 주 모니터에 한정하지 않고 Windows 가상 화면 경계 전체이므로, 배치된 여러 모니터가 하나의 이미지에 포함됩니다.

### 테스트 및 예상 결과

1. Windows 10/11에 .NET 8 SDK를 설치하고, 실제 데스크톱에 로그인한 상태로 위 명령을 실행합니다.
2. 빌드가 성공하고 `화면 캡처 완료` 및 이미지의 픽셀 크기가 출력되는지 확인합니다.
3. `Get-Item`에서 파일 크기가 0보다 큰지 확인하고, 이미지 뷰어에서 현재 화면과 모니터 배치가 보이는지 확인합니다.

Linux 개발 환경에서는 솔루션을 빌드하고 `dotnet run ... -- --help`로 사용법을 확인할 수 있지만 실제 Windows 화면 캡처는 실행할 수 없습니다.

### 자주 발생하는 문제

* `dotnet` 명령을 찾지 못하면 Windows에 .NET 8 SDK를 설치하고 새 PowerShell 창에서 다시 실행합니다.
* 권한 오류가 나면 저장소 폴더에 쓰기 권한이 있는지 확인하고, 출력 BMP 경로가 아닌 저장소 최상위 폴더에서 명령을 실행했는지 확인합니다.
* 확장자 오류가 나면 출력 파일 이름 끝이 `.bmp`인지 확인합니다.
* 검은 화면이나 캡처 실패가 있으면 Windows 데스크톱에 로그인되어 있고 화면이 잠기지 않았는지 확인합니다. 이 프로토타입은 서비스나 잠금/UAC 보안 데스크톱 캡처용이 아닙니다. 보호된 화면이나 일부 GPU 표면은 캡처되지 않을 수 있습니다.
* BMP는 압축되지 않아 해상도가 높을수록 파일이 큽니다. 이 단계의 결과 파일을 저장소에 추가하지 마세요.

이 단계의 목표는 화면 캡처와 파일 저장을 확인하는 것입니다. GDI 방식은 이 검증용 기반 구현이며, 실시간 프레임 전달을 시작하기 전에 Windows Graphics Capture(WGC) 기반 캡처로 발전시킵니다.
