import 'package:flutter/material.dart';

import 'screens/host_list_screen.dart';

void main() {
  runApp(const RemoteDesktopApp());
}

class RemoteDesktopApp extends StatelessWidget {
  const RemoteDesktopApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Remote Desktop',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(colorSchemeSeed: Colors.indigo, useMaterial3: true),
      darkTheme: ThemeData(colorSchemeSeed: Colors.indigo, brightness: Brightness.dark, useMaterial3: true),
      home: const HostListScreen(),
    );
  }
}
