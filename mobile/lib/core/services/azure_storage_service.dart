import 'dart:typed_data';

import 'package:equatable/equatable.dart';

import 'package:yanxia_mobile/core/models/captured_page_ref.dart';

abstract class AzureStorageService {
  Future<CapturedPageRef> uploadPage(CapturePageUploadRequest request);

  Future<void> deletePage(String pageId);
}

class CapturePageUploadRequest extends Equatable {
  const CapturePageUploadRequest({
    required this.batchId,
    required this.pageNumber,
    required this.bytes,
    required this.contentType,
  });

  final String batchId;
  final int pageNumber;
  final Uint8List bytes;
  final String contentType;

  @override
  List<Object?> get props => [batchId, pageNumber, bytes, contentType];
}

class StubAzureStorageService implements AzureStorageService {
  @override
  Future<CapturedPageRef> uploadPage(CapturePageUploadRequest request) async {
    // TODO: 替换为真实的 Azure 上传逻辑。
    return CapturedPageRef(
      pageId: 'stub-${request.pageNumber}',
      batchId: request.batchId,
      pageNumber: request.pageNumber,
      blobUrl: Uri.parse('https://example.com/${request.batchId}/${request.pageNumber}'),
    );
  }

  @override
  Future<void> deletePage(String pageId) async {
    // TODO: 调用后端删除对应 Blob。
  }
}

