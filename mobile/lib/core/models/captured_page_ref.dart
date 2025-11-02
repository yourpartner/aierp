import 'package:equatable/equatable.dart';

class CapturedPageRef extends Equatable {
  const CapturedPageRef({
    required this.pageId,
    required this.batchId,
    required this.pageNumber,
    required this.blobUrl,
    this.thumbnailUrl,
  });

  final String pageId;
  final String batchId;
  final int pageNumber;
  final Uri blobUrl;
  final Uri? thumbnailUrl;

  CapturedPageRef copyWith({
    int? pageNumber,
    Uri? thumbnailUrl,
  }) {
    return CapturedPageRef(
      pageId: pageId,
      batchId: batchId,
      pageNumber: pageNumber ?? this.pageNumber,
      blobUrl: blobUrl,
      thumbnailUrl: thumbnailUrl ?? this.thumbnailUrl,
    );
  }

  @override
  List<Object?> get props => [pageId, batchId, pageNumber, blobUrl, thumbnailUrl];
}

