using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WDCableWUI.Protocol;

namespace WDCableWUI.Services
{
    public class SpeedTestService : IDisposable
    {
        private static SpeedTestService? _instance;
        private static readonly object Lock = new();
        private static readonly TimeSpan SpeedTestTimeout = TimeSpan.FromSeconds(60);

        private readonly DispatcherQueue? _dispatcherQueue;
        private readonly ConcurrentDictionary<long, IncomingSpeedStream> _incomingSpeedStreams = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SpeedTestResult>> _pendingDownloads = new();
        private readonly Channel<ProtocolFrame> _bulkFrames;
        private readonly CancellationTokenSource _bulkFrameCancellationTokenSource = new();
        private readonly SemaphoreSlim _speedTestLock = new(1, 1);
        private SessionManager? _sessionManager;
        private bool _isDisposed;
        private string? _activeDownloadTestId;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<SpeedTestResult>? UploadCompleted;
        public event EventHandler<SpeedTestResult>? DownloadCompleted;
        public event EventHandler<SpeedTestProgressEventArgs>? ProgressChanged;

        public static SpeedTestService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (Lock)
                    {
                        _instance ??= new SpeedTestService();
                    }
                }

                return _instance;
            }
        }

        public bool IsConnected => _sessionManager?.IsReady ?? false;

        public bool IsDownloadTestInProgress => _activeDownloadTestId != null;

        public long ExpectedDownloadSize => _incomingSpeedStreams.Values.FirstOrDefault(s => s.TestId == _activeDownloadTestId)?.ExpectedSize ?? 0;

        private SpeedTestService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _bulkFrames = Channel.CreateUnbounded<ProtocolFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _ = Task.Run(() => ProcessBulkFramesAsync(_bulkFrameCancellationTokenSource.Token));
            _sessionManager = ServiceManager.SessionManager;
            if (_sessionManager != null)
            {
                SubscribeToSession(_sessionManager);
            }

            OnStatusChanged("SpeedTestService initialized");
        }

        public void StartListening()
        {
            OnStatusChanged(IsConnected ? "Speed test bulk channel is ready" : "Waiting for WDCable session readiness");
        }

        public void StopListening()
        {
        }

        public async Task PerformUploadTest(long sizeBytes)
        {
            if (!await TryEnterSpeedTestAsync(SpeedTestType.Upload, sizeBytes).ConfigureAwait(false))
            {
                return;
            }

            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                await SendSpeedPayloadAsync(Guid.NewGuid().ToString(), sizeBytes, emitLocalProgress: true).ConfigureAwait(false);
                var duration = DateTimeOffset.UtcNow - startedAt;
                var speedMbps = BulkProtocol.CalculateMbps(Math.Max(sizeBytes, 0), duration);
                OnStatusChanged($"Upload completed - Speed: {speedMbps:F2} Mbps");
                OnUploadCompleted(new SpeedTestResult
                {
                    TestType = SpeedTestType.Upload,
                    DataSize = Math.Max(sizeBytes, 0),
                    Duration = duration,
                    SpeedMbps = speedMbps,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                var duration = DateTimeOffset.UtcNow - startedAt;
                var error = $"Upload test failed: {ex.Message}";
                OnErrorOccurred(error);
                OnUploadCompleted(new SpeedTestResult
                {
                    TestType = SpeedTestType.Upload,
                    DataSize = Math.Max(sizeBytes, 0),
                    Duration = duration,
                    Success = false,
                    ErrorMessage = error
                });
            }
            finally
            {
                _speedTestLock.Release();
            }
        }

        public async Task PerformDownloadTest(long sizeBytes)
        {
            if (!await TryEnterSpeedTestAsync(SpeedTestType.Download, sizeBytes).ConfigureAwait(false))
            {
                return;
            }

            var testId = Guid.NewGuid().ToString();
            var streamId = BulkProtocol.NextStreamId();
            var startedAt = DateTimeOffset.UtcNow;
            var completionSource = new TaskCompletionSource<SpeedTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeDownloadTestId = testId;

            try
            {
                RequireReadySession();
                OnStatusChanged($"Starting download test - requesting {sizeBytes} bytes");

                _pendingDownloads[testId] = completionSource;
                await _sessionManager!.SendBulkFrameAsync(
                    ProtocolFrameType.BulkStart,
                    BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                    {
                        ["kind"] = BulkProtocol.KindSpeedRequest,
                        ["testId"] = testId,
                        ["sessionId"] = _sessionManager.CurrentSessionId,
                        ["sizeBytes"] = Math.Max(sizeBytes, 0),
                        ["timestamp"] = startedAt.ToUnixTimeMilliseconds()
                    }),
                    streamId: streamId,
                    correlationId: Guid.Parse(testId)).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(SpeedTestTimeout);
                using (timeoutCts.Token.Register(() => completionSource.TrySetException(new TimeoutException("Timed out waiting for speed data"))))
                {
                    var result = await completionSource.Task.ConfigureAwait(false);
                    OnDownloadCompleted(result);
                }
            }
            catch (Exception ex)
            {
                var duration = DateTimeOffset.UtcNow - startedAt;
                var error = $"Download test failed: {ex.Message}";
                OnErrorOccurred(error);
                OnDownloadCompleted(new SpeedTestResult
                {
                    TestType = SpeedTestType.Download,
                    DataSize = Math.Max(sizeBytes, 0),
                    Duration = duration,
                    Success = false,
                    ErrorMessage = error
                });
            }
            finally
            {
                _pendingDownloads.TryRemove(testId, out _);
                _activeDownloadTestId = null;
                _speedTestLock.Release();
            }
        }

        internal void ResetDownloadTestState()
        {
            _activeDownloadTestId = null;
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

            _speedTestLock.Dispose();
            _bulkFrameCancellationTokenSource.Dispose();
            _incomingSpeedStreams.Clear();
            _pendingDownloads.Clear();
            OnStatusChanged("SpeedTestService disposing");
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
            OnStatusChanged("WDCable session ready - Speed test is available");
        }

        private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
        {
            if (e.Phase is SessionPhase.Disconnected or SessionPhase.Failed or SessionPhase.Disconnecting)
            {
                FailActiveSpeedTests("Session disconnected");
            }
        }

        private void OnSessionFailed(object? sender, SessionFailedEventArgs e)
        {
            FailActiveSpeedTests(e.Message);
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
                if (code.StartsWith("speed_", StringComparison.Ordinal) || code.StartsWith("bulk_", StringComparison.Ordinal))
                {
                    var message = BulkProtocol.GetString(metadata, "message", "Peer reported speed test error");
                    FailActiveSpeedTests(message);
                    OnErrorOccurred(message);
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Invalid peer speed error metadata: {ex.Message}");
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
                if (kind != BulkProtocol.KindSpeedData &&
                    kind != BulkProtocol.KindSpeedRequest &&
                    !_incomingSpeedStreams.ContainsKey(frame.StreamId))
                {
                    return;
                }

                switch (frame.Type)
                {
                    case ProtocolFrameType.BulkStart:
                        if (kind == BulkProtocol.KindSpeedRequest)
                        {
                            await HandleSpeedRequestAsync(frame, metadata).ConfigureAwait(false);
                        }
                        else if (kind == BulkProtocol.KindSpeedData)
                        {
                            StartIncomingSpeedData(frame, metadata);
                        }
                        break;
                    case ProtocolFrameType.BulkChunk:
                        HandleIncomingSpeedChunk(frame);
                        break;
                    case ProtocolFrameType.BulkComplete:
                        CompleteIncomingSpeedData(frame, metadata);
                        break;
                    case ProtocolFrameType.BulkCancel:
                        CancelIncomingSpeed(frame, metadata);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Speed test frame handling failed: {ex.Message}");
            }
        }

        private async Task HandleSpeedRequestAsync(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            var requestedBytes = BulkProtocol.GetInt64(metadata, "sizeBytes", 0);
            var testId = BulkProtocol.GetString(metadata, "testId", Guid.NewGuid().ToString());
            try
            {
                await SendSpeedPayloadAsync(testId, requestedBytes, emitLocalProgress: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendBulkErrorAsync(frame.StreamId, "speed_send_failed", ex.Message).ConfigureAwait(false);
            }
        }

        private void StartIncomingSpeedData(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            var testId = BulkProtocol.GetString(metadata, "testId", frame.CorrelationId.ToString());
            var expectedSize = BulkProtocol.GetInt64(metadata, "sizeBytes", -1);
            var isLocalDownload = _pendingDownloads.ContainsKey(testId);
            var incoming = new IncomingSpeedStream(testId, expectedSize, DateTimeOffset.UtcNow, isLocalDownload);
            _incomingSpeedStreams[frame.StreamId] = incoming;
            if (incoming.IsLocalDownload)
            {
                OnStatusChanged($"Receiving speed test data: {expectedSize} bytes");
                OnProgressChanged(new SpeedTestProgressEventArgs(
                    SpeedTestType.Download,
                    0,
                    expectedSize,
                    0,
                    0));
            }
        }

        private void HandleIncomingSpeedChunk(ProtocolFrame frame)
        {
            if (!_incomingSpeedStreams.TryGetValue(frame.StreamId, out var incoming))
            {
                return;
            }

            incoming.BytesReceived += frame.Payload.Length;
            if (!incoming.IsLocalDownload)
            {
                return;
            }

            var duration = DateTimeOffset.UtcNow - incoming.StartedAt;
            var speedMbps = BulkProtocol.CalculateMbps(incoming.BytesReceived, duration);
            var progress = incoming.ExpectedSize > 0
                ? Math.Min(100, incoming.BytesReceived * 100.0 / incoming.ExpectedSize)
                : 0;

            OnProgressChanged(new SpeedTestProgressEventArgs(
                SpeedTestType.Download,
                incoming.BytesReceived,
                incoming.ExpectedSize,
                speedMbps,
                progress));
        }

        private void CompleteIncomingSpeedData(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            if (!_incomingSpeedStreams.TryRemove(frame.StreamId, out var incoming))
            {
                return;
            }

            var duration = DateTimeOffset.UtcNow - incoming.StartedAt;
            var speedMbps = BulkProtocol.CalculateMbps(incoming.BytesReceived, duration);
            var result = new SpeedTestResult
            {
                TestType = SpeedTestType.Download,
                DataSize = incoming.BytesReceived,
                Duration = duration,
                SpeedMbps = speedMbps,
                Success = incoming.ExpectedSize < 0 || incoming.BytesReceived == incoming.ExpectedSize
            };

            if (!incoming.IsLocalDownload)
            {
                OnStatusChanged($"Peer speed upload consumed: {incoming.BytesReceived} bytes");
                return;
            }

            OnProgressChanged(new SpeedTestProgressEventArgs(
                SpeedTestType.Download,
                incoming.BytesReceived,
                incoming.ExpectedSize,
                speedMbps,
                100));

            if (_pendingDownloads.TryGetValue(incoming.TestId, out var completionSource))
            {
                completionSource.TrySetResult(result);
            }

            OnStatusChanged($"Download completed - Speed: {speedMbps:F2} Mbps");
        }

        private void CancelIncomingSpeed(ProtocolFrame frame, IReadOnlyDictionary<string, JsonElement> metadata)
        {
            if (!_incomingSpeedStreams.TryRemove(frame.StreamId, out var incoming))
            {
                return;
            }

            var reason = BulkProtocol.GetString(metadata, "reason", "cancelled");
            if (incoming.IsLocalDownload && _pendingDownloads.TryGetValue(incoming.TestId, out var completionSource))
            {
                completionSource.TrySetException(new IOException($"Speed test cancelled: {reason}"));
            }

            if (incoming.IsLocalDownload)
            {
                OnErrorOccurred($"Speed test cancelled: {reason}");
            }
            else
            {
                OnStatusChanged($"Peer speed upload cancelled: {reason}");
            }
        }

        private async Task SendSpeedPayloadAsync(string testId, long sizeBytes, bool emitLocalProgress)
        {
            RequireReadySession();

            var safeSize = Math.Max(sizeBytes, 0);
            var streamId = BulkProtocol.NextStreamId();
            var startedAt = DateTimeOffset.UtcNow;
            var correlationId = BulkProtocol.GuidFromStringOrNew(testId);
            long bytesSent = 0;
            var buffer = new byte[BulkProtocol.ChunkSize];

            await _sessionManager!.SendBulkFrameAsync(
                ProtocolFrameType.BulkStart,
                BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                {
                    ["kind"] = BulkProtocol.KindSpeedData,
                    ["testId"] = testId,
                    ["sessionId"] = _sessionManager.CurrentSessionId,
                    ["sizeBytes"] = safeSize,
                    ["timestamp"] = startedAt.ToUnixTimeMilliseconds()
                }),
                streamId: streamId,
                correlationId: correlationId).ConfigureAwait(false);

            while (bytesSent < safeSize)
            {
                var chunkSize = (int)Math.Min(buffer.Length, safeSize - bytesSent);
                var payload = chunkSize == buffer.Length ? buffer : buffer[..chunkSize];

                await _sessionManager.SendBulkFrameAsync(
                    ProtocolFrameType.BulkChunk,
                    BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                    {
                        ["kind"] = BulkProtocol.KindSpeedData,
                        ["testId"] = testId,
                        ["offset"] = bytesSent
                    }),
                    payload,
                    streamId,
                    correlationId).ConfigureAwait(false);

                bytesSent += chunkSize;

                if (emitLocalProgress)
                {
                    var duration = DateTimeOffset.UtcNow - startedAt;
                    var speedMbps = BulkProtocol.CalculateMbps(bytesSent, duration);
                    var progress = safeSize > 0 ? bytesSent * 100.0 / safeSize : 100;
                    OnProgressChanged(new SpeedTestProgressEventArgs(
                        SpeedTestType.Upload,
                        bytesSent,
                        safeSize,
                        speedMbps,
                        progress));
                }
            }

            await _sessionManager.SendBulkFrameAsync(
                ProtocolFrameType.BulkComplete,
                BulkProtocol.BuildMetadata(new Dictionary<string, object?>
                {
                    ["kind"] = BulkProtocol.KindSpeedData,
                    ["testId"] = testId,
                    ["sizeBytes"] = bytesSent,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }),
                streamId: streamId,
                correlationId: correlationId).ConfigureAwait(false);

            if (emitLocalProgress)
            {
                var duration = DateTimeOffset.UtcNow - startedAt;
                var speedMbps = BulkProtocol.CalculateMbps(bytesSent, duration);
                OnProgressChanged(new SpeedTestProgressEventArgs(
                    SpeedTestType.Upload,
                    bytesSent,
                    safeSize,
                    speedMbps,
                    100));
            }
        }

        private async Task<bool> TryEnterSpeedTestAsync(SpeedTestType testType, long sizeBytes)
        {
            if (_sessionManager?.IsReady != true)
            {
                var error = "WDCable session is not ready for speed test";
                OnErrorOccurred(error);
                EmitFailureResult(testType, sizeBytes, error);
                return false;
            }

            if (!await _speedTestLock.WaitAsync(0).ConfigureAwait(false))
            {
                var error = "A speed test is already running";
                OnErrorOccurred(error);
                EmitFailureResult(testType, sizeBytes, error);
                return false;
            }

            return true;
        }

        private void EmitFailureResult(SpeedTestType testType, long sizeBytes, string error)
        {
            var result = new SpeedTestResult
            {
                TestType = testType,
                DataSize = Math.Max(sizeBytes, 0),
                Success = false,
                ErrorMessage = error
            };

            if (testType == SpeedTestType.Upload)
            {
                OnUploadCompleted(result);
            }
            else
            {
                OnDownloadCompleted(result);
            }
        }

        private void RequireReadySession()
        {
            if (_sessionManager?.IsReady != true)
            {
                throw new InvalidOperationException("WDCable session is not ready.");
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

        private void FailActiveSpeedTests(string message)
        {
            foreach (var pending in _pendingDownloads.Values)
            {
                pending.TrySetException(new IOException(message));
            }

            _pendingDownloads.Clear();
            _incomingSpeedStreams.Clear();
            _activeDownloadTestId = null;
        }

        private void OnStatusChanged(string status)
        {
            RaiseOnDispatcher(() => StatusChanged?.Invoke(this, status));
        }

        private void OnErrorOccurred(string error)
        {
            RaiseOnDispatcher(() => ErrorOccurred?.Invoke(this, error));
        }

        private void OnUploadCompleted(SpeedTestResult result)
        {
            RaiseOnDispatcher(() => UploadCompleted?.Invoke(this, result));
        }

        private void OnDownloadCompleted(SpeedTestResult result)
        {
            RaiseOnDispatcher(() => DownloadCompleted?.Invoke(this, result));
        }

        private void OnProgressChanged(SpeedTestProgressEventArgs args)
        {
            RaiseOnDispatcher(() => ProgressChanged?.Invoke(this, args));
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

        private sealed class IncomingSpeedStream
        {
            public IncomingSpeedStream(string testId, long expectedSize, DateTimeOffset startedAt, bool isLocalDownload)
            {
                TestId = testId;
                ExpectedSize = expectedSize;
                StartedAt = startedAt;
                IsLocalDownload = isLocalDownload;
            }

            public string TestId { get; }
            public long ExpectedSize { get; }
            public DateTimeOffset StartedAt { get; }
            public bool IsLocalDownload { get; }
            public long BytesReceived { get; set; }
        }
    }

    public class SpeedTestResult
    {
        public SpeedTestType TestType { get; set; }
        public long DataSize { get; set; }
        public TimeSpan Duration { get; set; }
        public double SpeedMbps { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class SpeedTestProgressEventArgs : EventArgs
    {
        public SpeedTestProgressEventArgs(
            SpeedTestType testType,
            long bytesTransferred,
            long totalBytes,
            double speedMbps,
            double progressPercentage)
        {
            TestType = testType;
            BytesTransferred = bytesTransferred;
            TotalBytes = totalBytes;
            SpeedMbps = speedMbps;
            ProgressPercentage = progressPercentage;
        }

        public SpeedTestType TestType { get; }
        public long BytesTransferred { get; }
        public long TotalBytes { get; }
        public double SpeedMbps { get; }
        public double ProgressPercentage { get; }
    }

    public enum SpeedTestType
    {
        Upload,
        Download
    }
}
