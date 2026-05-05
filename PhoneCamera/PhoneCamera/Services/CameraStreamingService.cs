using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace PhoneCamera.Services;

/// <summary>
/// TCP-клиент для отправки JPEG-кадров серверу CameraReceiver.
/// Протокол: FRAME_START (4 байт) + размер (4 байт LE) + JPEG + FRAME_END (4 байт).
///
/// Улучшения:
///  • Отправка асинхронна — поток CameraX-анализатора не блокируется.
///  • Drop-oldest: если очередь занята, старый кадр вытесняется, всегда отправляется свежий.
///  • Нет аллокаций в горячем пути: весь пакет (header+jpeg+footer) собирается
///    в рентованном ArrayPool-буфере и возвращается пулу после отправки.
///  • Один WriteAsync вместо четырёх Write — один системный вызов.
///  • Flush не нужен: NoDelay=true отключает алгоритм Нейгла.
/// </summary>
public sealed class CameraStreamingService : IDisposable
{
    // Протокол: фиксированные маркеры кадра
    private const byte S0 = 0x01, S1 = 0x02, S2 = 0x03, S3 = 0x04;
    private const byte E0 = 0x04, E1 = 0x03, E2 = 0x02, E3 = 0x01;
    private const int  FrameOverhead = 12; // 4 start + 4 size + 4 end

    private TcpClient?     _client;
    private NetworkStream? _stream;
    private Channel<(byte[] Packet, int Length)>? _channel;
    private Task?          _sendTask;
    private CancellationTokenSource? _sendCts;
    private bool _disposed;

    public bool    IsConnected => _client?.Connected == true && _stream != null;
    public string? ServerIp    { get; private set; }
    public int     ServerPort  { get; private set; }

    public async Task<bool> ConnectAsync(string ip, int port)
    {
        Disconnect();
        try
        {
            _client = new TcpClient { NoDelay = true };
            _client.Client.SendBufferSize = 2 * 1024 * 1024;
            await _client.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromSeconds(5));
            _stream    = _client.GetStream();
            ServerIp   = ip;
            ServerPort = port;

            _sendCts = new CancellationTokenSource();
            // capacity=1: не более одного кадра в ожидании (+ один в отправке).
            // DropWrite позволяет TryWrite возвращать false — DropOldest реализуем вручную,
            // чтобы корректно вернуть рентованный буфер в ArrayPool при вытеснении.
            _channel = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(1)
            {
                FullMode                     = BoundedChannelFullMode.DropWrite,
                SingleReader                 = true,
                SingleWriter                 = true,
                AllowSynchronousContinuations = false,
            });
            _sendTask = Task.Run(() => SendLoopAsync(_sendCts.Token));

            Debug.WriteLine($"[PCam][Stream] Connected to {ip}:{port}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PCam][Stream] Connect failed: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        var ch = _channel;
        _channel = null;
        ch?.Writer.TryComplete();

        _sendCts?.Cancel();
        try { _sendTask?.Wait(1000); } catch { }
        _sendCts?.Dispose();
        _sendCts  = null;
        _sendTask = null;

        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        Debug.WriteLine("[PCam][Stream] Disconnected");
    }

    /// <summary>
    /// Ставит кадр в очередь отправки. Вызывается из потока CameraX-анализатора.
    /// Никогда не блокирует: если очередь занята, старый кадр вытесняется (drop-oldest),
    /// его буфер сразу возвращается в ArrayPool.
    /// buf[0..len] — валидные JPEG-байты (может быть внутренним буфером MemoryStream).
    /// </summary>
    public void EnqueueFrame(byte[] buf, int len)
    {
        var ch = _channel;
        if (ch == null) return;

        // Собираем полный пакет в одном рентованном буфере:
        // [0..3]   = FRAME_START
        // [4..7]   = jpeg length, little-endian
        // [8..8+len-1] = JPEG bytes
        // [8+len..11+len] = FRAME_END
        int    packetLen = FrameOverhead + len;
        byte[] packet    = ArrayPool<byte>.Shared.Rent(packetLen);

        packet[0] = S0; packet[1] = S1; packet[2] = S2; packet[3] = S3;
        packet[4] = (byte) len;
        packet[5] = (byte)(len >>  8);
        packet[6] = (byte)(len >> 16);
        packet[7] = (byte)(len >> 24);
        Buffer.BlockCopy(buf, 0, packet, 8, len);
        packet[8 + len]     = E0;
        packet[8 + len + 1] = E1;
        packet[8 + len + 2] = E2;
        packet[8 + len + 3] = E3;

        // Drop-oldest вручную: если TryWrite вернул false (канал полон),
        // вычитываем старый кадр, возвращаем его буфер в пул и пишем новый.
        if (!ch.Writer.TryWrite((packet, packetLen)))
        {
            if (ch.Reader.TryRead(out var old))
                ArrayPool<byte>.Shared.Return(old.Packet);

            if (!ch.Writer.TryWrite((packet, packetLen)))
                ArrayPool<byte>.Shared.Return(packet); // канал завершён — выбрасываем
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var stream = _stream;
        var ch     = _channel;
        if (stream == null || ch == null) return;

        try
        {
            await foreach (var (packet, len) in ch.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // Один WriteAsync = один системный вызов; NoDelay=true — без буферизации
                    await stream.WriteAsync(packet.AsMemory(0, len), ct);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PCam][Stream] Send error: {ex.Message}");
                    ArrayPool<byte>.Shared.Return(packet);
                    Disconnect();
                    return;
                }
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PCam][Stream] SendLoop error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}

