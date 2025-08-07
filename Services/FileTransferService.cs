using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Storage;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Singleton service for managing file transfer functionality.
    /// Provides access to the current file transfer connection through the ConnectionService.
    /// </summary>
    public class FileTransferService : IDisposable
    {
        private static FileTransferService? _instance;
        private static readonly object _lock = new object();
        
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isDisposed = false;
        private CancellationTokenSource? _listeningCancellationTokenSource;
        private Task? _listeningTask;
        private string _downloadPath;
        
        // Events
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<FileTransferEventArgs>? FileReceived;
        public event EventHandler<FileTransferEventArgs>? FileSent;
        public event EventHandler<FileTransferProgressEventArgs>? TransferProgress;
        public event EventHandler<FileReceiveStartedEventArgs>? FileReceiveStarted;
        
        /// <summary>
        /// Gets the singleton FileTransferService instance.
        /// </summary>
        public static FileTransferService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new FileTransferService();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Gets the current file transfer connection from the ConnectionService.
        /// Returns null if no connection is available or if the connection is no longer valid.
        /// </summary>
        public TcpClient? FileTransferConnection
        {
            get
            {
                try
                {
                    var connectionService = ServiceManager.ConnectionService;
                    if (connectionService == null)
                    {
                        return null;
                    }
                    
                    var fileConnection = connectionService.FileConnection;
                    
                    // Check if connection is still valid
                    if (fileConnection != null && !IsConnectionValid(fileConnection))
                    {
                        OnStatusChanged("File transfer connection is no longer valid, attempting to re-obtain connection");
                        // The connection will be re-established by the ConnectionService
                        return null;
                    }
                    
                    return fileConnection;
                }
                catch (Exception ex)
                {
                    OnErrorOccurred($"Error obtaining file transfer connection: {ex.Message}");
                    return null;
                }
            }
        }
        
        /// <summary>
        /// Gets whether a valid file transfer connection is currently available.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                var connection = FileTransferConnection;
                return connection != null && IsConnectionValid(connection);
            }
        }
        
        /// <summary>
        /// Gets the current download path.
        /// </summary>
        public string DownloadPath => _downloadPath;
        
        /// <summary>
        /// Sets the download path and saves it to DataManager.
        /// </summary>
        /// <param name="path">The new download path</param>
        public void SetDownloadPath(string path)
        {
            try
            {
                _downloadPath = path;
                var dataManager = ServiceManager.DataManager;
                dataManager?.SetDownloadPath(path);
                OnStatusChanged($"Download path updated: {path}");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to update download path: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Private constructor to enforce singleton pattern.
        /// </summary>
        private FileTransferService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            
            // Get download path from DataManager
            try
            {
                var dataManager = ServiceManager.DataManager;
                _downloadPath = dataManager?.GetDownloadPath() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            catch
            {
                // Fallback to default path if DataManager is not available
                _downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            
            // Subscribe to ConnectionService events
            var connectionService = ServiceManager.ConnectionService;
            if (connectionService != null)
            {
                connectionService.ConnectionsEstablished += OnConnectionsEstablished;
            }
            
            OnStatusChanged("FileTransferService initialized");
        }
        
        /// <summary>
        /// Checks if a TCP connection is still valid and connected.
        /// </summary>
        /// <param name="connection">The TCP connection to check</param>
        /// <returns>True if the connection is valid and connected, false otherwise</returns>
        private bool IsConnectionValid(TcpClient connection)
        {
            try
            {
                if (connection == null || !connection.Connected)
                {
                    return false;
                }
                
                // Additional check using socket polling
                var socket = connection.Client;
                if (socket == null)
                {
                    return false;
                }
                
                // Poll the socket to check if it's still connected
                bool part1 = socket.Poll(1000, SelectMode.SelectRead);
                bool part2 = (socket.Available == 0);
                
                // If both conditions are true, the connection is closed
                return !(part1 && part2);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Raises the StatusChanged event.
        /// </summary>
        /// <param name="status">The status message</param>
        private void OnStatusChanged(string status)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => StatusChanged?.Invoke(this, status));
            }
            catch
            {
                // Ignore dispatcher errors during status updates
            }
        }
        
        /// <summary>
        /// Raises the ErrorOccurred event.
        /// </summary>
        /// <param name="error">The error message</param>
        private void OnErrorOccurred(string error)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => ErrorOccurred?.Invoke(this, error));
            }
            catch
            {
                // Ignore dispatcher errors during error reporting
            }
        }
        
        /// <summary>
        /// Handles the ConnectionsEstablished event from ConnectionService.
        /// </summary>
        /// <param name="sender">The event sender</param>
        /// <param name="e">The event arguments</param>
        private void OnConnectionsEstablished(object? sender, EventArgs e)
        {
            OnStatusChanged("TCP connections established - File transfer is now available");
            
            // Automatically start listening when connections are established
            StartListening();
        }
        
        /// <summary>
        /// Raises the FileReceived event.
        /// </summary>
        /// <param name="fileName">The received file name</param>
        /// <param name="filePath">The path where the file was saved</param>
        /// <param name="fileSize">The size of the received file</param>
        private void OnFileReceived(string fileName, string filePath, long fileSize)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => {
                    FileReceived?.Invoke(this, new FileTransferEventArgs(fileName, filePath, fileSize));
                });
            }
            catch (Exception ex)
            {
                // Ignore dispatcher errors during event delivery
            }
        }
        
        /// <summary>
        /// Raises the FileSent event.
        /// </summary>
        /// <param name="fileName">The sent file name</param>
        /// <param name="filePath">The path of the sent file</param>
        /// <param name="fileSize">The size of the sent file</param>
        private void OnFileSent(string fileName, string filePath, long fileSize)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => {
                    FileSent?.Invoke(this, new FileTransferEventArgs(fileName, filePath, fileSize));
                });
            }
            catch (Exception ex)
            {
                // Ignore dispatcher errors during event delivery
            }
        }
        
        /// <summary>
        /// Raises the TransferProgress event.
        /// </summary>
        /// <param name="fileName">The file name being transferred</param>
        /// <param name="bytesTransferred">The number of bytes transferred</param>
        /// <param name="totalBytes">The total number of bytes to transfer</param>
        private void OnTransferProgress(string fileName, long bytesTransferred, long totalBytes)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => {
                    TransferProgress?.Invoke(this, new FileTransferProgressEventArgs(fileName, bytesTransferred, totalBytes));
                });
            }
            catch (Exception ex)
            {
                // Ignore dispatcher errors during event delivery
            }
        }
        
        /// <summary>
        /// Raises the FileReceiveStarted event.
        /// </summary>
        /// <param name="fileName">The file name being received</param>
        /// <param name="fileSize">The size of the file being received</param>
        private void OnFileReceiveStarted(string fileName, long fileSize)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => {
                    FileReceiveStarted?.Invoke(this, new FileReceiveStartedEventArgs(fileName, fileSize));
                });
            }
            catch (Exception ex)
            {
                // Ignore dispatcher errors during event delivery
            }
        }
        


        /// Sends a file through the file transfer connection.
        /// </summary>
        /// <param name="file">The file to send</param>
        public async Task SendFileAsync(StorageFile file)
        {
            if (file == null)
            {
                OnErrorOccurred("Cannot send null file");
                return;
            }
            
            var connection = FileTransferConnection;
            if (connection == null || !IsConnectionValid(connection))
            {
                OnErrorOccurred("No valid file transfer connection available");
                return;
            }
            
            try
            {
                var stream = connection.GetStream();
                var properties = await file.GetBasicPropertiesAsync();
                var fileSize = (long)properties.Size;
                
                // Send file header
                var fileInfo = $"FILE:{file.Name}:{fileSize}\n";
                var headerBytes = Encoding.UTF8.GetBytes(fileInfo);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.FlushAsync();
                
                OnStatusChanged($"Sending file: {file.Name} ({fileSize} bytes)");
                
                // Send file content
                using var fileStream = await file.OpenStreamForReadAsync();
                var buffer = new byte[8192]; // 8KB buffer
                long totalBytesRead = 0;
                int bytesRead;
                
                while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    stream.Write(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;
                    
                    // Report progress
                    OnTransferProgress(file.Name, totalBytesRead, fileSize);
                }
                
                await stream.FlushAsync();
                OnStatusChanged($"File sent successfully: {file.Name}");
                OnFileSent(file.Name, file.Path, fileSize);
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to send file: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Starts listening for incoming files on the file transfer connection.
        /// This method should be called when a connection is established.
        /// </summary>
        public void StartListening()
        {
            var connection = FileTransferConnection;
            if (connection == null || !IsConnectionValid(connection))
            {
                OnErrorOccurred("No valid file transfer connection available for listening");
                return;
            }
            
            try
            {
                // Stop any existing listening task
                StopListening();
                
                // Create new cancellation token
                _listeningCancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = _listeningCancellationTokenSource.Token;
                
                // Start listening task
                _listeningTask = Task.Run(async () =>
                {
                    try
                    {
                        var stream = connection.GetStream();
                        
                        while (!cancellationToken.IsCancellationRequested && connection.Connected)
                        {
                            // Read header until newline
                            var headerBytes = new List<byte>();
                            int byteRead;
                            
                            while ((byteRead = stream.ReadByte()) != -1)
                            {
                                if (cancellationToken.IsCancellationRequested)
                                    return;
                                    
                                if (byteRead == '\n')
                                {
                                    break; // Found newline, header complete
                                }
                                
                                headerBytes.Add((byte)byteRead);
                            }
                            
                            if (headerBytes.Count == 0)
                            {
                                continue; // No header received
                            }
                            
                            var headerLine = Encoding.UTF8.GetString(headerBytes.ToArray());
                            
                            if (headerLine.StartsWith("FILE:"))
                            {
                                string currentDownloadPath;
                                lock (_lock)
                                {
                                    currentDownloadPath = _downloadPath;
                                }
                                await HandleIncomingFile(headerLine, stream, cancellationToken, currentDownloadPath);
                            }
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        OnErrorOccurred($"Error in file transfer listener: {ex.Message}");
                    }
                }, cancellationToken);
                
                OnStatusChanged("Started listening for file transfers");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Failed to start listening: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handles an incoming file transfer.
        /// </summary>
        /// <param name="fileHeader">The file header containing file info</param>
        /// <param name="stream">The network stream to read from</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="downloadPath">Optional custom download path, defaults to Downloads folder</param>
        private async Task HandleIncomingFile(string fileHeader, NetworkStream stream, CancellationToken cancellationToken, string? downloadPath = null)
        {
            try
            {
                // Parse file header: FILE:filename:filesize
                var parts = fileHeader.Split(':');
                if (parts.Length != 3)
                {
                    OnErrorOccurred("Invalid file header format");
                    return;
                }
                
                var fileName = parts[1];
                if (!long.TryParse(parts[2], out var fileSize))
                {
                    OnErrorOccurred("Invalid file size in header");
                    return;
                }
                
                OnStatusChanged($"Receiving file: {fileName} ({fileSize} bytes)");
                
                // Notify that file reception has started
                OnFileReceiveStarted(fileName, fileSize);
                OnTransferProgress(fileName, 0, fileSize);
                
                // Create download path
                var targetPath = downloadPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var downloadsFolder = await StorageFolder.GetFolderFromPathAsync(targetPath);
                var file = await downloadsFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                
                // Receive file content
                using var fileStream = await file.OpenStreamForWriteAsync();
                var buffer = new byte[8192]; // 8KB buffer
                long totalBytesReceived = 0;
                
                while (totalBytesReceived < fileSize && !cancellationToken.IsCancellationRequested)
                {
                    var bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalBytesReceived);
                    var bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                    
                    if (bytesRead == 0)
                    {
                        break; // Connection closed
                    }
                    
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalBytesReceived += bytesRead;
                    
                    // Report progress
                    OnTransferProgress(fileName, totalBytesReceived, fileSize);
                }
                
                if (totalBytesReceived == fileSize)
                {
                    OnStatusChanged($"File received successfully: {fileName}");
                    OnFileReceived(fileName, file.Path, fileSize);
                }
                else
                {
                    OnErrorOccurred($"File transfer incomplete: {fileName}");
                }
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error receiving file: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Stops listening for incoming files.
        /// </summary>
        public void StopListening()
        {
            try
            {
                // Cancel the listening task
                _listeningCancellationTokenSource?.Cancel();
                
                // Wait for the task to complete (with timeout)
                if (_listeningTask != null && !_listeningTask.IsCompleted)
                {
                    try
                    {
                        _listeningTask.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (AggregateException)
                    {
                        // Task was cancelled, which is expected
                    }
                }
                
                // Clean up resources
                _listeningCancellationTokenSource?.Dispose();
                _listeningCancellationTokenSource = null;
                _listeningTask = null;
                
                OnStatusChanged("Stopped listening for file transfers");
            }
            catch (Exception ex)
            {
                OnErrorOccurred($"Error stopping listener: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Disposes the FileTransferService and cleans up resources.
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            
            try
            {
                // Stop listening for files
                StopListening();
                
                // Unsubscribe from ConnectionService events
                var connectionService = ServiceManager.ConnectionService;
                if (connectionService != null)
                {
                    connectionService.ConnectionsEstablished -= OnConnectionsEstablished;
                }
                
                OnStatusChanged("FileTransferService disposing");
                _isDisposed = true;
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
        
        /// <summary>
        /// Resets the singleton instance. Used for testing or service restart scenarios.
        /// </summary>
        public static void ResetInstance()
        {
            lock (_lock)
            {
                _instance?.Dispose();
                _instance = null;
            }
        }
    }
    
    /// <summary>
    /// Event arguments for file transfer events.
    /// </summary>
    public class FileTransferEventArgs : EventArgs
    {
        public string FileName { get; }
        public string FilePath { get; }
        public long FileSize { get; }
        
        public FileTransferEventArgs(string fileName, string filePath, long fileSize)
        {
            FileName = fileName;
            FilePath = filePath;
            FileSize = fileSize;
        }
    }
    
    /// <summary>
    /// Event arguments for file transfer progress events.
    /// </summary>
    public class FileTransferProgressEventArgs : EventArgs
    {
        public string FileName { get; }
        public long BytesTransferred { get; }
        public long TotalBytes { get; }
        public double ProgressPercentage => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
        
        public FileTransferProgressEventArgs(string fileName, long bytesTransferred, long totalBytes)
        {
            FileName = fileName;
            BytesTransferred = bytesTransferred;
            TotalBytes = totalBytes;
        }
    }
    
    /// <summary>
    /// Event arguments for file receive started events.
    /// </summary>
    public class FileReceiveStartedEventArgs : EventArgs
    {
        public string FileName { get; }
        public long FileSize { get; }
        
        public FileReceiveStartedEventArgs(string fileName, long fileSize)
        {
            FileName = fileName;
            FileSize = fileSize;
        }
    }
}