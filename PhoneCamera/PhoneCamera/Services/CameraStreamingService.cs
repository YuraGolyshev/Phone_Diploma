using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
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

    // Период замера заполненности Channel (backpressure stats).
    // Читается из Preferences (ключ "cs_adaptive_window_ms"), который пишет UI
    // SettingsPage → CameraSettings.AdaptiveWindowMs. Дефолт 2000 ms совпадает
    // с CameraSettings.DEF_AdaptiveWindowMs. Захватывается значение на момент
    // ConnectAsync — менять окно «в полёте» нет смысла, оно становится актуальным
    // при следующем подключении.
    private const int  DefaultBackpressureWindowMs = 2000;
    private static int CurrentBackpressureWindowMs()
    {
        try { return Microsoft.Maui.Storage.Preferences.Get("cs_adaptive_window_ms", DefaultBackpressureWindowMs); }
        catch { return DefaultBackpressureWindowMs; }
    }

    private TcpClient?     _client;
    private NetworkStream? _stream;
    private Channel<(byte[] Packet, int Length)>? _channel;
    private Task?          _sendTask;
    private CancellationTokenSource? _sendCts;
    private bool _disposed;
    // true для любого видеокодека с inter-frame зависимостями (H.264, H.265).
    // Потеря NAL → мусор у декодера до следующего IDR (~секунда). Поэтому drop-oldest
    // запрещён, очередь больше + FullMode=Wait → natural backpressure через MediaCodec
    // к Camera2 sensor'у. JPEG (самодостаточные кадры) — drop-oldest безопасен.
    private bool _useBackpressure;

    // ── Адаптивная подстройка битрейта: статистика по заполненности Channel ──
    // Каждый EnqueueFrame в backpressure-режиме инкрементит _writeTotal, а если
    // TryWrite вернул false (канал занят, сеть отстаёт) — ещё и _writeBlocked.
    // Раз в ADAPTIVE_WINDOW_MS тик-таймер собирает счётчики, сбрасывает их в 0
    // и зовёт OnBackpressureWindow(blocked, total). Подписчик (H.264 pipeline)
    // решает поднимать или опускать битрейт.
    private long _writeTotal;
    private long _writeBlocked;
    private System.Threading.Timer? _bpTimer;
    /// <summary>Тиковая статистика блокировок отправки. Аргументы: (blocked, total)
    /// за прошедшее окно. Подписывается в MainPage → CameraPreviewView → handler →
    /// H264EncoderPipeline.ObserveBackpressure. Фиксированный intervalMs задаётся
    /// в CameraSettings.ADAPTIVE_WINDOW_MS, по умолчанию 2000 ms.</summary>
    public System.Action<int, int>? OnBackpressureWindow;

    public bool    IsConnected => _client?.Connected == true && _stream != null;
    public string? ServerIp    { get; private set; }
    public int     ServerPort  { get; private set; }

    /// <summary>
    /// Подключается и отправляет 1 байт handshake:
    /// 0x01 = JPEG legacy (совпадает с первым байтом FRAME_START_MARKER → совместимо
    ///        со старым сервером без диспетчера);
    /// 0x02 = H.264 Annex-B chunks (серверный диспетчер выберет H.264-ветку);
    /// 0x03 = H.265 (HEVC) Annex-B chunks (серверный диспетчер выберет H.265-ветку).
    /// Любой ненулевой не-0x01 байт включает backpressure-режим очереди.
    /// </summary>
    public async Task<bool> ConnectAsync(string ip, int port, byte handshakeByte = 0x01)
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

            // Handshake: один байт сразу после connect.
            // ВАЖНО: для JPEG (0x01) handshake НЕ шлём — этот байт совпадает с первым
            // байтом FRAME_START_MARKER (0x01,0x02,0x03,0x04), и сервер именно так его
            // и ожидает (читает первый байт стрима, узнаёт что это начало маркера, и
            // через PrefixedStream возвращает обратно в pipeline JPEG-ветки).
            // Если бы мы отправили лишний 0x01 — сервер увидел бы [01, 01, 02, 03, 04, …]
            // и не нашёл бы маркер.
            // Для H.264 (0x02) / H.265 (0x03) handshake обязателен — он отделяет нужную ветку.
            if (handshakeByte != 0x01)
                await _stream.WriteAsync(new[] { handshakeByte }, 0, 1).ConfigureAwait(false);

            _useBackpressure = (handshakeByte != 0x01);

            _sendCts = new CancellationTokenSource();
            // Разная стратегия по формату:
            //   JPEG          — кадр самодостаточен, потеря не ломает декодер. Capacity=1 +
            //                   drop-oldest (вытесняем старый кадр свежим), сеть никогда
            //                   не отстаёт от текущего момента.
            //   H.264 / H.265 — chunks являются последовательностью NAL-юнитов, потеря любого
            //                   => мусор на стороне декодера до следующего IDR (~секунда).
            //                   Capacity=8 + FullMode=Wait: при отставании сети encoder сам
            //                   заблокируется на EnqueueRawBytes → MediaCodec упрётся в свой
            //                   output-queue → Camera2 sensor сбавит темп. Естественный
            //                   backpressure через всю цепочку, без потерь.
            int capacity = _useBackpressure ? 8 : 1;
            var fullMode = _useBackpressure
                ? BoundedChannelFullMode.Wait
                : BoundedChannelFullMode.DropWrite;
            _channel = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(capacity)
            {
                FullMode                     = fullMode,
                SingleReader                 = true,
                SingleWriter                 = true,
                AllowSynchronousContinuations = false,
            });
            _sendTask = Task.Run(() => SendLoopAsync(_sendCts.Token));

            // Таймер замеров backpressure: только в H.264/H.265 режиме (для JPEG
            // drop-oldest нет смысла адаптировать битрейт). Tick'ает каждые
            // BackpressureWindowMs, читает счётчики, зовёт OnBackpressureWindow.
            if (_useBackpressure)
            {
                _writeTotal = 0;
                _writeBlocked = 0;
                int windowMs = CurrentBackpressureWindowMs();
                _bpTimer = new System.Threading.Timer(BpTimerTick, null, windowMs, windowMs);
            }

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

        try { _bpTimer?.Dispose(); } catch { }
        _bpTimer = null;

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

    private void BpTimerTick(object? _)
    {
        int total   = (int)Interlocked.Exchange(ref _writeTotal,   0L);
        int blocked = (int)Interlocked.Exchange(ref _writeBlocked, 0L);
        if (total <= 0) return;
        double pct = 100.0 * blocked / total;
        Debug.WriteLine($"[PCam][Stream] BW window: {total} frames, blocked={blocked} ({pct:F1}%)");
        try { OnBackpressureWindow?.Invoke(blocked, total); } catch { }
    }

    /// <summary>
    /// Ставит произвольные байты (например, H.264 NAL-чанк) в очередь отправки в том же
    /// фрейм-формате что и JPEG: FRAME_START + size(4) + payload + FRAME_END. Сервер
    /// в H.264-режиме использует ровно тот же фреймер, payload — Annex-B chunk.
    /// Данные копируются в рентованный буфер, оригинальный buf можно сразу переиспользовать.
    /// </summary>
    public void EnqueueRawBytes(byte[] buf, int len) => EnqueueFrame(buf, len);

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

        if (_useBackpressure)
        {
            // H.264 / H.265: НЕ дропаем — потеря NAL-юнита ломает поток до следующего IDR.
            // FullMode=Wait, capacity=8 → нормальный backpressure до MediaCodec.
            // ВАЖНО: с таймаутом 500ms. Без него output-thread может застрять навсегда
            // на большом 4K-кадре + slow Wi-Fi, и при Stop приложение упадёт
            // (NRE через JNI после release encoder'а). С таймаутом: если сеть совсем
            // встала на 500ms — drop'аем NAL chunk и продолжаем, output-thread
            // может корректно выйти на Stop.
            const int writeTimeoutMs = 500;
            Interlocked.Increment(ref _writeTotal);
            try
            {
                if (!ch.Writer.TryWrite((packet, packetLen)))
                {
                    // Канал занят — сеть не успевает. Это сигнал для AIMD-контроллера
                    // снизить битрейт. Сам пакет всё равно пытаемся протолкнуть
                    // (с таймаутом) — Wait-режим даёт честный backpressure.
                    Interlocked.Increment(ref _writeBlocked);
                    using var ctsTimeout = new CancellationTokenSource(writeTimeoutMs);
                    ch.Writer.WriteAsync((packet, packetLen), ctsTimeout.Token).AsTask().Wait();
                }
            }
            catch
            {
                // OperationCanceled / ChannelClosed — возвращаем буфер в пул.
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        else
        {
            // JPEG: drop-oldest вручную — если TryWrite вернул false (канал полон),
            // вычитываем старый кадр, возвращаем его буфер в пул и пишем новый.
            // Кадры самодостаточны — потеря не ломает декодер.
            if (!ch.Writer.TryWrite((packet, packetLen)))
            {
                if (ch.Reader.TryRead(out var old))
                    ArrayPool<byte>.Shared.Return(old.Packet);

                if (!ch.Writer.TryWrite((packet, packetLen)))
                    ArrayPool<byte>.Shared.Return(packet); // канал завершён — выбрасываем
            }
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

