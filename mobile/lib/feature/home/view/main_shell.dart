import 'package:flutter/material.dart';

import 'package:yanxia_mobile/feature/capture/view/capture_workspace_page.dart';
import 'package:yanxia_mobile/feature/chat/view/chat_timeline_page.dart';
import 'package:yanxia_mobile/feature/notifications/view/notification_center_page.dart';

class MainShell extends StatefulWidget {
  const MainShell({super.key});

  @override
  State<MainShell> createState() => _MainShellState();
}

class _MainShellState extends State<MainShell> {
  int _index = 0;

  final _pages = const [
    CaptureWorkspacePage(),
    ChatTimelinePage(),
    NotificationCenterPage(),
  ];

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: IndexedStack(
        index: _index,
        children: _pages,
      ),
      bottomNavigationBar: NavigationBar(
        selectedIndex: _index,
        onDestinationSelected: (value) => setState(() => _index = value),
        destinations: const [
          NavigationDestination(
            icon: Icon(Icons.document_scanner_outlined),
            label: '扫描',
          ),
          NavigationDestination(
            icon: Icon(Icons.chat_bubble_outline),
            label: '会话',
          ),
          NavigationDestination(
            icon: Icon(Icons.notifications_outlined),
            label: '通知',
          ),
        ],
      ),
    );
  }
}

