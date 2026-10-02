# RemoteDesktop Protocol v1

Windows·iPhone·Android·macOS Client가 모두 이 규칙으로 Host와 통신합니다.

| 구현 | 위치 |
|---|---|
| C# | `windows/Protocol/RemoteDesktop.Protocol` |
| Dart | `client_app/lib/protocol` |

두 구현은 같은 테스트 벡터로 검증합니다(`AuthProofTests`, `protocol_test.dart`, `webrtc_binding_test.dart`).

## 1. 전송 계층

| 경로 | 방식 | channel binding |
|---|---|---|
| LAN | TCP 50505 + TLS 1.2/1.3 (Host 자체 서명 RSA-2048), TCP_NODELAY | SHA-256(Host 인증서 DER) |
| 인터넷 | WebRTC Data Channel `remodesktop` (ordered, reliable), 시그널링으로 협상 | SHA-256("remodesktop-webrtc-v1\|" + offer 지문 + "\|" + answer 지문) |

WebRTC Data Channel은 메시지 단위이므로, 아래 바이트 스트림을 **16 KB 조각**으로 나눠 보내고 받는 쪽이 이어 붙입니다.

## 2. Framing

```text
+----------------+-----------+------------------------+
| length (u32)   | kind (u8) | payload (length-1 B)   |
+----------------+-----------+------------------------+
```

정수는 모두 **big-endian**입니다(오디오 PCM만 little-endian). `length`는 kind 1바이트를 포함하며 최대 16 MB입니다.

| kind | 이름 | payload |
|---|---|---|
| 1 | Control | UTF-8 JSON (최대 64 KB) |
| 2 | VideoFrame | 21바이트 헤더 + 압축 영상 |
| 3 | FileChunk | `[transfer_id u32][offset u64][data]` (data 최대 64 KB) |
| 4 | Audio | `[sequence u32][PCM 16bit LE, interleaved]` |

모르는 kind와 모르는 JSON `type`은 무시합니다(하위 호환).

### VideoFrame 헤더 (21바이트)

| offset | 크기 | 필드 | 설명 |
|---|---|---|---|
| 0 | u32 | frame_id | 1부터 증가 |
| 4 | i64 | capture_time_ms | Host 캡처 시각 (Unix ms) |
| 12 | i32 | width | 이 프레임의 픽셀 크기 (적응형 화질로 줄어들 수 있음) |
| 16 | i32 | height | |
| 20 | u8 | codec | 1 = JPEG (프레임마다 완전한 JPEG), 2 = H.264 (Annex-B, Baseline) |

## 3. Control 메시지 (JSON)

- 속성 이름은 `snake_case`입니다. **`type`은 첫 번째 속성이어야 합니다**(.NET 8 System.Text.Json 제약).
- 바이너리 값(nonce, proof, secret)은 표준 Base64입니다.

### 인증 (STEP 2·6)

| type | 방향 | 필드 |
|---|---|---|
| `hello` | C→H | `protocol_version`, `client_name`, `platform`(windows/ios/android/macos), `client_nonce`(32B), `codecs`(["h264","jpeg"]) |
| `auth_challenge` | H→C | `protocol_version`, `host_id`, `host_name`, `server_nonce`(32B), `auth_methods`(["access_code","password","device"]), `password_salt`(16B), `password_iterations`, `totp_required` |
| `auth_response` | C→H | `proof`(32B), `method`, `device_id`?, `totp`?, `register_device`, `device_name`? |
| `auth_result` | H→C | `success`, `error`?, `error_code`?, `session_id`, `host_proof`, `screen_width`, `screen_height`, `device_id`?, `device_secret`?, `monitors`, `video_codec`, `features` |

`error_code`: `invalid_credentials`, `totp_required`, `device_revoked`(Client는 저장된 장치 자격 증명을 지움), `denied`, `busy`, `locked`, `unsupported`

`features`: `input`, `clipboard`, `file_transfer`, `audio`, `power`, `monitor_select`

`monitors`: `[{"index":0,"x":0,"y":0,"width":1920,"height":1080,"primary":true}]`

### 화면과 상태 (STEP 2·8)

| type | 방향 | 필드 |
|---|---|---|
| `frame_ack` | C→H | `frame_id` — 화면에 표시한 뒤 보냄 |
| `keyframe_request` | C→H | — 디코딩 오류 시 다음 프레임을 H.264 IDR로 |
| `stream_stats` | H→C (2초마다) | `fps`, `kbps`, `rtt_ms`, `quality_level`(0 최고~4), `codec`, `width`, `height`, `encoder`? |
| `select_monitor` | C→H | `index` (-1 = 모든 모니터) |
| `bye` | 양방향 | `reason` |

### 입력 (STEP 3)

| type | 필드 |
|---|---|
| `mouse_move` | `x`, `y` (보고 있는 모니터 기준 0~1) |
| `mouse_button` | `button`(left/right/middle/x1/x2), `action`(down/up) |
| `mouse_wheel` | `delta_x`, `delta_y` (120 = 한 칸, 양수 = 오른쪽/위) |
| `key_down` / `key_up` | `code` (W3C `KeyboardEvent.code`: KeyA, ControlLeft, MetaLeft, Lang1(한/영) …) |
| `text_input` | `text` (최대 256자, 완성된 문자열) |

클릭은 down+up, 더블 클릭은 클릭 두 번, 드래그는 down → move… → up입니다. 연결이 끊기면 Host가 눌린 키와 버튼을 모두 뗍니다.

### 부가 기능 (STEP 9)

| type | 방향 | 필드 |
|---|---|---|
| `clipboard` | 양방향 | `text` (최대 32,000자) |
| `file_list_request` | C→H | — |
| `file_list` | H→C | `folder`, `files`: [{`name`, `size`, `modified`}] |
| `file_download` | C→H | `name` (공유 폴더 바로 아래 파일만) |
| `file_begin` | 양방향 | `transfer_id`, `name`, `size`, `direction`(upload/download) |
| `file_end` | 양방향 | `transfer_id`, `sha256` (16진수) |
| `file_result` | 받는 쪽 → 보낸 쪽 | `transfer_id`, `success`, `error`?, `saved_as`? |
| `file_cancel` | 양방향 | `transfer_id`, `reason`? |
| `audio` | C→H | `action`: start/stop |
| `audio_format` | H→C | `codec`("pcm16"), `sample_rate`, `channels` |
| `power_action` | C→H | `action`: lock/logoff/restart/shutdown |
| `power_result` | H→C | `action`, `success`, `error`? |

업로드는 Host의 `공유 폴더\Received`에 저장하고, 다운로드는 `공유 폴더`의 파일만 보낼 수 있습니다.

## 4. 인증 계산

```text
key (access_code) = SHA-256("remodesktop-access-code-v1:" + 영문/숫자만 남긴 대문자 코드)
key (password)    = PBKDF2-HMAC-SHA256(UTF-8 비밀번호, password_salt, password_iterations, 32)
key (device)      = device_secret (32B)

proof      = HMAC-SHA256(key, "remodesktop-auth-v1:client" || client_nonce || server_nonce || channel_binding)
host_proof = HMAC-SHA256(key, "remodesktop-auth-v1:host"   || client_nonce || server_nonce || channel_binding)
```

테스트 벡터 (client_nonce = 0x00..0x1F, server_nonce = 0x20..0x3F, binding = 0x40..0x5F):

| 입력 | 결과 (Base64) |
|---|---|
| client, access_code "k7mpq-2xrta" | `+sGhO+oce/K8SdCSIcU+oTKQhL6fMoYk5Hu5FQKV6uA=` |
| host, access_code "K7MPQ2XRTA" | `VcGqC8qG/WWWUZ+wzsahA4Mfp4fICp8duVC/ZUlTDsQ=` |

WebRTC binding 벡터: offer 지문 `AA:BB:CC`, answer 지문 `11:22:33` → `258C072E6C22600CC44BBCA029B82DE62BFF054609541C029E3A1E14762A54C6`

## 5. 흐름 제어

- Host는 ack를 받지 못한 프레임을 최대 2개까지만 보냅니다. 보내지 않은 번호의 ack는 무시합니다.
- 화면이 바뀌지 않으면 보내지 않고, 1초마다 한 프레임만 보냅니다(연결 확인).
- JPEG Client는 디코딩 중 새 프레임이 오면 건너뛸 수 있습니다. 건너뛴 프레임도 반드시 ack합니다.

## 6. 시그널링 (WebSocket `/ws`, JSON, `type` 첫 속성)

| type | 방향 | 필드 |
|---|---|---|
| `host_hello` | Host→S | `host_id`, `public_key` (ECDSA P-256 SPKI Base64) |
| `host_challenge` | S→Host | `nonce` |
| `host_auth` | Host→S | `signature` = ECDSA-SHA256("remodesktop-signal-v1:" + host_id + ":" + nonce) |
| `host_registered` | S→Host | `host_id` |
| `connect` | C→S | `host_id` |
| `connecting` | S→C | `session_id`, `ice_servers` (Client에 먼저 보냄) |
| `connect_request` | S→Host | `session_id`, `client_address`, `ice_servers` |
| `sdp` | Host↔C | `session_id`, `sdp_type`(offer: Host만 / answer: Client만), `sdp` |
| `ice` | Host↔C | `session_id`, `candidate`, `sdp_mid`, `sdp_mline_index` |
| `session_end` | 양방향 | `session_id`, `reason` |
| `status` / `status_result` | C↔S | `host_ids` / `online`: {id: bool} |
| `error` | S→ | `code`(offline, rate_limited, bad_request, unauthorized, session_not_found, host_id_taken), `message` |

`ice_servers`: `[{"urls":["stun:..."]}, {"urls":["turn:..."], "username":"만료시각:세션", "credential":"Base64(HMAC-SHA1(secret, username))"}]`

HTTP: `GET /api/status?ids=HOST-A,HOST-B` → `{"online":{"HOST-A":true,...}}`, `GET /health`

## 7. LAN 자동 검색 (UDP 50506, STEP 11)

```text
Client → 255.255.255.255:50506 (Windows는 각 네트워크의 브로드캐스트 주소에도)
  {"type":"rd_discover","version":1,"nonce":"16진수"}
Host → Client (유니캐스트)
  {"type":"rd_host","version":1,"nonce":"같은 값","host_id":"HOST-...","host_name":"PC 이름","port":50505}
```

- Host는 사설망·루프백 주소의 요청에만, 같은 주소에 초당 5번까지만 응답합니다. 응답에 비밀 정보는 없습니다.
- Client는 자기가 보낸 nonce가 든 응답만 받습니다.
- 검색은 편의 기능일 뿐 인증이 아닙니다. 연결은 평소처럼 TLS + 인증을 거칩니다.
- Host 옵션 `--no-discovery`(앱: "같은 네트워크에서 이 PC를 찾을 수 있게" 해제)로 끌 수 있습니다.
- iOS에서 브로드캐스트를 보내려면 Apple의 Multicast Networking 권한(`com.apple.developer.networking.multicast`)이 필요합니다.

