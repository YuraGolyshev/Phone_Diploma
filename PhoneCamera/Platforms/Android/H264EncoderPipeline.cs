using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Java.Lang;
using AOS = Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace PhoneCamera.Platforms.Android;

/// <summary>
/// Camera2 → MediaCodec H.264 encoder pipeline.
///
/// Архитектура (zero-copy, GPU path):
///   Camera2 sensor → encoder InputSurface → MediaCodec H.264 hardware encoder
///                                        → output ByteBuffer (Annex-B NAL units)
///                                        → callback OnNalChunk(bytes, len, isKeyFrame)
///
/// Главное преимущество перед YUV/ImageReader: HAL не ограничен 30fps на этом пути,
/// потому что данные идут от sensor'а в encoder напрямую, без копирования в DRAM.
/// На 1080p@60fps типовой Android device держит честные 60fps.
///
/// Класс изолирован от UI и CameraX. Public API минимален: Start/Stop + 2 callback'а.
/// Не привязан к конкретному handler'у — работает на собственных HandlerThread'ах.
/// </summary>
public sealed class H264EncoderPipeline : Java.Lang.Object
{
    // Все настройки (3A, EIS, image-processing, шаблон CaptureRequest, ручные
    // значения сенсора, H.264 I-frame interval) вынесены в общий статический
    // класс CameraSettings — он делится между этим pipeline'ом и JPEG-веткой
    // (CameraPreviewHandler). Тыкать настройки → CameraSettings.cs.

    // Callback вызывается на отдельном output thread; не блокировать!
    public System.Action<byte[], int, bool>? OnNalChunk;
    public System.Action<string>?            OnError;

    // Превью-кадр в виде ARGB Bitmap (~10 fps). Вызывается из preview-thread,
    // получатель должен Post() в UI thread сам. Bitmap собственность вызывающего —
    // потребитель должен .Recycle() после использования (или передавать в другой
    // bitmap pool).
    public System.Action<Bitmap>?            OnPreviewBitmap;

    // Когда false — OnPreviewBitmap не будет вызываться и превью-reader не создаётся
    private volatile bool                    _previewEnabled = true;

    // Кодек: false = H.264 (AVC, по умолчанию), true = H.265 (HEVC).
    // Меняется ДО Start() — внутри Start() читается один раз для ConfigureEncoder.
    private volatile bool                    _useHevc;

    public bool IsRunning => _running;

    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set => _previewEnabled = value;
    }

    /// <summary>true → MediaCodec открывается на video/hevc вместо video/avc.
    /// NAL-стрим всё равно идёт в Annex-B, фреймер CameraStreamingService не различает.</summary>
    public bool UseHevc
    {
        get => _useHevc;
        set => _useHevc = value;
    }

    // Размер preview-стрима. Full HD 1920×1080 — для красивой картинки в UI.
    // Внимание: на ряде HAL комбинация PRIV (encoder) + YUV (preview) при таких
    // размерах ограничивает fps до 30 для обоих стримов (известный 30fps cap).
    // На Pixel/iPhone обычно работает 60. CPU нагрузка на YUV→ARGB ~20-30 ms на
    // 1080p — за счёт throttle '1 из 2' получаем плавные ~15 fps превью даже на
    // 30fps source, что приятно глазу. Если хочется ещё больше fps — снизить
    // throttle до '1 из 1' (но CPU-bound).
    private const int PREVIEW_W = 960;
    private const int PREVIEW_H = 540;

    private MediaCodec?            _encoder;
    private Surface?               _inputSurface;
    private CameraDevice?          _device;
    private CameraCaptureSession?  _session;
    private HandlerThread?         _cameraThread;
    private global::Android.OS.Handler? _cameraHandler;
    private System.Threading.Thread? _outputThread;
    private ImageReader?           _previewReader; // YUV_420_888 для preview
    private volatile bool          _running;

    private int _w, _h, _fps;
    private string? _cameraId;

    // ── Состояние AIMD-контроллера битрейта ─────────────────────────────────
    // _bitrateCeiling — формульный потолок (то, что выдала ChooseBitrate при
    //   ConfigureEncoder). AIMD может опускать ниже потолка, но не поднимать выше.
    // _currentBitrate — текущее значение, переданное в MediaCodec.
    // _clearWindowsCount — счётчик «чистых» окон подряд для подъёма обратно.
    private int _bitrateCeiling;
    private int _currentBitrate;
    private int _clearWindowsCount;

    // ── Public API ──────────────────────────────────────────────────────────
    public void Start(string cameraId, int width, int height, int fps)
    {
        Stop();

        _cameraId = cameraId;
        _w = width;
        _h = height;
        _fps = fps;
        _running = true;

        try
        {
            _cameraThread = new HandlerThread("PCamH264-Cam");
            _cameraThread.Start();
            _cameraHandler = new global::Android.OS.Handler(_cameraThread.Looper!);

            // Preview surface: YUV_420_888 ImageReader, маленький (640×480),
            // listener получает изображения и шлёт ARGB-Bitmap в OnPreviewBitmap.
            // На многих HAL добавление этого surface рядом с encoder (PRIV+YUV)
            // может ограничить fps до 30. Если это критично — закомментируй
            // блок ниже, и будет работать как раньше, без превью.
            if (_previewEnabled)
            {
                try
                {
                    _previewReader = ImageReader.NewInstance(
                        PREVIEW_W, PREVIEW_H,
                        global::Android.Graphics.ImageFormatType.Yuv420888, 2);
                    _previewReader.SetOnImageAvailableListener(
                        new PreviewListener(this), _cameraHandler);
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][H264] Preview reader init failed: {ex.Message} (продолжаем без превью)");
                    _previewReader = null;
                }
            }

            ConfigureEncoder(width, height, fps);

            _outputThread = new System.Threading.Thread(OutputLoop) { IsBackground = true, Name = "PCamH264-Out" };
            _outputThread.Start();

            var ctx = global::Android.App.Application.Context;
            var cm  = (CameraManager)ctx.GetSystemService(global::Android.Content.Context.CameraService)!;
            cm.OpenCamera(cameraId, new DeviceCb(this), _cameraHandler);

            System.Diagnostics.Debug.WriteLine(
                $"[PCam][H264] Start requested: {width}x{height}@{fps}fps");
        }
        catch (System.Exception ex)
        {
            ReportError($"Start failed: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        _running = false;

        try { _session?.StopRepeating(); } catch { }
        try { _session?.Close();         } catch { }
        _session = null;

        try { _device?.Close(); } catch { }
        _device = null;

        // SignalEndOfInputStream завершает encoder корректно (нужно перед Stop()).
        try { _encoder?.SignalEndOfInputStream(); } catch { }

        // Ждём выход output-thread. На 4K он может быть в WriteAsync.Wait() в Channel
        // CameraStreamingService — там стоит таймаут 500ms, плюс DequeueOutputBuffer'у
        // дать дренировать оставшиеся NAL'ы. Берём 2000ms с запасом, чтобы Stop был
        // надёжный без шанса на NRE через JNI после release encoder'а.
        try { _outputThread?.Join(2000); } catch { }
        _outputThread = null;

        try { _encoder?.Stop();    } catch { }
        try { _encoder?.Release(); } catch { }
        _encoder = null;

        try { _inputSurface?.Release(); } catch { }
        _inputSurface = null;

        try { _previewReader?.Close(); } catch { }
        _previewReader = null;

        try { _cameraThread?.QuitSafely(); } catch { }
        _cameraThread  = null;
        _cameraHandler = null;
    }

    // ── Encoder setup ───────────────────────────────────────────────────────
    private void ConfigureEncoder(int w, int h, int fps)
    {
        // Выбор MIME и профиля по флагу _useHevc. NAL-формат на выходе один и тот же
        // (Annex-B chunks через MediaCodec output buffers), отличается только
        // содержимое внутри chunk'ов — AVC NAL units vs HEVC NAL units. Серверная
        // ветка различает по handshake-байту в TCP, а не по контенту.
        string mime = _useHevc ? MediaFormat.MimetypeVideoHevc! : MediaFormat.MimetypeVideoAvc!;
        int    profile = _useHevc
            ? (int)MediaCodecProfileType.Hevcprofilemain
            : (int)MediaCodecProfileType.Avcprofilebaseline;
        string codecLabel = _useHevc ? "HEVC" : "AVC";

        _encoder = MediaCodec.CreateEncoderByType(mime)
                   ?? throw new System.InvalidOperationException($"{codecLabel} encoder not available");

        // Считаем формульный потолок один раз и запоминаем — AIMD будет с ним
        // сравнивать при попытках поднять битрейт обратно.
        _bitrateCeiling   = ChooseBitrate(w, h, fps);
        _currentBitrate   = _bitrateCeiling;
        _clearWindowsCount = 0;

        var fmt = MediaFormat.CreateVideoFormat(mime, w, h)!;
        // COLOR_FormatSurface = 0x7F000789 (= 2130708361). Используем числовую константу,
        // т.к. enum Xamarin может различаться по именам между релизами.
        fmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        fmt.SetInteger(MediaFormat.KeyBitRate,        _bitrateCeiling);
        fmt.SetInteger(MediaFormat.KeyFrameRate,      fps);
        fmt.SetInteger(MediaFormat.KeyIFrameInterval, CameraSettings.H264_I_FRAME_INTERVAL_SECONDS);
        fmt.SetInteger(MediaFormat.KeyProfile,        profile);

        // Hint'ы на low-latency и 60fps operating rate (новые API)
        if (AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.Q)
        {
            try { fmt.SetInteger(MediaFormat.KeyLowLatency, 1); } catch { }
        }
        if (AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.M)
        {
            try { fmt.SetInteger(MediaFormat.KeyOperatingRate, fps); } catch { }
        }
        // Префиксить SPS/PPS перед каждым IDR — декодер сможет начать с любого keyframe (API 25+)
        if (AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.NMr1)
        {
            try { fmt.SetInteger("prepend-sps-pps-to-idr-frames", 1); } catch { }
        }

        _encoder.Configure(fmt, null, null, MediaCodecConfigFlags.Encode);
        _inputSurface = _encoder.CreateInputSurface();
        _encoder.Start();

        System.Diagnostics.Debug.WriteLine(
            $"[PCam][H264] Encoder configured: codec={codecLabel} ({mime}) {w}x{h}@{fps}fps " +
            $"bitrate={_bitrateCeiling / 1e6:F2} Mbps (ceiling, adaptive={CameraSettings.ADAPTIVE_BITRATE_ENABLED})");
    }

    // Формульный «потолок качества» для разрешения/fps. Берём константы из
    // CameraSettings — туда же ходить если хочется подкрутить.
    // AIMD-контроллер ниже стартует с этого значения и может его опускать
    // (если сеть не тянет) и обратно поднимать, но НЕ выше этого потолка.
    private int ChooseBitrate(int w, int h, int fps)
    {
        double bpp = _useHevc ? CameraSettings.H265_BITS_PER_PIXEL
                              : CameraSettings.H264_BITS_PER_PIXEL;
        long pixels = (long)w * h * fps;
        long bps = (long)(pixels * bpp);
        if (bps < CameraSettings.BITRATE_MIN_BPS) bps = CameraSettings.BITRATE_MIN_BPS;
        if (bps > CameraSettings.BITRATE_CAP_BPS) bps = CameraSettings.BITRATE_CAP_BPS;
        return (int)bps;
    }

    // Меняет битрейт энкодера на лету через MediaCodec.SetParameters
    // (PARAMETER_KEY_VIDEO_BITRATE = "video-bitrate"). Поддерживается с API 19.
    // Сессия НЕ перезапускается — encoder подхватит следующий же кадр с новым
    // bitrate (через 1-2 фрейма для VBR-балансировки).
    private void SetTargetBitrate(int bps)
    {
        var enc = _encoder;
        if (enc == null) return;
        if (bps == _currentBitrate) return;
        try
        {
            var bundle = new global::Android.OS.Bundle();
            bundle.PutInt("video-bitrate", bps);
            enc.SetParameters(bundle);
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][H264] Bitrate adjust: {_currentBitrate / 1e6:F2} → {bps / 1e6:F2} Mbps " +
                $"(ceiling {_bitrateCeiling / 1e6:F2} Mbps)");
            _currentBitrate = bps;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][H264] SetTargetBitrate failed: {ex.Message}");
        }
    }

    /// <summary>
    /// AIMD-контроллер: получает раз в окно статистику blocked/total от
    /// CameraStreamingService. Решает менять ли битрейт.
    ///   blocked/total > BACKPRESSURE_THRESHOLD →  битрейт × DECREASE_FACTOR
    ///   blocked/total < CLEAR_THRESHOLD (N окон подряд) → × INCREASE_FACTOR
    ///   не выше _bitrateCeiling, не ниже BITRATE_MIN_BPS.
    /// </summary>
    public void ObserveBackpressure(int blocked, int total)
    {
        if (!CameraSettings.ADAPTIVE_BITRATE_ENABLED) return;
        if (!_running) return;
        if (total <= 0) return;

        double rate = (double)blocked / total;

        if (rate >= CameraSettings.ADAPTIVE_BACKPRESSURE_THRESHOLD)
        {
            // Затор — быстро вниз.
            _clearWindowsCount = 0;
            int newBps = (int)(_currentBitrate * CameraSettings.ADAPTIVE_DECREASE_FACTOR);
            if (newBps < CameraSettings.BITRATE_MIN_BPS) newBps = CameraSettings.BITRATE_MIN_BPS;
            if (newBps != _currentBitrate)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] AIMD ↓ backpressure {rate * 100:F1}% ({blocked}/{total})");
                SetTargetBitrate(newBps);
            }
        }
        else if (rate < CameraSettings.ADAPTIVE_CLEAR_THRESHOLD)
        {
            // Чисто — копим окна, потом аккуратно вверх.
            _clearWindowsCount++;
            if (_clearWindowsCount >= CameraSettings.ADAPTIVE_CLEAR_WINDOWS_TO_RAISE
                && _currentBitrate < _bitrateCeiling)
            {
                _clearWindowsCount = 0;
                int newBps = (int)(_currentBitrate * CameraSettings.ADAPTIVE_INCREASE_FACTOR);
                if (newBps > _bitrateCeiling) newBps = _bitrateCeiling;
                if (newBps != _currentBitrate)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][H264] AIMD ↑ clear {CameraSettings.ADAPTIVE_CLEAR_WINDOWS_TO_RAISE} windows " +
                        $"(last {rate * 100:F2}%, {blocked}/{total})");
                    SetTargetBitrate(newBps);
                }
            }
        }
        else
        {
            // Серединка — не вниз, не вверх. Серию «чистых» сбрасываем,
            // чтобы не подняться раньше времени.
            _clearWindowsCount = 0;
        }
    }

    // ── Output loop (отдельный thread) ──────────────────────────────────────
    private void OutputLoop()
    {
        var info = new MediaCodec.BufferInfo();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastLogMs = 0;
        int chunks = 0, keys = 0;
        long totalBytes = 0;
        try
        {
            while (_running)
            {
                int idx;
                try { idx = _encoder!.DequeueOutputBuffer(info, 10_000L); } // 10 ms
                catch { break; }

                if (idx == (int)MediaCodecInfoState.TryAgainLater) continue;
                if (idx == (int)MediaCodecInfoState.OutputFormatChanged) continue;
                if (idx == (int)MediaCodecInfoState.OutputBuffersChanged) continue;
                if (idx < 0) continue;

                if (info.Size > 0)
                {
                    var buf = _encoder!.GetOutputBuffer(idx);
                    if (buf != null)
                    {
                        buf.Position(info.Offset);
                        buf.Limit(info.Offset + info.Size);

                        byte[] data = new byte[info.Size];
                        buf.Get(data);

                        bool isKey = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                        chunks++;
                        if (isKey) keys++;
                        totalBytes += info.Size;

                        try { OnNalChunk?.Invoke(data, info.Size, isKey); }
                        catch (System.Exception ex) { ReportError($"OnNalChunk threw: {ex.Message}"); }

                        long nowMs = sw.ElapsedMilliseconds;
                        if (nowMs - lastLogMs >= 1000)
                        {
                            double secs = (nowMs - lastLogMs) / 1000.0;
                            double fps  = chunks / secs;
                            double mbps = totalBytes * 8.0 / secs / 1_000_000.0;
                            System.Diagnostics.Debug.WriteLine(
                                $"[PCam][H264] Encoded fps≈{fps:F1}  keys={keys}  bitrate≈{mbps:F2} Mbps");
                            lastLogMs = nowMs;
                            chunks = 0; keys = 0; totalBytes = 0;
                        }
                    }
                }

                try { _encoder!.ReleaseOutputBuffer(idx, false); } catch { }

                if ((info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) break;
            }
        }
        catch (System.Exception ex)
        {
            ReportError($"OutputLoop crash: {ex.Message}");
        }
    }

    private void ReportError(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][H264] ERROR: {msg}");
        try { OnError?.Invoke(msg); } catch { }
    }

    // ── Camera2 callbacks ───────────────────────────────────────────────────
    private sealed class DeviceCb : CameraDevice.StateCallback
    {
        private readonly H264EncoderPipeline _o;
        public DeviceCb(H264EncoderPipeline o) { _o = o; }

        public override void OnOpened(CameraDevice camera)
        {
            _o._device = camera;
            try
            {
                var surfaces = new System.Collections.Generic.List<Surface> { _o._inputSurface! };
                if (_o._previewReader?.Surface != null)
                    surfaces.Add(_o._previewReader.Surface);

                // На Android 13+ пробуем новый SessionConfiguration API с
                // STREAM_USE_CASE_VIDEO_RECORD. Это даёт HAL явный hint «эта сессия
                // для записи видео» — на ряде устройств (особенно где обычная session
                // лочит 30fps) HAL переключается в video-recording sensor mode и
                // разблокирует 60fps. Если устройство не поддерживает — fallback на
                // legacy createCaptureSession.
                bool used = false;
                if (AOS.Build.VERSION.SdkInt >= AOS.BuildVersionCodes.Tiramisu)
                {
                    try
                    {
                        used = TryCreateSessionWithVideoUseCase(camera);
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PCam][H264] STREAM_USE_CASE_VIDEO_RECORD failed: {ex.Message} → fallback to legacy");
                    }
                }
                if (!used)
                {
                    System.Diagnostics.Debug.WriteLine("[PCam][H264] Using legacy CreateCaptureSession (no STREAM_USE_CASE)");
                    camera.CreateCaptureSession(surfaces, new SessionCb(_o), _o._cameraHandler);
                }
            }
            catch (System.Exception ex) { _o.ReportError($"CreateCaptureSession: {ex.Message}"); }
        }

        // Android 13+ путь через SessionConfiguration + OutputConfiguration.setStreamUseCase().
        // Возвращает true если сессия создана этим путём (callback ещё не отработал — придёт
        // асинхронно). Бросает если устройство не поддерживает stream use case → внешний catch
        // переключится на legacy.
        private bool TryCreateSessionWithVideoUseCase(CameraDevice camera)
        {
            // Проверяем характеристику устройства: объявляет ли HAL поддержку stream use cases.
            try
            {
                var ctx = global::Android.App.Application.Context;
                var cm  = (CameraManager)ctx.GetSystemService(global::Android.Content.Context.CameraService)!;
                var chars = cm.GetCameraCharacteristics(camera.Id!);
                var key = CameraCharacteristics.ScalerAvailableStreamUseCases;
                var supported = chars.Get(key);
                if (supported == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[PCam][H264] Device does NOT advertise SCALER_AVAILABLE_STREAM_USE_CASES → using legacy session");
                    return false;
                }

                // Логируем содержимое (long[] — примитивный массив, читаем через JNI helper).
                // Если что-то не так с чтением — просто пропускаем диагностику, само наличие
                // ненулевого supported уже подтверждает поддержку.
                try
                {
                    int len = JNIEnv.GetArrayLength(supported.Handle);
                    long[] arr = new long[len];
                    JNIEnv.CopyArray(supported.Handle, arr);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < arr.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append("0x").Append(arr[i].ToString("X"));
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][H264] Device supports stream use cases: [{sb}] (length={len})");
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][H264] (diagnostic only) couldn't enumerate stream use cases: {ex.Message}");
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] Probe SCALER_AVAILABLE_STREAM_USE_CASES failed: {ex.Message}");
                return false;
            }

            // OutputConfiguration: encoder = VIDEO_RECORD, preview = PREVIEW.
            const long STREAM_USE_CASE_PREVIEW      = 0x1L;
            const long STREAM_USE_CASE_VIDEO_RECORD = 0x3L;

            var encCfg = new global::Android.Hardware.Camera2.Params.OutputConfiguration(_o._inputSurface!);
            try { encCfg.StreamUseCase = STREAM_USE_CASE_VIDEO_RECORD; }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][H264] StreamUseCase encoder failed: {ex.Message}");
                return false;
            }

            var configs = new System.Collections.Generic.List<global::Android.Hardware.Camera2.Params.OutputConfiguration> { encCfg };

            if (_o._previewReader?.Surface != null)
            {
                var prevCfg = new global::Android.Hardware.Camera2.Params.OutputConfiguration(_o._previewReader.Surface);
                try { prevCfg.StreamUseCase = STREAM_USE_CASE_PREVIEW; } catch { /* не критично */ }
                configs.Add(prevCfg);
            }

            // SessionConfiguration требует executor. Используем простой адаптер на наш Handler,
            // чтобы callback'и шли в тот же поток что и старый CreateCaptureSession.
            var executor = new HandlerExecutor(_o._cameraHandler!);
            var sessionCfg = new global::Android.Hardware.Camera2.Params.SessionConfiguration(
                (int)global::Android.Hardware.Camera2.Params.SessionType.Regular,
                configs,
                executor,
                new SessionCb(_o));

            // ── Session parameters ───────────────────────────────────────────────
            // Передаются HAL на этапе configure_streams (до setRepeatingRequest).
            // Ключевое отличие от request-параметров: HAL читает их при ВЫБОРЕ
            // sensor mode. Per-frame значения в repeating request HAL уже использует
            // только для подкрутки экспозиции/гейна внутри выбранного sensor mode.
            // Поэтому ставим ровно тот же набор настроек, что и в repeating request,
            // через общий CameraSettings.ApplyToBuilder — иначе кадры будут отбрасываться
            // (требование Camera2: ключи в session parameters и request должны совпадать).
            try
            {
                var sessionParams = camera.CreateCaptureRequest(CameraSettings.CAPTURE_TEMPLATE);
                CameraSettings.ApplyToBuilder(sessionParams, _o._fps);
                sessionCfg.SessionParameters = sessionParams.Build();
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] Session parameters set (template={CameraSettings.CAPTURE_TEMPLATE}, " +
                    $"3A={(CameraSettings.ENABLE_3A ? "AUTO" : "OFF")}, AE_RANGE=[{_o._fps},{_o._fps}])");
            }
            catch (System.Exception ex)
            {
                // Если HAL не объявляет эти ключи в getAvailableSessionKeys(), set может
                // бросить. Это не фатально — просто продолжим без session parameters.
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] Session parameters set failed (continuing without): {ex.Message}");
            }

            camera.CreateCaptureSession(sessionCfg);
            System.Diagnostics.Debug.WriteLine(
                "[PCam][H264] Capture session requested with STREAM_USE_CASE_VIDEO_RECORD");
            return true;
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            try { camera.Close(); } catch { }
            if (_o._device == camera) _o._device = null;
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            _o.ReportError($"Camera error: {error}");
            try { camera.Close(); } catch { }
        }
    }

    private sealed class SessionCb : CameraCaptureSession.StateCallback
    {
        private readonly H264EncoderPipeline _o;
        public SessionCb(H264EncoderPipeline o) { _o = o; }

        public override void OnConfigured(CameraCaptureSession session)
        {
            if (_o._device == null) { try { session.Close(); } catch { } return; }
            _o._session = session;
            try
            {
                // CaptureRequest строится по шаблону из CameraSettings. Здесь же мы
                // переопределяем значения через CameraSettings.ApplyToBuilder.
                // То же самое дублируется в session parameters (выбор sensor mode
                // при configure_streams).
                var b = _o._device.CreateCaptureRequest(CameraSettings.CAPTURE_TEMPLATE)!;
                b.AddTarget(_o._inputSurface!);
                if (_o._previewReader?.Surface != null)
                    b.AddTarget(_o._previewReader.Surface);

                CameraSettings.ApplyToBuilder(b, _o._fps);

                session.SetRepeatingRequest(b.Build(), new FpsCaptureCb(_o), _o._cameraHandler);
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] Capture session running @ {_o._fps}fps  " +
                    $"(template={CameraSettings.CAPTURE_TEMPLATE}, " +
                    $"3A={(CameraSettings.ENABLE_3A ? "AUTO" : "OFF")}, " +
                    $"EIS={(CameraSettings.ENABLE_VIDEO_STABILIZATION ? "ON" : "OFF")}, " +
                    $"NR={CameraSettings.NOISE_REDUCTION_MODE}/Edge={CameraSettings.EDGE_MODE}/TM={CameraSettings.TONEMAP_MODE})");
            }
            catch (System.Exception ex) { _o.ReportError($"OnConfigured: {ex.Message}"); }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            _o.ReportError("Capture session configure failed");
        }
    }

    // ── ImageReader listener для preview (YUV_420_888 → ARGB Bitmap) ────────
    // Throttle: один из трёх кадров — для UI на телефоне 10 fps хватает с
    // запасом, и снимаем нагрузку с CPU (preview-конверсия ~3-5 ms на 640×480,
    // умножаем на reduced rate).
    private sealed class PreviewListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly H264EncoderPipeline _o;
        public PreviewListener(H264EncoderPipeline o) { _o = o; }

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader == null) return;

            // Без throttle: AcquireLatestImage сам пропустит устаревшие кадры,
            // если YUV→ARGB не успевает за HAL fps. Естественный backpressure.
            global::Android.Media.Image? image = null;
            try
            {
                image = reader.AcquireLatestImage();
                if (image == null) return;

                var bmp = YuvToArgbBitmap(image);
                if (bmp != null)
                {
                    try { _o.OnPreviewBitmap?.Invoke(bmp); }
                    catch (System.Exception ex) { _o.ReportError($"OnPreviewBitmap threw: {ex.Message}"); }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][H264] Preview decode error: {ex.Message}");
            }
            finally
            {
                try { image?.Close(); } catch { }
            }
        }

        // Конверсия YUV_420_888 → ARGB Bitmap. BT.601 формула, без SIMD.
        // Достаточно быстро для 640×480 @10fps (~3-5 ms на кадр).
        private static Bitmap? YuvToArgbBitmap(global::Android.Media.Image img)
        {
            int w = img.Width;
            int h = img.Height;
            var planes = img.GetPlanes();
            if (planes == null || planes.Length < 3) return null;

            var yPlane = planes[0];
            var uPlane = planes[1];
            var vPlane = planes[2];

            int yStride       = yPlane.RowStride;
            int uvStride      = uPlane.RowStride;
            int uvPixelStride = uPlane.PixelStride;

            // Копируем буферы планов в байт-массивы — внутренний цикл по
            // ByteBuffer'у был бы медленнее.
            var yBuf = yPlane.Buffer; var uBuf = uPlane.Buffer; var vBuf = vPlane.Buffer;
            if (yBuf == null || uBuf == null || vBuf == null) return null;

            yBuf.Rewind();
            byte[] yData = new byte[yBuf.Remaining()]; yBuf.Get(yData);
            uBuf.Rewind();
            byte[] uData = new byte[uBuf.Remaining()]; uBuf.Get(uData);
            vBuf.Rewind();
            byte[] vData = new byte[vBuf.Remaining()]; vBuf.Get(vData);

            int[] argb = new int[w * h];
            int idx = 0;

            for (int row = 0; row < h; row++)
            {
                int yLine = row * yStride;
                int uvLine = (row >> 1) * uvStride;
                for (int col = 0; col < w; col++)
                {
                    int Y  = yData[yLine + col] & 0xFF;
                    int uvOff = uvLine + (col >> 1) * uvPixelStride;
                    int U  = uData[uvOff] & 0xFF;
                    int V  = vData[uvOff] & 0xFF;

                    int Yc = Y - 16;
                    int Uc = U - 128;
                    int Vc = V - 128;
                    int R  = (1192 * Yc + 1634 * Vc) >> 10;
                    int G  = (1192 * Yc -  833 * Vc -  400 * Uc) >> 10;
                    int B  = (1192 * Yc + 2066 * Uc) >> 10;
                    if (R < 0) R = 0; else if (R > 255) R = 255;
                    if (G < 0) G = 0; else if (G > 255) G = 255;
                    if (B < 0) B = 0; else if (B > 255) B = 255;

                    argb[idx++] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                }
            }

            return Bitmap.CreateBitmap(argb, w, h, Bitmap.Config.Argb8888!);
        }
    }

    // Простейший адаптер: java.util.concurrent.Executor → Android Handler.
    // SessionConfiguration требует Executor. Пихаем все callback'и в наш cameraHandler,
    // чтобы поведение было идентично legacy CreateCaptureSession(handler).
    private sealed class HandlerExecutor : Java.Lang.Object, Java.Util.Concurrent.IExecutor
    {
        private readonly global::Android.OS.Handler _handler;
        public HandlerExecutor(global::Android.OS.Handler handler) { _handler = handler; }
        public void Execute(IRunnable command)
        {
            _handler.Post(command.Run);
        }
    }

    // Числовые соответствия MediaCodec.INFO_* (на случай если enum не находится)
    private enum MediaCodecInfoState
    {
        TryAgainLater         = -1,
        OutputFormatChanged   = -2,
        OutputBuffersChanged  = -3,
    }

    // Логирует реальный sensor frame duration / AE FPS range / image-processing modes
    // из CaptureResult раз в секунду. Если HAL переопределил наши настройки (например,
    // NR_MODE=2 (HIGH_QUALITY) хотя мы просили 1 (FAST)) — увидим в логах, и значит
    // pipeline принципиально HQ → 30fps lock.
    private sealed class FpsCaptureCb : CameraCaptureSession.CaptureCallback
    {
        private readonly H264EncoderPipeline _o;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private int _frames;
        private long _lastLogMs;
        private bool _firstLogged;

        public FpsCaptureCb(H264EncoderPipeline o) { _o = o; }

        public override void OnCaptureCompleted(
            CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            _frames++;
            long now = _sw.ElapsedMilliseconds;
            if (now - _lastLogMs < 1000) return;

            long durNs = -1;
            try { var d = result.Get(CaptureResult.SensorFrameDuration);
                  if (d != null) durNs = ((Java.Lang.Long)d).LongValue(); } catch { }

            string fpsRange = "?";
            try { var r = result.Get(CaptureResult.ControlAeTargetFpsRange);
                  if (r != null) fpsRange = r.ToString()!; } catch { }

            int ctrlMode = -1, nr = -1, edge = -1, tm = -1, stab = -1, scene = -1;
            try { var v = result.Get(CaptureResult.ControlMode);
                  if (v != null) ctrlMode = ((Java.Lang.Integer)v).IntValue(); } catch { }
            try { var v = result.Get(CaptureResult.NoiseReductionMode);
                  if (v != null) nr = ((Java.Lang.Integer)v).IntValue(); } catch { }
            try { var v = result.Get(CaptureResult.EdgeMode);
                  if (v != null) edge = ((Java.Lang.Integer)v).IntValue(); } catch { }
            try { var v = result.Get(CaptureResult.TonemapMode);
                  if (v != null) tm = ((Java.Lang.Integer)v).IntValue(); } catch { }
            try { var v = result.Get(CaptureResult.ControlVideoStabilizationMode);
                  if (v != null) stab = ((Java.Lang.Integer)v).IntValue(); } catch { }
            try { var v = result.Get(CaptureResult.ControlSceneMode);
                  if (v != null) scene = ((Java.Lang.Integer)v).IntValue(); } catch { }

            double realFps = (now - _lastLogMs > 0) ? _frames * 1000.0 / (now - _lastLogMs) : 0;
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][H264] Camera real fps≈{realFps:F1}  sensorFrameDuration=" +
                $"{(durNs > 0 ? (durNs / 1_000_000.0).ToString("F2") + "ms" : "?")}  AE={fpsRange}");
            // Раз в секунду — детали HAL processing modes. CTRL_MODE: 0=OFF 1=AUTO 2=USE_SCENE 3=USE_EXT.
            // NR/EDGE/TM: 0=OFF 1=FAST 2=HIGH_QUALITY 3=MINIMAL 4=ZSL. STAB/SCENE: 0=OFF/DISABLED.
            // Если просили FAST (1), а в результате HIGH_QUALITY (2) — HAL переопределил, и это
            // объясняет 30fps lock даже на forced sensor frameDuration=16.67ms.
            System.Diagnostics.Debug.WriteLine(
                $"[PCam][H264]   modes: CTRL={ctrlMode} NR={nr} EDGE={edge} TM={tm} STAB={stab} SCENE={scene}");
            _lastLogMs = now;
            _frames = 0;
        }
    }
}
