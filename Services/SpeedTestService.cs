using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace WDCableWUI.Services
{
    /// <summary>
    /// Singleton service for managing speed test functionality.
    /// Provides access to the current speed test connection through the WiFiDirectService.
    /// </summary>
    public class SpeedTestService : IDisposable
    {
        private static SpeedTestService? _instance;
        private static readonly object _lock = new object();
        private readonly DispatcherQueue _dispatcherQueue;
        private bool _isDisposed = false;
        private bool _isDownloadTestInProgress = false;
        private long _expectedDownloadSize = 0;
        private CancellationTokenSource? _listenerCancellationTokenSource;
        private Task? _listenerTask;
        
        // Events
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<SpeedTestResult>? UploadCompleted;
        public event EventHandler<SpeedTestResult>? DownloadCompleted;
        
        /// <summary>
        /// Gets the singleton SpeedTestService instance.
        /// </summary>
        public static SpeedTestService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new SpeedTestService();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Gets the current speed test connection from the ConnectionService.
        /// Returns null if no connection is available or if the connection is no longer valid.
        /// </summary>
        private TcpClient? SpeedTestConnection
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
                    
                    var speedTestConnection = connectionService.SpeedTestConnection;
                    
                    // Check if connection is still valid
                    if (speedTestConnection != null && !IsConnectionValid(speedTestConnection))
                    {
                        OnStatusChanged("Speed test connection is no longer valid, attempting to re-obtain connection");
                        // The connection will be re-established by the ConnectionService
                        return null;
                    }
                    
                    return speedTestConnection;
                }
                catch (Exception ex)
                {
                    OnErrorOccurred($"Error obtaining speed test connection: {ex.Message}");
                    return null;
                }
            }
        }
        
        /// <summary>
        /// Gets whether a valid speed test connection is currently available.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                var connection = SpeedTestConnection;
                return connection != null && IsConnectionValid(connection);
            }
        }
        
        /// <summary>
        /// Private constructor to enforce singleton pattern.
        /// </summary>
        private SpeedTestService()
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            
            // Subscribe to ConnectionService events
            var connectionService = ServiceManager.ConnectionService;
            if (connectionService != null)
            {
                
                System.Diagnostics.Debug.WriteLine("SpeedTestService initialized");
                connectionService.ConnectionsEstablished += OnConnectionsEstablished;
            }
            
            OnStatusChanged("SpeedTestService initialized");
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
        /// Raises the UploadCompleted event.
        /// </summary>
        /// <param name="result">The speed test result</param>
        private void OnUploadCompleted(SpeedTestResult result)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => UploadCompleted?.Invoke(this, result));
            }
            catch
            {
                // Ignore dispatcher errors during event reporting
            }
        }
        
        /// <summary>
        /// Raises the DownloadCompleted event.
        /// </summary>
        /// <param name="result">The speed test result</param>
        private void OnDownloadCompleted(SpeedTestResult result)
        {
            try
            {
                _dispatcherQueue?.TryEnqueue(() => DownloadCompleted?.Invoke(this, result));
            }
            catch
            {
                // Ignore dispatcher errors during event reporting
            }
        }
        
        /// <summary>
        /// Handles the ConnectionsEstablished event from ConnectionService.
        /// </summary>
        /// <param name="sender">The event sender</param>
        /// <param name="e">The event arguments</param>
        private void OnConnectionsEstablished(object? sender, EventArgs e)
        {
            // Automatically start listening when connections are established
            StartListening();
            
            OnStatusChanged("TCP connections established - Speed test is now available");
        }

        /// <summary>
        /// Starts listening for incoming speed test requests and data.
        /// This method should be called when a connection is established.
        /// </summary>
        public void StartListening()
        {
            StopListening(); // Stop any existing listener
            
            _listenerCancellationTokenSource = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenForIncomingData(_listenerCancellationTokenSource.Token));
            
            OnStatusChanged("Speed test listener started");
        }

        public void StopListening()
        {
            try
            {
                _listenerCancellationTokenSource?.Cancel();
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore errors during stop
            }
            finally
            {
                _listenerCancellationTokenSource?.Dispose();
                _listenerCancellationTokenSource = null;
                _listenerTask = null;
            }
            
            OnStatusChanged("Speed test listener stopped");
        }
        
        /// <summary>
        /// Performs an upload speed test by sending data to the connected client.
        /// </summary>
        /// <param name="sizeBytes">The amount of data to upload in bytes</param>
        /// <returns>A task that completes when the upload test is finished</returns>
        public async Task PerformUploadTest(long sizeBytes)
        {
            const int BUFFER_SIZE = 8192;
            var startTime = DateTime.UtcNow;
            
            try
            {
                var connection = SpeedTestConnection;
                if (connection == null || !connection.Connected)
                {
                    var errorMsg = "No valid speed test connection available";
                    OnErrorOccurred(errorMsg);
                    OnUploadCompleted(new SpeedTestResult
                    {
                        TestType = SpeedTestType.Upload,
                        DataSize = sizeBytes,
                        Success = false,
                        ErrorMessage = errorMsg
                    });
                    return;
                }
                
                OnStatusChanged($"Starting upload test - {sizeBytes} bytes");
                
                var stream = connection.GetStream();
                
                // Send protocol header
                var headerMessage = $"SPEED_TEST_DATA:{sizeBytes}\n";
                var headerBytes = Encoding.UTF8.GetBytes(headerMessage);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                await stream.FlushAsync();
                
                // Create buffer for data transmission
                var buffer = new byte[BUFFER_SIZE];
                
                long totalSent = 0;
                while (totalSent < sizeBytes)
                {
                    var remainingBytes = sizeBytes - totalSent;
                    var bytesToSend = (int)Math.Min(BUFFER_SIZE, remainingBytes);
                    
                    // await stream.WriteAsync(buffer, 0, bytesToSend);
                    stream.Write(buffer, 0, bytesToSend);

                    totalSent += bytesToSend;
                    
                    // Update progress
                    var progress = (double)totalSent / sizeBytes * 100;
                    OnStatusChanged($"Upload progress: {progress:F1}% ({totalSent}/{sizeBytes} bytes)");
                }
                
                await stream.FlushAsync();
                
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;
                var speedMbps = (sizeBytes * 8.0) / (1024 * 1024) / duration.TotalSeconds;
                
                OnStatusChanged($"Upload completed - Speed: {speedMbps:F2} Mbps");
                
                OnUploadCompleted(new SpeedTestResult
                {
                    TestType = SpeedTestType.Upload,
                    DataSize = sizeBytes,
                    Duration = duration,
                    SpeedMbps = speedMbps,
                    Success = true
                });
            }
            catch (Exception ex)
            {
                var errorMsg = $"Upload test failed: {ex.Message}";
                OnErrorOccurred(errorMsg);
                
                var endTime = DateTime.UtcNow;
                var duration = endTime - startTime;
                
                OnUploadCompleted(new SpeedTestResult
                {
                    TestType = SpeedTestType.Upload,
                    DataSize = sizeBytes,
                    Duration = duration,
                    Success = false,
                    ErrorMessage = errorMsg
                });
            }
        }
         
         /// <summary>
         /// Performs a download speed test by requesting data from the connected client.
         /// </summary>
         /// <param name="sizeBytes">The amount of data to request for download in bytes</param>
         /// <returns>A task that completes when the download request is sent</returns>
         public async Task PerformDownloadTest(long sizeBytes)
         {
             try
             {
                 var connection = SpeedTestConnection;
                 if (connection == null || !connection.Connected)
                 {
                     var errorMsg = "No valid speed test connection available";
                     OnErrorOccurred(errorMsg);
                     OnDownloadCompleted(new SpeedTestResult
                     {
                         TestType = SpeedTestType.Download,
                         DataSize = sizeBytes,
                         Success = false,
                         ErrorMessage = errorMsg
                     });
                     return;
                 }
                 
                 OnStatusChanged($"Starting download test - requesting {sizeBytes} bytes");
                 
                 var stream = connection.GetStream();
                 
                 // Send download request
                 var requestMessage = $"SPEED_TEST_REQUEST:{sizeBytes}\n";
                 var requestBytes = Encoding.UTF8.GetBytes(requestMessage);
                 await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                 await stream.FlushAsync();
                 
                 // Set download test state
                 _isDownloadTestInProgress = true;
                 _expectedDownloadSize = sizeBytes;
                 
                 OnStatusChanged("Download request sent, waiting for data from remote client");
             }
             catch (Exception ex)
             {
                 var errorMsg = $"Download test request failed: {ex.Message}";
                 OnErrorOccurred(errorMsg);
                 
                 _isDownloadTestInProgress = false;
                 _expectedDownloadSize = 0;
                 
                 OnDownloadCompleted(new SpeedTestResult
                 {
                     TestType = SpeedTestType.Download,
                     DataSize = sizeBytes,
                     Success = false,
                     ErrorMessage = errorMsg
                 });
             }
         }
         
         /// <summary>
         /// Gets whether a download test is currently in progress.
         /// </summary>
         public bool IsDownloadTestInProgress => _isDownloadTestInProgress;
         
         /// <summary>
         /// Gets the expected download size for the current test.
         /// </summary>
         public long ExpectedDownloadSize => _expectedDownloadSize;
         
         /// <summary>
          /// Resets the download test state. Should be called when download is completed or cancelled.
          /// </summary>
          internal void ResetDownloadTestState()
          {
              _isDownloadTestInProgress = false;
              _expectedDownloadSize = 0;
          }
          
          /// <summary>
          /// Listens for incoming data on the speed test connection.
          /// </summary>
          /// <param name="cancellationToken">Token to cancel the listening operation</param>
          private async Task ListenForIncomingData(CancellationToken cancellationToken)
          {
              try
              {
                  while (!cancellationToken.IsCancellationRequested)
                  {
                      var connection = SpeedTestConnection;
                      if (connection == null || !connection.Connected)
                      {
                          await Task.Delay(1000, cancellationToken); // Wait before retrying
                          continue;
                      }
                      
                      var stream = connection.GetStream();
                      
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
                      
                      var headerMessage = Encoding.UTF8.GetString(headerBytes.ToArray());
                      
                      // Parse and handle the message
                      await HandleIncomingMessage(headerMessage, stream, cancellationToken);
                  }
              }
              catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
              {
                  OnErrorOccurred($"Listener error: {ex.Message}");
              }
          }
          
          /// <summary>
          /// Handles incoming speed test messages.
          /// </summary>
          /// <param name="headerMessage">The header message received</param>
          /// <param name="stream">The network stream</param>
          /// <param name="cancellationToken">Cancellation token</param>
          private async Task HandleIncomingMessage(string headerMessage, NetworkStream stream, CancellationToken cancellationToken)
          {
              try
              {
                  if (headerMessage.StartsWith("SPEED_TEST_REQUEST:"))
                  {
                      // Handle upload request from remote client
                      var sizeStr = headerMessage.Substring("SPEED_TEST_REQUEST:".Length);
                      if (long.TryParse(sizeStr, out long requestedSize))
                      {
                          OnStatusChanged($"Received upload request for {requestedSize} bytes");
                          
                          // Use our upload function to send data to help remote client test download
                          await SendDataForRemoteDownloadTest(requestedSize, stream, cancellationToken);
                      }
                  }
                  else if (headerMessage.StartsWith("SPEED_TEST_DATA:"))
                  {
                      // Handle incoming data
                      var sizeStr = headerMessage.Substring("SPEED_TEST_DATA:".Length);
                      if (long.TryParse(sizeStr, out long dataSize))
                      {
                          if (_isDownloadTestInProgress && dataSize == _expectedDownloadSize)
                          {
                              // This is the data we requested for our download test
                              await HandleDownloadTestData(dataSize, stream, cancellationToken);
                          }
                          else
                          {
                              // This is unsolicited data or upload test data, just drop it
                              OnStatusChanged($"Dropping {dataSize} bytes of unsolicited data");
                              await DropIncomingData(dataSize, stream, cancellationToken);
                          }
                      }
                  }
              }
              catch (Exception ex)
              {
                  OnErrorOccurred($"Error handling message '{headerMessage}': {ex.Message}");
              }
          }
          
          /// <summary>
          /// Sends data to help remote client perform download test.
          /// </summary>
          private async Task SendDataForRemoteDownloadTest(long sizeBytes, NetworkStream stream, CancellationToken cancellationToken)
          {
              const int BUFFER_SIZE = 8192;
              
              try
              {
                  // Send data header first
                  var dataHeader = $"SPEED_TEST_DATA:{sizeBytes}\n";
                  var headerBytes = Encoding.UTF8.GetBytes(dataHeader);
                  await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken);
                  await stream.FlushAsync(cancellationToken);
                  
                  // Send the actual data
                  var buffer = new byte[BUFFER_SIZE];
                  for (int i = 0; i < BUFFER_SIZE; i++)
                  {
                      buffer[i] = (byte)(i % 256);
                  }
                  
                  long totalSent = 0;
                  while (totalSent < sizeBytes && !cancellationToken.IsCancellationRequested)
                  {
                      var remainingBytes = sizeBytes - totalSent;
                      var bytesToSend = (int)Math.Min(BUFFER_SIZE, remainingBytes);
                      
                      await stream.WriteAsync(buffer, 0, bytesToSend, cancellationToken);
                      totalSent += bytesToSend;
                  }
                  
                  await stream.FlushAsync(cancellationToken);
                  OnStatusChanged($"Sent {totalSent} bytes to help remote download test");
              }
              catch (Exception ex)
              {
                  OnErrorOccurred($"Error sending data for remote download test: {ex.Message}");
              }
          }
          
          /// <summary>
          /// Handles incoming download test data and measures speed.
          /// </summary>
          private async Task HandleDownloadTestData(long expectedSize, NetworkStream stream, CancellationToken cancellationToken)
          {
              const int BUFFER_SIZE = 8192;
              var startTime = DateTime.UtcNow;
              
              try
              {
                  OnStatusChanged("Receiving download test data");
                  
                  var buffer = new byte[BUFFER_SIZE];
                  long totalReceived = 0;
                  
                  while (totalReceived < expectedSize && !cancellationToken.IsCancellationRequested)
                  {
                      var remainingBytes = expectedSize - totalReceived;
                      var bytesToRead = (int)Math.Min(BUFFER_SIZE, remainingBytes);
                      
                      var bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                      if (bytesRead == 0)
                      {
                          break; // Connection closed
                      }
                      
                      totalReceived += bytesRead;
                      
                      // Update progress
                      var progress = (double)totalReceived / expectedSize * 100;
                      OnStatusChanged($"Download progress: {progress:F1}% ({totalReceived}/{expectedSize} bytes)");
                  }
                  
                  var endTime = DateTime.UtcNow;
                  var duration = endTime - startTime;
                  var speedMbps = (totalReceived * 8.0) / (1024 * 1024) / duration.TotalSeconds;
                  
                  ResetDownloadTestState();
                  
                  OnStatusChanged($"Download completed - Speed: {speedMbps:F2} Mbps");
                  
                  OnDownloadCompleted(new SpeedTestResult
                  {
                      TestType = SpeedTestType.Download,
                      DataSize = totalReceived,
                      Duration = duration,
                      SpeedMbps = speedMbps,
                      Success = totalReceived == expectedSize
                  });
              }
              catch (Exception ex)
              {
                  ResetDownloadTestState();
                  OnErrorOccurred($"Error during download test: {ex.Message}");
                  
                  OnDownloadCompleted(new SpeedTestResult
                  {
                      TestType = SpeedTestType.Download,
                      DataSize = expectedSize,
                      Success = false,
                      ErrorMessage = ex.Message
                  });
              }
          }
          
          /// <summary>
          /// Drops incoming data that we don't need.
          /// </summary>
          private async Task DropIncomingData(long dataSize, NetworkStream stream, CancellationToken cancellationToken)
          {
              const int BUFFER_SIZE = 8192;
              
              try
              {
                  var buffer = new byte[BUFFER_SIZE];
                  long totalDropped = 0;
                  
                  while (totalDropped < dataSize && !cancellationToken.IsCancellationRequested)
                  {
                      var remainingBytes = dataSize - totalDropped;
                      var bytesToRead = (int)Math.Min(BUFFER_SIZE, remainingBytes);
                      
                      var bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);
                      if (bytesRead == 0)
                      {
                          break; // Connection closed
                      }
                      
                      totalDropped += bytesRead;
                  }
                  
                  OnStatusChanged($"Dropped {totalDropped} bytes of unwanted data");
              }
              catch (Exception ex)
              {
                  OnErrorOccurred($"Error dropping data: {ex.Message}");
              }
          }
         
         /// <summary>
         /// Disposes the SpeedTestService and cleans up resources.
         /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            
            try
            {
                // Stop listening for requests
                StopListening();
                
                // Unsubscribe from ConnectionService events
                var connectionService = ServiceManager.ConnectionService;
                if (connectionService != null)
                {
                    connectionService.ConnectionsEstablished -= OnConnectionsEstablished;
                }
                
                OnStatusChanged("SpeedTestService disposing");
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
    /// Represents the result of a speed test operation.
    /// </summary>
    public class SpeedTestResult
    {
        public SpeedTestType TestType { get; set; }
        public long DataSize { get; set; }
        public TimeSpan Duration { get; set; }
        public double SpeedMbps { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
    
    /// <summary>
    /// Enumeration of speed test types.
    /// </summary>
    public enum SpeedTestType
    {
        Upload,
        Download
    }
}