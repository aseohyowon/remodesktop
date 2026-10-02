# Remote Desktop Client (Flutter)

iPhone · Android · macOS에서 Windows Host에 접속하는 앱입니다.

- LAN(IP) 또는 인터넷(Host ID, 시그널링 서버 + WebRTC) 연결
- 접속 코드 / 비밀번호 / 신뢰된 장치 로그인, 2단계 인증
- 터치패드·직접 터치 모드, 가상 키보드(한글), macOS 마우스·키보드
- 클립보드, 파일 보내기/받기, 모니터 선택, 전원 메뉴, 연결 품질, 자동 재연결

프로토콜은 [../docs/protocol.md](../docs/protocol.md), 실행·테스트 방법은 [../docs/development.md](../docs/development.md)를 참고하세요.

```bash
flutter pub get
flutter test
flutter run
```
