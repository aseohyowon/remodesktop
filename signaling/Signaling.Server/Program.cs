using Signaling.Server;

// 시그널링 서버
// - Host 등록(ECDSA 서명으로 Host ID 소유 증명), Client 연결 요청 중계(SDP/ICE), 온라인 상태 조회
// - 화면/입력 데이터는 이 서버를 지나지 않습니다(WebRTC P2P 또는 TURN).
// - 운영 환경에서는 반드시 HTTPS(wss://) 뒤에서 실행하세요. (docs/development.md STEP 7 참고)

var app = SignalingApp.Build(args);
app.Run();
