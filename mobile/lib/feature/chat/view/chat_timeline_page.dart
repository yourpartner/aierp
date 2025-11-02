import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:yanxia_mobile/feature/chat/bloc/chat_timeline_bloc.dart';

class ChatTimelinePage extends StatefulWidget {
  const ChatTimelinePage({super.key});

  @override
  State<ChatTimelinePage> createState() => _ChatTimelinePageState();
}

class _ChatTimelinePageState extends State<ChatTimelinePage> {
  @override
  void initState() {
    super.initState();
    context
        .read<ChatTimelineBloc>()
        .add(const ChatTimelineSubscriptionRequested());
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('处理会话')),
      body: BlocBuilder<ChatTimelineBloc, ChatTimelineState>(
        builder: (context, state) {
          if (state.entries.isEmpty) {
            return const Center(
              child: Text('等待 AI 处理结果...'),
            );
          }

          return ListView.builder(
            itemCount: state.entries.length,
            itemBuilder: (context, index) {
              final entry = state.entries[index];
              return ListTile(
                leading: const Icon(Icons.receipt_long),
                title: Text(entry.title),
                subtitle: Text(entry.body),
                trailing: const Icon(Icons.chevron_right),
              );
            },
          );
        },
      ),
    );
  }
}

