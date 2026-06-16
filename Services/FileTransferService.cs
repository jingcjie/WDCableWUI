using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WDCableWUI.Protocol;
using Windows.Storage;

namespace WDCableWUI.Services
{
    public class FileTransferService : IDisposable
    {
        private static FileTransferService? _instance;
        private static readonly object Lock = new();

        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly ConcurrentDictionary<long, IncomingFileStream> _incomingFiles = new();
        private readonly ConcurrentDictionary<string, bool> _activeSendIds = new();
        private readonly Channel<ProtocolFrame> _bulkFrames;
        private readonly CancellationTokenSource _bulkFrameCancellationTokenSource = new();
        private SessionManager? _sessionManager;
        private string _downloadPath;
        private bool _isDisposed;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<FileTransferEventArgs>? FileReceived;
        public event EventHandler<FileTransferEventArgs>? FileSent;
        public event EventHandler<FileTransferProgressEventArgs>? TransferProgress;
        public event EventHandler<FileReceiveStartedEventArgs>? FileReceiveStarted;
        public event EventHandler<FileTransferFailedEventArgs>? TransferFailed;

        public static FileTransferService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Lock)
                    {
                        _instance ??= new FileTransferService();
                    }
                }

                return _instance;
            }
        }

        public bool IsConnected => _sessionManager?.IsReady ?? false;

        public string DownloadPath => _downloadPath;

        private FileTransferService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _bulkFrames = Channel.CreateUnbounded<ProtocolFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _ = Task.Run(() => ProcessBulkFramesAsync(_bulkFrameCancellationTokenSource.Token));
            _downloadPath = DefaultDownloadPath();
            try
            {
                _downloadPath = ServiceManager.DataManager?.GetDownloadPath() ?? _downloadPath;
            }
            catch
            {
            }

            _sessionManager = ServiceManager.SessionManager;
            if (_sessionManager != null)
            {
                SubscribeToSession(_sessionManager);
            }

            OnStatusChanged("FileTransferService initialized");
        }

        public void SetDownloadPath(string path)
        {
            try
            {
                _downloadPath = string.IsNullOrWhiteSpace(path) ? DefaultDownloadPath() : path;
                ServiceManager.DataManager?.SetDownloadPath(_downloadPath);
                OnStatusChanged($"Download path updated: {_downloadPath}");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to update download path: {ex.Message}");
            }
        }

        public async Task SendFileAsync(StorageFile file)
        {
            if (file == null)
            {
                OnErrorOccurred("Cannot send null file");
                return;
            }

            if (_sessionManager?.IsReady != true || string.IsNullOrWhiteSpace(_sessionManager.CurrentSessionId))
            {
                OnErrorOccurred("WDCable session is not ready for file transfer");
                return;
            }

            var transferId = Guid.NewGuid().ToString();
            var streamId = BulkProtocol.NextStreamId();
            var safeName = BulkProtocol.SafeFileName(file.Name);
            var properties = await file.GetBasicPropertiesAsync();
            var sizeBytes = (long)properties.Size;
            var startedAt = DateTimeOffset.UtcNow;
            long bytesSent = 0;
            _activeSendIds[transferId] = true;

            try
            {
                OnStatusChanged($"Sending file: {safeName} ({sizeBytes} bytes)");
                OnTransferProgress(safeName, 0, sizeBytes, transferId, isSender: true);

                await _sessionManager.SendBulkFrameAsync(
                    ProtocolFrameType.BulkStart,
                    BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                    {
                        ["kind"] = BulkProtocol.KindFile,
                        ["transferId"] = transferId,
                        ["sessionId"] = _sessionManager.CurrentSessionId,
                        ["fileName"] = safeName,
                        ["sizeBytes"] = sizeBytes,
                        ["timestamp"] = startedAt.ToUnixTimeMilliseconds()
                    }),
                    streamId: streamId,
                    correlationId: Guid.Parse(transferId)).ConfigureAwait(false);

                using var sha256 = SHA256.Create();
                await using var input = await file.OpenStreamForReadAsync();
                var buffer = new byte[BulkProtocol.ChunkSize];

                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var payload = new byte[read];
                    Buffer.BlockCopy(buffer, 0, payload, 0, read);
                    sha256.TransformBlock(payload, 0, payload.Length, null, 0);

                    await _sessionManager.SendBulkFrameAsync(
                        ProtocolFrameType.BulkChunk,
                        BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                        {
                            ["kind"] = BulkProtocol.KindFile,
                            ["transferId"] = transferId,
                            ["offset"] = bytesSent
                        }),
                        payload,
                        streamId,
                        Guid.Parse(transferId)).ConfigureAwait(false);

                    bytesSent += read;
                    OnTransferProgress(safeName, bytesSent, sizeBytes, transferId, isSender: true);
                }

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = BulkProtocol.Sha256Hex(sha256.Hash ?? Array.Empty<byte>());

                await _sessionManager.SendBulkFrameAsync(
                    ProtocolFrameType.BulkComplete,
                    BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                    {
                        ["kind"] = BulkProtocol.KindFile,
                        ["transferId"] = transferId,
                        ["sizeBytes"] = bytesSent,
                        ["sha256"] = hash,
                        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }),
                    streamId: streamId,
                    correlationId: Guid.Parse(transferId)).ConfigureAwait(false);

                OnTransferProgress(safeName, bytesSent, sizeBytes, transferId, isSender: true);
                OnStatusChanged($"File sent successfully: {safeName}");
                OnFileSent(safeName, file.Path, bytesSent, transferId);
            }
            catch (Exception ex)
            {
                await TryCancelBulkStreamAsync(streamId, transferId, "file_send_failed", ex.Message).ConfigureAwait(false);
                OnTransferFailed(safeName, file.Path, bytesSent, transferId, isSender: true, errorMessage: ex.Message);
                OnErrorOccurred($"Failed to send file: {ex.Message}");
            }
            finally
            {
                _activeSendIds.TryRemove(transferId, out _);
            }
        }

        public void StartListening()
        {
            OnStatusChanged(IsConnected ? "File transfer bulk channel is ready" : "Waiting for WDCable session readiness");
        }

        public void StopListening()
        {
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _bulkFrameCancellationTokenSource.Cancel();
            _bulkFrames.Writer.TryComplete();
            if (_sessionManager != null)
            {
                UnsubscribeFromSession(_sessionManager);
                _sessionManager = null;
            }

            CleanupIncoming(deletePartialFiles: true);
            _bulkFrameCancellationTokenSource.Dispose();
            OnStatusChanged("FileTransferService disposing");
        }

        public static void ResetInstance()
        {
            lock (Lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }

        private void SubscribeToSession(SessionManager sessionManager)
        {
            sessionManager.SessionReady += OnSessionReady;
            sessionManager.StateChanged += OnSessionStateChanged;
            sessionManager.SessionFailed += OnSessionFailed;
            sessionManager.BulkFrameReceived += OnBulkFrameReceived;
            sessionManager.ControlFrameReceived += OnControlFrameReceived;
        }

        private void UnsubscribeFromSession(SessionManager sessionManager)
        {
            sessionManager.SessionReady -= OnSessionReady;
            sessionManager.StateChanged -= OnSessionStateChanged;
            sessionManager.SessionFailed -= OnSessionFailed;
            sessionManager.BulkFrameReceived -= OnBulkFrameReceived;
            sessionManager.ControlFrameReceived -= OnControlFrameReceived;
        }

        private void OnSessionReady(object? sender, SessionReadyEventArgs e)
        {
            OnStatusChanged("WDCable session ready - File transfer is available");
        }

        private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        {
            if (e.Phase is SessionPhase.Disconnected or SessionPhase.Failed or SessionPhase.Disconnecting)
            {
                CleanupIncoming(deletePartialFiles: true);
            }
        }

        private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
        {
            CleanupIncoming(deletePartialFiles: true);
            OnErrorOccurred($"WDCable session failed: {e.Message}");
        }

        private void OnControlFrameReceived(object? sender, ProtocolFrameReceivedEventArgs e)
        {
            if (e.Frame.Type != ProtocolFrameType.Error)
            {
                return;
            }

            try
            {
                var metadata = BulkProtocol.ParseMetadata(e.Frame.MetadataJson);
                var code = BulkProtocol.GetString(metadata, "code");
                if (code.StartsWith("file_", StringComparison.Ordinal) || code.StartsWith("bulk_", StringComparison.Ordinal))
                {
                    OnErrorOccurred(BulkProtocol.GetString(metadata, "message", "Peer reported file transfer error"));
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Invalid peer file error metadata: {ex.Message}");
            }
        }

        private void OnBulkFrameReceived(object? sender, ProtocolFrameReceivedEventArgs e)
        {
            if (!_isDisposed)
            {
                _bulkFrames.Writer.TryWrite(e.Frame);
            }
        }

        private async Task ProcessBulkFramesAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var frame in _bulkFrames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleBulkFrameAsync(frame).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task HandleBulkFrameAsync(ProtocolFrame frame)
        {
            try
            {
                var metadata = BulkProtocol.ParseMetadata(frame.MetadataJson);
                var kind = BulkProtocol.GetString(metadata, "kind");
                if (kind != BulkProtocol.KindFile && !_incomingFiles.ContainsKey(frame.StreamId))
                {
                    return;
                }

                switch (frame.Type)
                {
                    case ProtocolFrameType.BulkStart:
                        if (kind == BulkProtocol.KindFile)
                        {
                            StartIncomingFile(frame, metadata);
                        }
                        break;
                    case ProtocolFrameType.BulkChunk:
                        HandleIncomingFileChunk(frame);
                        break;
                    case ProtocolFrameType.BulkComplete:
                        await CompleteIncomingFileAsync(frame, metadata).ConfigureAwait(false);
                        break;
                    case ProtocolFrameType.BulkCancel:
                        CancelIncomingFile(frame, metadata);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"File transfer frame handling failed: {ex.Message}");
            }
        }

        private void StartIncomingFile(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            var transferId = BulkProtocol.GetString(metadata, "transferId", Guid.NewGuid().ToString());
            var originalName = BulkProtocol.GetString(metadata, "fileName", "unknown_file");
            var expectedSize = BulkProtocol.GetInt64(metadata, "sizeBytes", -1);
            if (expectedSize < 0)
            {
                expectedSize = -1;
            }

            if (_incomingFiles.TryRemove(frame.StreamId, out var existing))
            {
                existing.Dispose();
                TryDelete(existing.WorkingPath);
                OnTransferFailed(
                    existing.SafeFileName,
                    existing.TargetPath,
                    existing.BytesReceived,
                    existing.TransferId,
                    isSender: false,
                    errorMessage: "Duplicate file transfer start received for the same stream.");
            }

            var targetDirectory = ResolveDownloadDirectory();
            var targetPath = BulkProtocol.DuplicateSafePath(targetDirectory, originalName);
            var workingPath = CreatePartialPath(targetPath);
            var safeName = Path.GetFileName(targetPath);

            var incoming = new IncomingFileStream(
                transferId,
                originalName,
                safeName,
                expectedSize,
                targetPath,
                workingPath,
                File.Create(workingPath),
                SHA256.Create());

            _incomingFiles[frame.StreamId] = incoming;
            OnStatusChanged($"Receiving file: {safeName} ({expectedSize} bytes)");
            OnFileReceiveStarted(safeName, expectedSize, transferId);
            OnTransferProgress(safeName, 0, expectedSize, transferId, isSender: false);
        }

        private void HandleIncomingFileChunk(ProtocolFrame frame)
        {
            if (!_incomingFiles.TryGetValue(frame.StreamId, out var incoming))
            {
                return;
            }

            incoming.OutputStream.Write(frame.Payload, 0, frame.Payload.Length);
            incoming.Digest.TransformBlock(frame.Payload, 0, frame.Payload.Length, null, 0);
            incoming.BytesReceived += frame.Payload.Length;
            OnTransferProgress(incoming.SafeFileName, incoming.BytesReceived, incoming.ExpectedSize, incoming.TransferId, isSender: false);
        }

        private async Task CompleteIncomingFileAsync(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            if (!_incomingFiles.TryRemove(frame.StreamId, out var incoming))
            {
                return;
            }

            try
            {
                incoming.Digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                incoming.OutputStream.Flush();
                incoming.OutputStream.Dispose();
                var actualHash = BulkProtocol.Sha256Hex(incoming.Digest.Hash ?? Array.Empty<byte>());
                var expectedSize = BulkProtocol.GetInt64(metadata, "sizeBytes", incoming.ExpectedSize);
                if (expectedSize >= 0 && incoming.BytesReceived != expectedSize)
                {
                    throw new IOException($"Size mismatch for {incoming.SafeFileName}: expected {expectedSize} bytes, received {incoming.BytesReceived} bytes");
                }

                var expectedHash = BulkProtocol.GetString(metadata, "sha256");
                if (!string.IsNullOrWhiteSpace(expectedHash) &&
                    !expectedHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"Checksum mismatch for {incoming.SafeFileName}");
                }

                var finalPath = MoveCompletedFile(incoming);
                var finalName = Path.GetFileName(finalPath);
                OnTransferProgress(finalName, incoming.BytesReceived, incoming.ExpectedSize, incoming.TransferId, isSender: false);
                OnStatusChanged($"File received successfully: {finalName}");
                OnFileReceived(finalName, finalPath, incoming.BytesReceived, incoming.TransferId);
                await SendAckAsync(frame, "bulk.complete").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TryDelete(incoming.WorkingPath);
                await SendBulkErrorAsync(frame.StreamId, "file_receive_failed", ex.Message).ConfigureAwait(false);
                OnTransferFailed(incoming.SafeFileName, incoming.TargetPath, incoming.BytesReceived, incoming.TransferId, isSender: false, errorMessage: ex.Message);
                OnErrorOccurred($"File receive failed: {ex.Message}");
            }
            finally
            {
                incoming.Dispose();
            }
        }

        private void CancelIncomingFile(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            if (!_incomingFiles.TryRemove(frame.StreamId, out var incoming))
            {
                return;
            }

            incoming.Dispose();
            TryDelete(incoming.WorkingPath);
            var reason = BulkProtocol.GetString(metadata, "reason", "cancelled");
            OnTransferFailed(incoming.SafeFileName, incoming.TargetPath, incoming.BytesReceived, incoming.TransferId, isSender: false, errorMessage: reason);
            OnErrorOccurred($"File transfer cancelled: {incoming.SafeFileName} ({reason})");
        }

        private async Task SendAckAsync(ProtocolFrame frame, string ackFor)
        {
            if (_sessionManager?.IsReady != true)
            {
                return;
            }

            await _sessionManager.SendControlFrameAsync(
                ProtocolFrameType.Ack,
                BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                {
                    ["ackFor"] = ackFor,
                    ["streamId"] = frame.StreamId,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }),
                streamId: frame.StreamId,
                correlationId: frame.CorrelationId).ConfigureAwait(false);
        }

        private async Task TryCancelBulkStreamAsync(long streamId, string transferId, string reason, string message)
        {
            if (_sessionManager?.IsReady != true)
            {
                return;
            }

            try
            {
                await _sessionManager.SendBulkFrameAsync(
                    ProtocolFrameType.BulkCancel,
                    BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                    {
                        ["kind"] = BulkProtocol.KindFile,
                        ["transferId"] = transferId,
                        ["reason"] = reason,
                        ["message"] = message
                    }),
                    streamId: streamId,
                    correlationId: BulkProtocol.GuidFromStringOrNew(transferId)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task SendBulkErrorAsync(long streamId, string code, string message)
        {
            if (_sessionManager?.IsReady != true)
            {
                return;
            }

            await _sessionManager.SendControlFrameAsync(
                ProtocolFrameType.Error,
                BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["message"] = message,
                    ["streamId"] = streamId
                }),
                streamId: streamId).ConfigureAwait(false);
        }

        private string ResolveDownloadDirectory()
        {
            var fallback = DefaultDownloadPath();
            var requested = string.IsNullOrWhiteSpace(_downloadPath) ? fallback : _downloadPath;
            try
            {
                Directory.CreateDirectory(requested);
                return requested;
            }
            catch
            {
                Directory.CreateDirectory(fallback);
                OnStatusChanged($"Download path unavailable, using fallback: {fallback}");
                return fallback;
            }
        }

        private static string CreatePartialPath(string targetPath)
        {
            var directory = Path.GetDirectoryName(targetPath) ?? DefaultDownloadPath();
            var fileName = Path.GetFileName(targetPath);
            return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.wdcable-part");
        }

        private static string MoveCompletedFile(IncomingFileStream incoming)
        {
            var targetPath = incoming.TargetPath;
            if (File.Exists(targetPath))
            {
                var directory = Path.GetDirectoryName(targetPath) ?? DefaultDownloadPath();
                targetPath = BulkProtocol.DuplicateSafePath(directory, Path.GetFileName(targetPath));
            }

            File.Move(incoming.WorkingPath, targetPath);
            return targetPath;
        }

        private void CleanupIncoming(bool deletePartialFiles)
        {
            foreach (var pair in _incomingFiles)
            {
                if (_incomingFiles.TryRemove(pair.Key, out var incoming))
                {
                    incoming.Dispose();
                    if (deletePartialFiles)
                    {
                        TryDelete(incoming.WorkingPath);
                    }
                }
            }
        }

        private static string DefaultDownloadPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private void OnStatusChanged(string status)
        {
            RaiseOnDispatcher(() => StatusChanged?.Invoke(this, status));
        }

        private void OnErrorOccurred(string error)
        {
            RaiseOnDispatcher(() => ErrorOccurred?.Invoke(this, error));
        }

        private void OnFileReceived(string fileName, string filePath, long fileSize, string transferId)
        {
            RaiseOnDispatcher(() => FileReceived?.Invoke(this, new FileTransferEventArgs(fileName, filePath, fileSize, transferId)));
        }

        private void OnFileSent(string fileName, string filePath, long fileSize, string transferId)
        {
            RaiseOnDispatcher(() => FileSent?.Invoke(this, new FileTransferEventArgs(fileName, filePath, fileSize, transferId)));
        }

        private void OnTransferProgress(string fileName, long bytesTransferred, long totalBytes, string transferId, bool isSender)
        {
            RaiseOnDispatcher(() => TransferProgress?.Invoke(this, new FileTransferProgressEventArgs(fileName, bytesTransferred, totalBytes, transferId, isSender)));
        }

        private void OnFileReceiveStarted(string fileName, long fileSize, string transferId)
        {
            RaiseOnDispatcher(() => FileReceiveStarted?.Invoke(this, new FileReceiveStartedEventArgs(fileName, fileSize, transferId)));
        }

        private void OnTransferFailed(string fileName, string filePath, long fileSize, string transferId, bool isSender, string errorMessage)
        {
            RaiseOnDispatcher(() => TransferFailed?.Invoke(this, new FileTransferFailedEventArgs(fileName, filePath, fileSize, transferId, isSender, errorMessage)));
        }

        private void RaiseOnDispatcher(Action action)
        {
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(() => action());
            }
            else
            {
                action();
            }
        }

        private sealed class IncomingFileStream : IDisposable
        {
            public IncomingFileStream(
                string transferId,
                string fileName,
                string safeFileName,
                long expectedSize,
                string targetPath,
                string workingPath,
                FileStream outputStream,
                SHA256 digest)
            {
                TransferId = transferId;
                FileName = fileName;
                SafeFileName = safeFileName;
                ExpectedSize = expectedSize;
                TargetPath = targetPath;
                WorkingPath = workingPath;
                OutputStream = outputStream;
                Digest = digest;
            }

            public string TransferId { get; }
            public string FileName { get; }
            public string SafeFileName { get; }
            public long ExpectedSize { get; }
            public string TargetPath { get; }
            public string WorkingPath { get; }
            public FileStream OutputStream { get; }
            public SHA256 Digest { get; }
            public long BytesReceived { get; set; }

            public void Dispose()
            {
                OutputStream.Dispose();
                Digest.Dispose();
            }
        }
    }

    public class FileTransferEventArgs : EventArgs
    {
        public string FileName { get; }
        public string FilePath { get; }
        public long FileSize { get; }
        public string TransferId { get; }

        public FileTransferEventArgs(string fileName, string filePath, long fileSize, string transferId = "")
        {
            FileName = fileName;
            FilePath = filePath;
            FileSize = fileSize;
            TransferId = transferId;
        }
    }

    public class FileTransferProgressEventArgs : EventArgs
    {
        public string FileName { get; }
        public long BytesTransferred { get; }
        public long TotalBytes { get; }
        public string TransferId { get; }
        public bool IsSender { get; }
        public double ProgressPercentage
        {
            get
            {
                if (TotalBytes < 0)
                {
                    return 0;
                }

                if (TotalBytes == 0)
                {
                    return 100;
                }

                return Math.Min(100, Math.Max(0, (double)BytesTransferred / TotalBytes * 100));
            }
        }

        public FileTransferProgressEventArgs(
            string fileName,
            long bytesTransferred,
            long totalBytes,
            string transferId = "",
            bool isSender = false)
        {
            FileName = fileName;
            BytesTransferred = bytesTransferred;
            TotalBytes = totalBytes;
            TransferId = transferId;
            IsSender = isSender;
        }
    }

    public class FileReceiveStartedEventArgs : EventArgs
    {
        public string FileName { get; }
        public long FileSize { get; }
        public string TransferId { get; }

        public FileReceiveStartedEventArgs(string fileName, long fileSize, string transferId = "")
        {
            FileName = fileName;
            FileSize = fileSize;
            TransferId = transferId;
        }
    }

    public class FileTransferFailedEventArgs : EventArgs
    {
        public string FileName { get; }
        public string FilePath { get; }
        public long FileSize { get; }
        public string TransferId { get; }
        public bool IsSender { get; }
        public string ErrorMessage { get; }

        public FileTransferFailedEventArgs(
            string fileName,
            string filePath,
            long fileSize,
            string transferId,
            bool isSender,
            string errorMessage)
        {
            FileName = fileName;
            FilePath = filePath;
            FileSize = fileSize;
            TransferId = transferId;
            IsSender = isSender;
            ErrorMessage = errorMessage;
        }
    }
}
