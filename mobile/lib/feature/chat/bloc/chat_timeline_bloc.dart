import 'dart:async';

import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';
import 'package:uuid/uuid.dart';

import 'package:yanxia_mobile/core/models/ai_event.dart';
import 'package:yanxia_mobile/core/services/ai_event_stream.dart';
import 'package:yanxia_mobile/feature/chat/models/chat_entry.dart';

part 'chat_timeline_event.dart';
part 'chat_timeline_state.dart';

class ChatTimelineBloc extends Bloc<ChatTimelineEvent, ChatTimelineState> {
  ChatTimelineBloc({required AiEventStream aiEventStream})
      : _aiEventStream = aiEventStream,
        super(const ChatTimelineState.initial()) {
    on<ChatTimelineSubscriptionRequested>(_onSubscriptionRequested);
    on<_ChatTimelineAiEventReceived>(_onAiEventReceived);
  }

  final AiEventStream _aiEventStream;
  final Uuid _uuid = const Uuid();
  StreamSubscription<AiEvent>? _subscription;

  Future<void> _onSubscriptionRequested(
    ChatTimelineSubscriptionRequested event,
    Emitter<ChatTimelineState> emit,
  ) async {
    await _subscription?.cancel();
    await _aiEventStream.start();
    _subscription = _aiEventStream.events.listen((event) {
      add(_ChatTimelineAiEventReceived(event));
    });
  }

  void _onAiEventReceived(
    _ChatTimelineAiEventReceived event,
    Emitter<ChatTimelineState> emit,
  ) {
    final aiEvent = event.event;
    final entry = ChatEntry(
      id: _uuid.v4(),
      title: _titleForEvent(aiEvent),
      body: aiEvent.message ?? '',
      timestamp: DateTime.now(),
      batchId: aiEvent.batchId,
      extra: aiEvent.payload,
    );

    final entries = List<ChatEntry>.from(state.entries)..insert(0, entry);

    emit(state.copyWith(entries: entries));
  }

  String _titleForEvent(AiEvent event) {
    switch (event.type) {
      case AiEventType.processing:
        return '批次 ${event.batchId} 正在处理';
      case AiEventType.completed:
        return '批次 ${event.batchId} 已完成';
      case AiEventType.failed:
        return '批次 ${event.batchId} 处理失败';
    }
  }

  @override
  Future<void> close() async {
    await _subscription?.cancel();
    await _aiEventStream.dispose();
    return super.close();
  }
}

