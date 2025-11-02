import 'dart:async';

import 'package:yanxia_mobile/core/models/ai_event.dart';

abstract class AiEventStream {
  Stream<AiEvent> get events;

  Future<void> start();

  Future<void> dispose();
}

class StubAiEventStream implements AiEventStream {
  StubAiEventStream();

  final StreamController<AiEvent> _controller = StreamController.broadcast();

  @override
  Stream<AiEvent> get events => _controller.stream;

  @override
  Future<void> start() async {
    // 预留：连接后端实时事件。
  }

  @override
  Future<void> dispose() async {
    await _controller.close();
  }
}

