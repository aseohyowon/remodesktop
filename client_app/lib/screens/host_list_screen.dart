// 첫 화면: 등록된 PC 목록, 온라인 상태, 추가/편집/삭제, 연결

import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:http/http.dart' as http;

import '../protocol/auth.dart';
import '../protocol/connection.dart';
import '../protocol/protocol.dart';
import '../protocol/webrtc_transport.dart';
import '../storage/host_store.dart';
import 'remote_screen.dart';

enum _Status { checking, online, offline }

class HostListScreen extends StatefulWidget {
  const HostListScreen({super.key});

  @override
  State<HostListScreen> createState() => _HostListScreenState();
}

class _HostListScreenState extends State<HostListScreen> {
  final _store = HostStore();
  final _knownHosts = PrefsKnownHosts();
  final _devices = SecureDeviceCredentials();
  List<SavedHost> _hosts = [];
  final Map<String, _Status> _status = {};
  bool _loading = true;
  Uri? _signaling;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final hosts = await _store.load();
    final signaling = await _store.loadSignaling();
    if (!mounted) return;
    setState(() {
      _hosts = hosts;
      _signaling = signaling;
      _loading = false;
    });
    await _refreshStatus();
  }

  Future<void> _refreshStatus() async {
    setState(() {
      for (final host in _hosts) {
        _status[host.id] = _Status.checking;
      }
    });

    final internet = _hosts.where((h) => h.isInternet).toList();
    await Future.wait([
      // LAN PC: 포트가 열려 있는지 확인
      ..._hosts.where((h) => !h.isInternet).map((host) async {
        final online = await probeHost(host.address, host.port);
        if (mounted) setState(() => _status[host.id] = online ? _Status.online : _Status.offline);
      }),
      // 인터넷 PC: 시그널링 서버에 한 번에 조회
      if (internet.isNotEmpty) _refreshInternetStatus(internet),
    ]);
  }

  Future<void> _refreshInternetStatus(List<SavedHost> hosts) async {
    Map<String, dynamic> online = {};
    final signaling = _signaling;
    if (signaling != null) {
      try {
        final response = await http
            .get(statusUri(signaling, hosts.map((h) => h.hostId!).toList()))
            .timeout(const Duration(seconds: 5));
        if (response.statusCode == 200) {
          online = (jsonDecode(response.body) as Map<String, dynamic>)['online'] as Map<String, dynamic>;
        }
      } catch (_) {}
    }
    if (!mounted) return;
    setState(() {
      for (final host in hosts) {
        _status[host.id] = online[host.hostId] == true ? _Status.online : _Status.offline;
      }
    });
  }

  Future<void> _editSettings() async {
    final controller = TextEditingController(text: _signaling?.toString() ?? '');
    final value = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('설정'),
        content: TextField(
          controller: controller,
          keyboardType: TextInputType.url,
          autocorrect: false,
          decoration: const InputDecoration(
            labelText: '시그널링 서버 (인터넷 연결용)',
            hintText: 'wss://signal.example.com/ws',
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('취소')),
          FilledButton(onPressed: () => Navigator.pop(context, controller.text), child: const Text('저장')),
        ],
      ),
    );
    controller.dispose();
    if (value == null) return;
    final uri = Uri.tryParse(value.trim());
    if (value.trim().isNotEmpty && (uri == null || (uri.scheme != 'ws' && uri.scheme != 'wss'))) {
      _showMessage('ws:// 또는 wss:// 로 시작하는 주소를 입력하세요.');
      return;
    }
    await _store.saveSignaling(value);
    setState(() => _signaling = value.trim().isEmpty ? null : uri);
    await _refreshStatus();
  }

  // ---------------- PC 추가/편집/삭제 ----------------

  Future<void> _editHost([SavedHost? existing]) async {
    final result = await showDialog<SavedHost>(
      context: context,
      builder: (_) => _HostEditDialog(existing: existing),
    );
    if (result == null) return;

    setState(() {
      final index = _hosts.indexWhere((h) => h.id == result.id);
      if (index >= 0) {
        _hosts[index] = result;
      } else {
        _hosts.add(result);
      }
    });
    await _store.saveAll(_hosts);
    await _refreshStatus();
  }

  Future<void> _deleteHost(SavedHost host) async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('PC 삭제'),
        content: Text('${host.name}을(를) 목록에서 삭제할까요?\n저장된 인증서 지문도 함께 삭제됩니다.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('취소')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('삭제')),
        ],
      ),
    );
    if (ok != true) return;

    setState(() => _hosts.removeWhere((h) => h.id == host.id));
    await _store.saveAll(_hosts);
    if (host.hostId != null) {
      await _knownHosts.forget(host.hostId!);
      await _devices.remove(host.hostId!);
    }
  }

  // ---------------- 연결 ----------------

  Future<void> _connect(SavedHost host) async {
    // 신뢰된 장치로 등록된 PC는 비밀번호 없이 바로 연결합니다.
    final trusted = host.hostId != null && await _devices.has(host.hostId!);
    if (!mounted) return;

    LoginRequest? login = trusted ? const LoginRequest(method: AuthMethods.accessCode, secret: '') : null;
    login ??= await showDialog<LoginRequest>(context: context, builder: (_) => _LoginDialog(hostName: host.name));
    if (login == null || !mounted) return;

    final result = await _runConnect(host, login);
    if (!mounted) return;

    // 장치 등록이 해제되었으면 로그인 창을 다시 띄웁니다.
    if (result is ConnectionFailed && result.errorCode == AuthErrorCodes.deviceRevoked && trusted) {
      _showMessage(result.message);
      final retry = await showDialog<LoginRequest>(context: context, builder: (_) => _LoginDialog(hostName: host.name));
      if (retry == null || !mounted) return;
      await _openOrShow(host, await _runConnect(host, retry));
      return;
    }
    await _openOrShow(host, result);
  }

  LoginRequest? _lastLogin;

  /// 진행 표시 없이 연결 (자동 재연결용)
  Future<RemoteHostConnection> _connectQuietly(SavedHost host, LoginRequest login) async {
    final prompts = _DialogPrompts(context);
    final ClientTransport transport;
    if (host.isInternet) {
      final signaling = _signaling;
      if (signaling == null) throw ConnectionFailed('시그널링 서버 설정이 없습니다.');
      transport = await WebRtcTransport.connect(signaling, host.hostId!);
    } else {
      transport = await TlsTransport.connect(host.address, host.port);
    }
    return RemoteHostConnection.authenticate(
      transport: transport,
      login: login,
      clientName: Platform.localHostname,
      platform: Platform.operatingSystem,
      knownHosts: _knownHosts,
      devices: _devices,
      prompts: prompts,
    );
  }

  /// 성공하면 RemoteHostConnection, 실패하면 ConnectionFailed를 반환합니다.
  Future<Object> _runConnect(SavedHost host, LoginRequest login) async {
    _lastLogin = login;
    showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (_) => AlertDialog(
        content: Row(children: [
          const CircularProgressIndicator(),
          const SizedBox(width: 20),
          Expanded(child: Text(login.method == AuthMethods.password ? '연결 중... (비밀번호 확인)' : '연결 중...')),
        ]),
      ),
    );

    final prompts = _DialogPrompts(context);
    Object result;
    try {
      final ClientTransport transport;
      if (host.isInternet) {
        final signaling = _signaling;
        if (signaling == null) {
          throw ConnectionFailed('인터넷 연결에는 시그널링 서버 설정이 필요합니다. 오른쪽 위 ⚙에서 입력하세요.');
        }
        transport = await WebRtcTransport.connect(signaling, host.hostId!);
      } else {
        transport = await TlsTransport.connect(host.address, host.port);
      }
      result = await RemoteHostConnection.authenticate(
        transport: transport,
        login: login,
        clientName: Platform.localHostname,
        platform: Platform.operatingSystem, // ios, android, macos
        knownHosts: _knownHosts,
        devices: _devices,
        prompts: prompts,
      );
    } on ConnectionFailed catch (e) {
      result = e;
    } catch (e) {
      result = ConnectionFailed('연결 실패: $e');
    }

    if (mounted) {
      Navigator.of(context).pop(); // 진행 표시 닫기
    } else if (result is RemoteHostConnection) {
      await result.close();
    }
    return result;
  }

  Future<void> _openOrShow(SavedHost host, Object result) async {
    if (result is! RemoteHostConnection) {
      _showMessage((result as ConnectionFailed).message);
      return;
    }
    final connection = result;

    // 처음 연결에 성공하면 Host ID를 기억해 둡니다.
    if (host.hostId != connection.hostId) {
      final index = _hosts.indexWhere((h) => h.id == host.id);
      if (index >= 0) {
        _hosts[index] = host.copyWith(hostId: connection.hostId);
        await _store.saveAll(_hosts);
      }
    }

    if (!mounted) return;
    final login = _lastLogin ?? const LoginRequest(method: AuthMethods.accessCode, secret: '');
    final reason = await Navigator.of(context).push<String>(
      MaterialPageRoute(
        builder: (_) => RemoteScreen(
          connection: connection,
          title: host.name,
          // 자동 재연결: 같은 방식으로 다시 연결 (신뢰된 장치면 비밀값 없이, 아니면 메모리에 있는 로그인 정보로)
          reconnect: () => _connectQuietly(host, login),
        ),
      ),
    );
    if (mounted && reason != null) {
      _showMessage(reason);
    }
    await _refreshStatus();
  }

  void _showMessage(String message) {
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message), duration: const Duration(seconds: 5)));
  }

  // ---------------- UI ----------------

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Remote Desktop'),
        actions: [
          IconButton(onPressed: _refreshStatus, icon: const Icon(Icons.refresh), tooltip: '상태 새로고침'),
          IconButton(onPressed: _editSettings, icon: const Icon(Icons.settings), tooltip: '설정'),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => _editHost(),
        icon: const Icon(Icons.add),
        label: const Text('PC 추가'),
      ),
      body: _loading
          ? const Center(child: CircularProgressIndicator())
          : _hosts.isEmpty
              ? const Center(
                  child: Padding(
                    padding: EdgeInsets.all(32),
                    child: Text(
                      '등록된 PC가 없습니다.\n\nWindows PC에서 Host를 실행한 뒤\n[PC 추가]로 Host 콘솔에 표시된 주소를 입력하세요.',
                      textAlign: TextAlign.center,
                    ),
                  ),
                )
              : RefreshIndicator(
                  onRefresh: _refreshStatus,
                  child: ListView.separated(
                    padding: const EdgeInsets.only(bottom: 96),
                    itemCount: _hosts.length,
                    separatorBuilder: (_, _) => const Divider(height: 1),
                    itemBuilder: (context, index) => _buildHostTile(_hosts[index]),
                  ),
                ),
    );
  }

  Widget _buildHostTile(SavedHost host) {
    final status = _status[host.id] ?? _Status.checking;
    final (Color color, String label, IconData icon) = switch (status) {
      _Status.online => (Colors.green, 'Online', Icons.circle),
      _Status.offline => (Colors.grey, 'Offline', Icons.circle_outlined),
      _Status.checking => (Colors.grey, '확인 중', Icons.more_horiz),
    };

    return ListTile(
      leading: Icon(host.isInternet ? Icons.public : Icons.desktop_windows, size: 36),
      title: Text(host.name),
      subtitle: Text(host.isInternet
          ? '인터넷  ·  ${host.hostId}'
          : [
              '${host.address}:${host.port}',
              if (host.hostId != null) host.hostId!,
            ].join('  ·  ')),
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 12, color: color),
          const SizedBox(width: 4),
          Text(label, style: TextStyle(color: color)),
          PopupMenuButton<String>(
            onSelected: (value) => value == 'edit' ? _editHost(host) : _deleteHost(host),
            itemBuilder: (_) => const [
              PopupMenuItem(value: 'edit', child: Text('편집')),
              PopupMenuItem(value: 'delete', child: Text('삭제')),
            ],
          ),
        ],
      ),
      onTap: () => _connect(host),
    );
  }
}

class _HostEditDialog extends StatefulWidget {
  const _HostEditDialog({this.existing});
  final SavedHost? existing;

  @override
  State<_HostEditDialog> createState() => _HostEditDialogState();
}

class _HostEditDialogState extends State<_HostEditDialog> {
  final _formKey = GlobalKey<FormState>();
  late final _name = TextEditingController(text: widget.existing?.name ?? '');
  late bool _internet = widget.existing?.isInternet ?? false;
  late final _address = TextEditingController(
      text: widget.existing == null ? '192.168.' : (widget.existing!.isInternet ? '' : widget.existing!.address));
  late final _hostId = TextEditingController(text: widget.existing?.hostId ?? 'HOST-');
  late final _port = TextEditingController(text: '${widget.existing?.port ?? defaultPort}');

  @override
  void dispose() {
    _name.dispose();
    _address.dispose();
    _hostId.dispose();
    _port.dispose();
    super.dispose();
  }

  void _save() {
    if (!_formKey.currentState!.validate()) return;
    final existing = widget.existing;
    final address = _internet ? '' : _address.text.trim();
    final hostId = _internet ? _hostId.text.trim().toUpperCase() : existing?.hostId;
    final host = SavedHost(
      id: existing?.id ?? DateTime.now().microsecondsSinceEpoch.toString(),
      name: _name.text.trim(),
      address: address,
      port: int.tryParse(_port.text) ?? defaultPort,
      hostId: hostId,
    );
    Navigator.pop(context, host);
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text(widget.existing == null ? 'PC 추가' : 'PC 편집'),
      content: Form(
        key: _formKey,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            SegmentedButton<bool>(
              segments: const [
                ButtonSegment(value: false, label: Text('같은 Wi-Fi'), icon: Icon(Icons.wifi)),
                ButtonSegment(value: true, label: Text('인터넷'), icon: Icon(Icons.public)),
              ],
              selected: {_internet},
              onSelectionChanged: (v) => setState(() => _internet = v.first),
            ),
            TextFormField(
              controller: _name,
              decoration: const InputDecoration(labelText: '이름', hintText: 'PC-Office'),
              validator: (v) => (v == null || v.trim().isEmpty) ? '이름을 입력하세요' : null,
            ),
            if (_internet)
              TextFormField(
                controller: _hostId,
                textCapitalization: TextCapitalization.characters,
                decoration: const InputDecoration(labelText: 'Host ID', hintText: 'HOST-8F29A1'),
                validator: (v) => RegExp(r'^[A-Za-z0-9-]{6,40}$').hasMatch(v?.trim() ?? '') ? null : 'Host 화면의 Host ID를 입력하세요',
              )
            else ...[
              TextFormField(
                controller: _address,
                decoration: const InputDecoration(labelText: '주소 (IP)', hintText: '192.168.0.10'),
                keyboardType: TextInputType.url,
                validator: (v) => (v == null || v.trim().isEmpty) ? '주소를 입력하세요' : null,
              ),
              TextFormField(
                controller: _port,
                decoration: const InputDecoration(labelText: '포트'),
                keyboardType: TextInputType.number,
                inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                validator: (v) {
                  final port = int.tryParse(v ?? '');
                  return (port == null || port < 1 || port > 65535) ? '1~65535' : null;
                },
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('취소')),
        FilledButton(onPressed: _save, child: const Text('저장')),
      ],
    );
  }
}

/// 연결 중 확인 창들 (인증서 지문, 2단계 인증)
class _DialogPrompts implements ConnectPrompts {
  _DialogPrompts(this.context);
  final BuildContext context;

  @override
  Future<bool> confirmNewHost(NewHostInfo info) async {
    if (!context.mounted) return false;
    final ok = await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (context) => AlertDialog(
        title: const Text('처음 연결하는 PC'),
        content: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Text('Host ID: ${info.hostId}'),
              Text('PC 이름: ${info.hostName}'),
              const SizedBox(height: 12),
              const Text('인증서 지문'),
              SelectableText(info.fingerprint, style: const TextStyle(fontFamily: 'monospace', fontSize: 13)),
              const SizedBox(height: 12),
              const Text('Host 화면에 표시된 지문과 같은지 확인하세요.'),
            ],
          ),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('취소')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('같음, 연결')),
        ],
      ),
    );
    return ok == true;
  }

  @override
  Future<String?> askTotp(String hostName) async {
    if (!context.mounted) return null;
    final controller = TextEditingController();
    final code = await showDialog<String>(
      context: context,
      barrierDismissible: false,
      builder: (context) => AlertDialog(
        title: const Text('2단계 인증'),
        content: TextField(
          controller: controller,
          autofocus: true,
          keyboardType: TextInputType.number,
          maxLength: 6,
          inputFormatters: [FilteringTextInputFormatter.digitsOnly],
          decoration: InputDecoration(labelText: '$hostName 인증 앱의 6자리 코드'),
          onSubmitted: (v) => Navigator.pop(context, v),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('취소')),
          FilledButton(onPressed: () => Navigator.pop(context, controller.text), child: const Text('확인')),
        ],
      ),
    );
    controller.dispose();
    return code;
  }
}

class _LoginDialog extends StatefulWidget {
  const _LoginDialog({required this.hostName});
  final String hostName;

  @override
  State<_LoginDialog> createState() => _LoginDialogState();
}

class _LoginDialogState extends State<_LoginDialog> {
  final _secret = TextEditingController();
  String _method = AuthMethods.accessCode;
  bool _remember = false;

  @override
  void dispose() {
    _secret.dispose();
    super.dispose();
  }

  void _submit() {
    if (_secret.text.isEmpty) return;
    Navigator.pop(context, LoginRequest(method: _method, secret: _secret.text, rememberDevice: _remember));
  }

  @override
  Widget build(BuildContext context) {
    final isPassword = _method == AuthMethods.password;
    return AlertDialog(
      title: Text('${widget.hostName} 연결'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SegmentedButton<String>(
            segments: const [
              ButtonSegment(value: AuthMethods.accessCode, label: Text('접속 코드')),
              ButtonSegment(value: AuthMethods.password, label: Text('비밀번호')),
            ],
            selected: {_method},
            onSelectionChanged: (v) => setState(() => _method = v.first),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _secret,
            autofocus: true,
            obscureText: true,
            autocorrect: false,
            enableSuggestions: false,
            textCapitalization: isPassword ? TextCapitalization.none : TextCapitalization.characters,
            decoration: InputDecoration(
              labelText: isPassword ? '비밀번호' : '접속 코드',
              hintText: isPassword ? 'Host에서 설정한 비밀번호' : 'Host 화면에 표시된 10자리',
            ),
            onSubmitted: (_) => _submit(),
          ),
          CheckboxListTile(
            value: _remember,
            onChanged: (v) => setState(() => _remember = v ?? false),
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
            title: const Text('이 기기 기억하기'),
            subtitle: const Text('다음부터 비밀번호 없이 연결'),
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context), child: const Text('취소')),
        FilledButton(onPressed: _submit, child: const Text('연결')),
      ],
    );
  }
}
