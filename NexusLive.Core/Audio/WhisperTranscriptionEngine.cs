using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace NexusLive.Core.Audio
{
    public class WhisperTranscriptionEngine : ITranscriptionEngine, IDisposable
    {
        public event EventHandler<TranscriptionSegmentEventArgs>? SegmentTranscribed;
        
        // State management
        private bool _isInitialized;
        private string? _modelPath;
        private bool _isCapturing;
        
        // High-Priority Audio Buffer
        private struct AudioBufferItem
        {
            public float[] Data;
            public int Length;
        }
        private readonly ConcurrentQueue<AudioBufferItem> _audioQueue = new();
        private const int MaxQueueSize = 120; // ~12 seconds of buffering at 100ms frames
        
        // NAudio Capture Device
        private WasapiCapture? _captureDevice;
        
        // Watchdog & Heartbeat
        private DateTime _lastAudioActivityTime;
        private CancellationTokenSource? _watchdogCts;
        private readonly double _silenceThreshold = 0.001; // Amplitude threshold for audio activity detection
        
        // Zero-Allocation Memory Management
        private readonly TranscriptionSegment _reusableSegment = new();
        private TranscriptionSegmentEventArgs? _reusableEventArgs;
        
        // Scratch buffers for zero-allocation conversion
        private float[] _nativeMonoScratch = new float[8192];
        private float[] _resampledScratch = new float[4096];
        
        // Background Transcription Processing
        private CancellationTokenSource? _processingCts;
        private Task? _processingTask;

        public Task InitializeAsync(string modelPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("Model path cannot be null or empty.", nameof(modelPath));
            }

            _modelPath = modelPath;
            _reusableEventArgs = new TranscriptionSegmentEventArgs(_reusableSegment);
            _isInitialized = true;
            
            // Start background loop for transcription processing
            _processingCts = new CancellationTokenSource();
            _processingTask = Task.Run(() => ProcessAudioQueueLoopAsync(_processingCts.Token));

            // Start native Windows mic capture
            StartCapture();
            
            return Task.CompletedTask;
        }

        public void StartCapture()
        {
            if (_isCapturing) return;
            
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                
                // 1. Force hardware AGC to turn OFF via Device Topology
                TryDisableHardwareAgc(device);
                
                // 2. Initialize WASAPI capture (uses MixFormat by default for reliability, bypassing AGC effects)
                _captureDevice = new WasapiCapture(device);
                _captureDevice.DataAvailable += OnAudioDataAvailable;
                
                _captureDevice.StartRecording();
                _isCapturing = true;
                _lastAudioActivityTime = DateTime.UtcNow;
                
                // Start Audio Watchdog monitor
                _watchdogCts = new CancellationTokenSource();
                Task.Run(() => AudioWatchdogLoopAsync(_watchdogCts.Token));
                
                Console.WriteLine($"[AUDIO] Recording started. Format: {_captureDevice.WaveFormat.SampleRate}Hz, {_captureDevice.WaveFormat.Channels} channels, {_captureDevice.WaveFormat.BitsPerSample} bits.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO ERROR] Failed to start native audio capture: {ex.Message}");
            }
        }

        public void StopCapture()
        {
            _watchdogCts?.Cancel();
            _watchdogCts?.Dispose();
            _watchdogCts = null;
            
            if (_captureDevice != null)
            {
                try
                {
                    _captureDevice.StopRecording();
                    _captureDevice.DataAvailable -= OnAudioDataAvailable;
                    _captureDevice.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AUDIO ERROR] Error stopping capture device: {ex.Message}");
                }
                _captureDevice = null;
            }
            _isCapturing = false;
        }

        private void RestartCapture()
        {
            Console.WriteLine("[WATCHDOG] Restarting audio capture device transparently...");
            StopCapture();
            Thread.Sleep(250); // Small cooldown
            StartCapture();
        }

        private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0 || _captureDevice == null) return;
            
            // Read bytes as a Span for high-performance zero-allocation manipulation
            ReadOnlySpan<byte> audioBytes = new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded);
            
            int nativeSampleRate = _captureDevice.WaveFormat.SampleRate;
            int nativeChannels = _captureDevice.WaveFormat.Channels;
            int nativeBits = _captureDevice.WaveFormat.BitsPerSample;
            
            // Convert to 16kHz mono and get the rented array
            float[] rentedBuffer = ConvertAndResample(audioBytes, nativeSampleRate, nativeChannels, nativeBits, out int outLength);
            
            if (outLength > 0)
            {
                // Check signal amplitude for Heartbeat
                bool hasSignal = CheckAudioSignal(new ReadOnlySpan<float>(rentedBuffer, 0, outLength));
                if (hasSignal)
                {
                    _lastAudioActivityTime = DateTime.UtcNow;
                }
                
                // Enqueue to the high-priority buffer
                _audioQueue.Enqueue(new AudioBufferItem { Data = rentedBuffer, Length = outLength });
                
                // Size-limiting strategy: Discard oldest frames if capacity is reached
                while (_audioQueue.Count > MaxQueueSize)
                {
                    if (_audioQueue.TryDequeue(out var discarded))
                    {
                        ArrayPool<float>.Shared.Return(discarded.Data);
                    }
                }
            }
            else
            {
                ArrayPool<float>.Shared.Return(rentedBuffer);
            }
        }

        public Task ProcessAudioAsync(float[] pcmData, CancellationToken cancellationToken)
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Transcription engine is not initialized.");
            }

            if (pcmData == null || pcmData.Length == 0)
            {
                return Task.CompletedTask;
            }

            // Rent array from pool to copy external PCM data
            var rented = ArrayPool<float>.Shared.Rent(pcmData.Length);
            Array.Copy(pcmData, rented, pcmData.Length);
            
            // Check signal amplitude for Heartbeat
            bool hasSignal = CheckAudioSignal(pcmData);
            if (hasSignal)
            {
                _lastAudioActivityTime = DateTime.UtcNow;
            }

            _audioQueue.Enqueue(new AudioBufferItem { Data = rented, Length = pcmData.Length });

            while (_audioQueue.Count > MaxQueueSize)
            {
                if (_audioQueue.TryDequeue(out var discarded))
                {
                    ArrayPool<float>.Shared.Return(discarded.Data);
                }
            }

            return Task.CompletedTask;
        }

        private float[] ConvertAndResample(ReadOnlySpan<byte> bytes, int sampleRate, int channels, int bitsPerSample, out int outLength)
        {
            int nativeFrames;
            
            if (bitsPerSample == 32)
            {
                ReadOnlySpan<float> nativeFloats = MemoryMarshal.Cast<byte, float>(bytes);
                nativeFrames = nativeFloats.Length / channels;
                EnsureScratchCapacity(nativeFrames, (int)(nativeFrames * 16000.0 / sampleRate) + 2);
                
                for (int i = 0; i < nativeFrames; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += nativeFloats[i * channels + ch];
                    }
                    _nativeMonoScratch[i] = sum / channels;
                }
            }
            else if (bitsPerSample == 16)
            {
                ReadOnlySpan<short> nativeShorts = MemoryMarshal.Cast<byte, short>(bytes);
                nativeFrames = nativeShorts.Length / channels;
                EnsureScratchCapacity(nativeFrames, (int)(nativeFrames * 16000.0 / sampleRate) + 2);
                
                for (int i = 0; i < nativeFrames; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += nativeShorts[i * channels + ch] / 32768.0f;
                    }
                    _nativeMonoScratch[i] = sum / channels;
                }
            }
            else
            {
                // Fallback for 24-bit PCM
                int bytesPerSample = bitsPerSample / 8;
                nativeFrames = bytes.Length / bytesPerSample / channels;
                EnsureScratchCapacity(nativeFrames, (int)(nativeFrames * 16000.0 / sampleRate) + 2);
                
                for (int i = 0; i < nativeFrames; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int byteOffset = (i * channels + ch) * bytesPerSample;
                        if (bitsPerSample == 24)
                        {
                            int sampleVal = (bytes[byteOffset] | (bytes[byteOffset + 1] << 8) | (bytes[byteOffset + 2] << 16));
                            if ((sampleVal & 0x800000) != 0) sampleVal |= unchecked((int)0xFF000000); // Sign extension
                            sum += sampleVal / 8388608.0f;
                        }
                    }
                    _nativeMonoScratch[i] = sum / channels;
                }
            }
            
            // Linear Resampling to 16000 Hz
            double ratio = (double)sampleRate / 16000.0;
            int outCount = (int)(nativeFrames / ratio);
            
            float outSampleIndex = 0.0f;
            int outIndex = 0;
            while (outIndex < outCount && outSampleIndex < nativeFrames - 1)
            {
                int index1 = (int)outSampleIndex;
                int index2 = index1 + 1;
                float frac = (float)(outSampleIndex - index1);
                float interpolated = _nativeMonoScratch[index1] * (1.0f - frac) + _nativeMonoScratch[index2] * frac;
                _resampledScratch[outIndex++] = interpolated;
                outSampleIndex += (float)ratio;
            }
            
            outLength = outIndex;
            
            // Rent buffer from pool for queue processing (Zero-Allocation steady state)
            float[] rented = ArrayPool<float>.Shared.Rent(outIndex);
            Array.Copy(_resampledScratch, rented, outIndex);
            return rented;
        }

        private void EnsureScratchCapacity(int nativeFrames, int resampledCount)
        {
            if (_nativeMonoScratch.Length < nativeFrames)
            {
                _nativeMonoScratch = new float[nativeFrames * 2];
            }
            if (_resampledScratch.Length < resampledCount)
            {
                _resampledScratch = new float[resampledCount * 2];
            }
        }

        private bool CheckAudioSignal(ReadOnlySpan<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (Math.Abs(buffer[i]) > _silenceThreshold)
                {
                    return true;
                }
            }
            return false;
        }

        private void TryDisableHardwareAgc(MMDevice device)
        {
            try
            {
                var topology = device.DeviceTopology;
                if (topology == null) return;
                
                int connectorCount = (int)topology.ConnectorCount;
                var visited = new System.Collections.Generic.HashSet<uint>();
                for (int i = 0; i < connectorCount; i++)
                {
                    var connector = topology.GetConnector((uint)i);
                    if (connector == null) continue;
                    
                    var part = connector.Part;
                    if (part != null)
                    {
                        DisableAgcInPartRecursive(part, visited);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AUDIO DIAG] Could not disable hardware AGC: {ex.Message}");
            }
        }

        private void DisableAgcInPartRecursive(Part part, System.Collections.Generic.HashSet<uint> visited)
        {
            if (part == null) return;
            
            uint id = part.LocalId;
            if (visited.Contains(id)) return;
            visited.Add(id);
            
            Guid subType = part.GetSubType;
            Guid ksnodetypeAgc = new Guid("95663730-11FA-11D3-BA9B-00104B12A27F");
            if (subType == ksnodetypeAgc)
            {
                try
                {
                    var field = typeof(Part).GetField("partInterface", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var rawPart = field?.GetValue(part);
                    if (rawPart is ICustomPart customPart)
                    {
                        Guid iidAgc = new Guid("85401FD4-6DE4-4b9d-9869-2D6753A82F3C");
                        int hr = customPart.Activate(1, ref iidAgc, out var obj);
                        if (hr == 0 && obj is ICustomAudioAutoGainControl agc)
                        {
                            var context = Guid.Empty;
                            agc.SetEnabled(false, ref context);
                            Console.WriteLine("[AUDIO] Hardware AGC has been programmatically disabled.");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AUDIO DIAG] Error activating ICustomAudioAutoGainControl: {ex.Message}");
                }
            }
            
            // Recurse incoming parts
            try
            {
                var incomingList = part.PartsIncoming;
                if (incomingList != null)
                {
                    int count = (int)incomingList.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var incomingPart = incomingList[(uint)i];
                        if (incomingPart != null)
                        {
                            DisableAgcInPartRecursive(incomingPart, visited);
                        }
                    }
                }
            }
            catch
            {
                // Fallback / ignore
            }

            // Recurse outgoing parts
            try
            {
                var outgoingList = part.PartsOutgoing;
                if (outgoingList != null)
                {
                    int count = (int)outgoingList.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var outgoingPart = outgoingList[(uint)i];
                        if (outgoingPart != null)
                        {
                            DisableAgcInPartRecursive(outgoingPart, visited);
                        }
                    }
                }
            }
            catch
            {
                // Fallback / ignore
            }
        }

        private async Task ProcessAudioQueueLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_audioQueue.TryDequeue(out var item))
                {
                    try
                    {
                        // Simulated whisper inference latency
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        
                        // Reusing TranscriptionSegment to avoid triggering GC allocations
                        _reusableSegment.Text = "This is a simulated real-time transcribed segment of meeting conversation.";
                        _reusableSegment.Start = TimeSpan.FromSeconds(0);
                        _reusableSegment.End = TimeSpan.FromSeconds(3);
                        _reusableSegment.Confidence = 0.95;
                        _reusableSegment.Timestamp = DateTime.UtcNow;
                        
                        if (_reusableEventArgs != null)
                        {
                            OnSegmentTranscribed(_reusableEventArgs);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AUDIO ERROR] Error in transcription processing loop: {ex.Message}");
                    }
                    finally
                    {
                        // Safely return rented buffer to array pool
                        ArrayPool<float>.Shared.Return(item.Data);
                    }
                }
                else
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task AudioWatchdogLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                    
                    if (_isCapturing && (DateTime.UtcNow - _lastAudioActivityTime).TotalSeconds > 10)
                    {
                        Console.WriteLine("[WATCHDOG] No audio activity detected for more than 10 seconds. Auto-restarting capture device...");
                        RestartCapture();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful cancellation exit
            }
        }

        protected virtual void OnSegmentTranscribed(TranscriptionSegmentEventArgs e)
        {
            SegmentTranscribed?.Invoke(this, e);
        }

        public void Dispose()
        {
            StopCapture();
            
            _processingCts?.Cancel();
            try
            {
                _processingTask?.GetAwaiter().GetResult();
            }
            catch { }
            _processingCts?.Dispose();
            
            // Clear remaining queue items and return buffers to pool
            while (_audioQueue.TryDequeue(out var item))
            {
                ArrayPool<float>.Shared.Return(item.Data);
            }
            
            GC.SuppressFinalize(this);
        }
    }

    [ComImport]
    [Guid("AE2DE0E7-046A-47B2-9F81-F09C1340D83A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICustomPart
    {
        [PreserveSig]
        int GetName(out IntPtr ppwstrName);
        
        [PreserveSig]
        int GetLocalId(out uint pnId);
        
        [PreserveSig]
        int GetGlobalId(out IntPtr ppwstrGlobalId);
        
        [PreserveSig]
        int GetPartType(out int pPartType);
        
        [PreserveSig]
        int GetSubType(out Guid pSubType);
        
        [PreserveSig]
        int GetControlInterfaceCount(out uint pCount);
        
        [PreserveSig]
        int GetControlInterface(uint nIndex, out IntPtr ppInterface);
        
        [PreserveSig]
        int Activate(uint dwClsContext, ref Guid refiid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport]
    [Guid("85401FD4-6DE4-4b9d-9869-2D6753A82F3C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICustomAudioAutoGainControl
    {
        [PreserveSig]
        int GetEnabled(out bool pfEnabled);
        
        [PreserveSig]
        int SetEnabled(bool fEnabled, ref Guid pguidEventContext);
    }
}
