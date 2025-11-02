import 'package:equatable/equatable.dart';

class ChatEntry extends Equatable {
  const ChatEntry({
    required this.id,
    required this.title,
    required this.body,
    required this.timestamp,
    this.batchId,
    this.extra,
  });

  final String id;
  final String title;
  final String body;
  final DateTime timestamp;
  final String? batchId;
  final Map<String, dynamic>? extra;

  @override
  List<Object?> get props => [id, title, body, timestamp, batchId, extra];
}

