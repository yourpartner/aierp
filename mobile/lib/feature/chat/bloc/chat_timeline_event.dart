part of 'chat_timeline_bloc.dart';

abstract class ChatTimelineEvent extends Equatable {
  const ChatTimelineEvent();

  @override
  List<Object?> get props => [];
}

class ChatTimelineSubscriptionRequested extends ChatTimelineEvent {
  const ChatTimelineSubscriptionRequested();
}

class _ChatTimelineAiEventReceived extends ChatTimelineEvent {
  const _ChatTimelineAiEventReceived(this.event);

  final AiEvent event;

  @override
  List<Object?> get props => [event];
}

