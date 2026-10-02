# 보안

## 1. 원칙

- 인터넷에 Host의 원격 제어 포트를 열지 않습니다. LAN 리스너는 **사설망·루프백 주소만** 받습니다(`--allow-public`으로만 해제).
  인터넷 연결은 Host가 시그널링 서버로 **나가는** 연결을 맺고 WebRTC로 연결합니다.
- 모든 통신은 암호화됩니다: LAN = TLS 1.2/1.3, 인터넷 = WebRTC DTLS.
- 비밀값(접속 코드·비밀번호·장치 비밀키)은 **네트워크로 보내지 않습니다.** HMAC 증명값만 보냅니다.
- 비밀번호는 저장하지 않습니다. PBKDF2 결과만 Windows DPAPI로 암호화해 저장합니다.
- 비밀번호, 접속 코드, 증명값, 토큰, 입력한 키와 문자는 **로그에 남기지 않습니다.**

## 2. 인증

### 방식

| 방식 | 키 | 비고 |
|---|---|---|
| 접속 코드 | SHA-256("remodesktop-access-code-v1:" + 코드) | Host 실행마다 새 10자리(약 49비트). `--no-access-code`로 끄기 |
| 비밀번호 | PBKDF2-HMAC-SHA256(비밀번호, salt 16B, 200,000회) | Host에서 `password set` 또는 앱의 [비밀번호 설정] |
| 신뢰된 장치 | 32바이트 무작위 비밀키 | 로그인 시 "기억하기"로 발급. 90일 미사용 시 만료, Host에서 언제든 해제 |

### 증명 (상호 인증 + 재전송 방지 + 채널 바인딩)

```text
proof      = HMAC-SHA256(key, "remodesktop-auth-v1:client" || client_nonce || server_nonce || channel_binding)
host_proof = HMAC-SHA256(key, "remodesktop-auth-v1:host"   || client_nonce || server_nonce || channel_binding)
channel_binding = LAN: SHA-256(Host TLS 인증서)
                  인터넷: SHA-256("remodesktop-webrtc-v1|" + Host DTLS 지문 + "|" + Client DTLS 지문)
```

- **재전송 방지**: 양쪽 nonce가 연결마다 새로 만들어집니다.
- **중간자 방지**: 채널 바인딩 때문에 중간자가 자기 TLS/DTLS로 중계하면 증명이 맞지 않습니다.
  시그널링 서버가 SDP를 바꿔치기해도 마찬가지입니다.
- **상호 인증**: Host도 같은 키를 안다는 `host_proof`를 보냅니다. 인증에 성공한 경우에만 보냅니다(오프라인 대입 재료 방지).
- **LAN TOFU**: 처음 연결 시 Host 인증서 지문을 확인하고 저장합니다. 같은 Host ID의 지문이 바뀌면 연결을 거부합니다.

### 추가 확인

- **2단계 인증(TOTP, RFC 6238)**: 접속 코드·비밀번호 로그인 시 6자리 코드. ±30초 허용, 같은 코드 재사용 불가.
- **접속 승인**: 신뢰된 장치가 아니면 Host 화면에서 [허용]을 눌러야 합니다. 30초 후 자동 거부.
- 신뢰된 장치는 등록할 때 2단계 인증과 승인을 이미 거쳤으므로 다음부터 생략합니다.

### 무차별 대입 방지

- 실패 응답은 1초 늦게 보냅니다.
- 같은 IP 5회 실패 → 5분 차단. 모든 IP 합계 10분 동안 30회 실패 → 2분 전체 차단.
- 인증 전 동시 연결은 16개까지, TLS 핸드셰이크 10초, 인증 15초 제한.
- 시그널링 서버: IP당 연결 요청 분당 20회, 연결당 메시지 10초당 200개, 메시지 64 KB 제한.

### 세션

- 세션 최대 12시간(`--session-hours`). 지나면 다시 인증해야 합니다.
- 한 번에 한 Client만 연결할 수 있습니다.
- Host 사용자는 언제든 [연결 끊기]를 할 수 있습니다.
- 모든 로그인·실패·거부·파일·전원 동작은 접속 기록(`access-log.jsonl`)에 남습니다.

## 3. 저장 데이터

| 파일 (`%LOCALAPPDATA%\RemoteDesktop`) | 내용 | 보호 |
|---|---|---|
| `host.json` | Host ID, 비밀번호 키, 장치 비밀키, TOTP 비밀키, 시그널링 서명 키 | 비밀값은 DPAPI(현재 사용자) |
| Windows 인증서 저장소 (현재 사용자\개인) | LAN TLS 인증서와 개인 키 | Windows가 사용자 계정으로 보호 |
| `devices.json` (Client) | 신뢰된 장치 자격 증명 | DPAPI |
| `known_hosts.json` (Client) | Host ID → 인증서 지문 | 비밀 아님 |
| `access-log.jsonl` | 접속 기록 | 비밀값 없음 |
| 서비스 모드 `C:\ProgramData\RemoteDesktop` (STEP 13) | `host.json`, TLS 인증서(`host-certificate.protected`), 접속 기록, 로그 | 비밀값은 DPAPI(**이 PC 범위**). 대신 폴더 권한을 SYSTEM과 Administrators만으로 제한(상속 끊음) |
| 모바일 앱 | 장치 비밀키 | iOS/macOS 키체인, Android Keystore (flutter_secure_storage) |

`host.json`은 실행 중인 Host 옆에서 바뀌어도(예: `devices revoke`) 다음 인증부터 바로 반영됩니다.

## 4. 기능별 위험 관리

| 기능 | 제한 |
|---|---|
| 입력 | `--view-only`면 모든 입력과 클립보드·파일·전원 기능을 끕니다 |
| 파일 | Host의 **공유 폴더 안에서만** 주고받습니다. 경로·`..`·예약 이름(CON, NUL…)을 거부합니다. 4 GB 제한, SHA-256을 확인한 뒤에만 저장, 같은 이름은 덮어쓰지 않습니다 |
| 클립보드 | 텍스트만, 32,000자 제한. 옵션으로 끌 수 있습니다 |
| 전원 | 화면 잠금만 기본 허용. 로그아웃/재시작/종료는 `--allow-power`(앱의 옵션) 필요 |
| 소리 | 옵션으로 끌 수 있습니다 |

## 5. Windows 보안상 제약

- **UIPI**: 일반 권한 Host는 관리자 권한 창을 조작할 수 없습니다. 필요하면 Host를 관리자 권한으로 실행합니다.
- **보안 데스크톱**: 로그인 화면, 잠금 화면, UAC 확인 창은 일반 프로그램이 캡처하거나 조작할 수 없습니다.
  → Windows 서비스 모드(STEP 13)에서 지원합니다.
- **Ctrl+Alt+Del**: Secure Attention Sequence라 키 입력으로 만들 수 없습니다. 일반 모드에서는 [작업 관리자(Ctrl+Shift+Esc)]와 [화면 잠금]을 제공합니다.
  → 서비스 모드에서는 서비스가 `SendSAS`로 보냅니다 (Windows 정책 `SoftwareSASGeneration`=1 필요, 설치 스크립트가 설정하고 제거 시 원래 값으로 되돌림).

### 미디어 트랙 (STEP 14)

- 미디어 연결의 SDP(DTLS 지문 포함)는 이미 상호 인증·암호화된 채널 안에서만 주고받으므로, 영상·소리를 받는 상대는 인증된 Client뿐입니다.
- 영상·소리는 DTLS-SRTP로 암호화됩니다. 인증 전에는 `media_offer`를 받지 않고, 세션마다 한 번만 만들 수 있습니다.
- LAN 연결이면 ICE 서버 없이 같은 네트워크 주소로만 연결됩니다.

### Windows 서비스 모드의 보안 설계 (STEP 13)

서비스 모드의 에이전트는 SYSTEM 권한이므로 원격 사용자는 사실상 PC 전체를 다룰 수 있습니다. 그래서:

| 위험 | 대책 |
|---|---|
| 화면 앞에 아무도 없을 때 접속 | 접속 코드·승인을 쓰지 않고 **비밀번호(필수) / 신뢰된 장치 / 2단계 인증**만 허용. 설치 스크립트가 비밀번호를 먼저 설정 |
| 실행 파일 바꿔치기로 SYSTEM 권한 탈취 | 실행 파일을 `C:\Program Files\RemoteDesktop`(관리자만 쓰기 가능)에 복사해 그 경로로 서비스 등록 |
| 설정 파일 탈취·변조 | `C:\ProgramData\RemoteDesktop` 폴더 권한을 SYSTEM·Administrators만으로 제한. 설정 변경(`service password` 등)은 관리자만 |
| 다른 프로그램이 Ctrl+Alt+Del 요청 | 파이프는 같은 계정(SYSTEM)만 연결(`CurrentUserOnly`), 연결한 프로세스가 서비스가 띄운 에이전트인지 PID로 확인, 2초에 한 번 |
| 서비스가 죽었는데 에이전트만 남음 | 에이전트를 Job Object(KILL_ON_JOB_CLOSE)에 넣어 서비스와 함께 종료 |
| 인터넷 노출 | 방화벽 규칙은 개인 네트워크만. 인터넷은 시그널링(WebRTC) 사용 시에만, 이때는 2단계 인증 권장 |

## 6. 운영 권장 사항

1. 시그널링 서버는 반드시 HTTPS(`wss://`) 뒤에서 실행합니다 (Caddy/nginx 리버스 프록시).
2. TURN(coturn)은 `use-auth-secret`과 사설망 중계 금지(`denied-peer-ip`)를 설정합니다 (`signaling/turnserver.conf.example`).
3. 인터넷으로 쓸 Host는 **비밀번호 + 2단계 인증**을 켜고, 접속 코드는 끄는 것(`--no-access-code`)을 권장합니다.
4. 쓰지 않는 신뢰된 장치는 해제합니다 ([신뢰된 장치] 또는 `devices revoke`).
5. 접속 기록을 주기적으로 확인합니다.

## 7. 알려진 한계

- 접속 코드(약 49비트)는 중간자가 증명값 하나를 가로챈 뒤 오프라인 대입을 시도하면 이론상 공격받을 수 있습니다.
  LAN TOFU와 채널 바인딩이 1차 방어입니다. 인터넷 사용 시에는 비밀번호(PBKDF2) + 2단계 인증을 권장합니다.
- 비밀번호 방식은 PAKE(OPAQUE/SRP)가 아니므로, Host의 `host.json`과 Windows 사용자 계정이 함께 유출되면 비밀번호 키로 로그인할 수 있습니다.
- 시그널링 서버의 Host ID 등록은 "처음 등록한 키가 주인"(TOFU) 방식입니다. Host ID는 6자리 16진수이므로 공용 서버라면 더 긴 ID를 권장합니다.
