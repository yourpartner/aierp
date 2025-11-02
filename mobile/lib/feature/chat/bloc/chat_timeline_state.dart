part of 'chat_timeline_bloc.dart';

class ChatTimelineState extends Equatable {
  const ChatTimelineState({required this.entries});

  const ChatTimelineState.initial() : entries = const [];

  final List<ChatEntry> entries;

  ChatTimelineState copyWith({List<ChatEntry>? entries}) {
    return ChatTimelineState(entries: entries ?? this.entries);
  }

  @override
  List<Object?> get props => [entries];
}

