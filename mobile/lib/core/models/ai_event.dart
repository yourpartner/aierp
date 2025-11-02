import 'package:equatable/equatable.dart';

class AiEvent extends Equatable {
  const AiEvent({
    required this.batchId,
    required this.type,
    this.message,
    this.payload,
  });

  final String batchId;
  final AiEventType type;
  final String? message;
  final Map<String, dynamic>? payload;

  @override
  List<Object?> get props => [batchId, type, message, payload];
}

enum AiEventType {
  processing,
  completed,
  failed,
}

