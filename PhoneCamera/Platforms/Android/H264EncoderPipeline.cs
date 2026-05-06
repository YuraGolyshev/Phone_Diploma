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
    // Callback вызывается на отдельном output thread; не блокировать!
    public System.Action<byte[], int, bool>? OnNalChunk;
    public System.Action<string>?            OnError;

    public bool IsRunning => _running;

    private MediaCodec?            _encoder;
    private Surface?               _inputSurface;
    private CameraDevice?          _device;
    private CameraCaptureSession?  _session;
    private HandlerThread?         _cameraThread;
    private global::Android.OS.Handler? _cameraHandler;
    private System.Threading.Thread? _outputThread;
    private volatile bool          _running;

    private int _w, _h, _fps;
    private string? _cameraId;

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

        // Дождёмся выхода output thread (он сам остановится по EOS или _running=false)
        try { _outputThread?.Join(500); } catch { }
        _outputThread = null;

        try { _encoder?.Stop();    } catch { }
        try { _encoder?.Release(); } catch { }
        _encoder = null;

        try { _inputSurface?.Release(); } catch { }
        _inputSurface = null;

        try { _cameraThread?.QuitSafely(); } catch { }
        _cameraThread  = null;
        _cameraHandler = null;
    }

    // ── Encoder setup ───────────────────────────────────────────────────────
    private void ConfigureEncoder(int w, int h, int fps)
    {
        _encoder = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc!)
                   ?? throw new System.InvalidOperationException("AVC encoder not available");

        var fmt = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc!, w, h)!;
        // COLOR_FormatSurface = 0x7F000789 (= 2130708361). Используем числовую константу,
        // т.к. enum Xamarin может различаться по именам между релизами.
        fmt.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        fmt.SetInteger(MediaFormat.KeyBitRate,        ChooseBitrate(w, h, fps));
        fmt.SetInteger(MediaFormat.KeyFrameRate,      fps);
        fmt.SetInteger(MediaFormat.KeyIFrameInterval, 1); // ключевые кадры раз в секунду
        fmt.SetInteger(MediaFormat.KeyProfile,        (int)MediaCodecProfileType.Avcprofilebaseline);

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
    }

    private static int ChooseBitrate(int w, int h, int fps)
    {
        // Очень грубая эмпирика: 0.1 bit/pixel при 30fps, +50% при 60fps.
        // 1920×1080@30  → ~6 Mbps;  1920×1080@60 → ~9 Mbps;  3840×2160@30 → ~25 Mbps
        long pixels = (long)w * h * fps;
        long bps = pixels / 12; // ≈ 0.083 bpp
        if (bps < 2_000_000) bps = 2_000_000;
        if (bps > 25_000_000) bps = 25_000_000;
        return (int)bps;
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

            // Создаём OutputConfiguration с STREAM_USE_CASE_VIDEO_RECORD = 0x3
            var outputCfg = new global::Android.Hardware.Camera2.Params.OutputConfiguration(_o._inputSurface!);
            const long STREAM_USE_CASE_VIDEO_RECORD = 0x3L;
            try
            {
                outputCfg.StreamUseCase = STREAM_USE_CASE_VIDEO_RECORD;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] Setting StreamUseCase failed: {ex.Message}");
                return false;
            }

            // SessionConfiguration требует executor. Используем простой адаптер на наш Handler,
            // чтобы callback'и шли в тот же поток что и старый CreateCaptureSession.
            var executor = new HandlerExecutor(_o._cameraHandler!);
            var sessionCfg = new global::Android.Hardware.Camera2.Params.SessionConfiguration(
                (int)global::Android.Hardware.Camera2.Params.SessionType.Regular,
                new System.Collections.Generic.List<global::Android.Hardware.Camera2.Params.OutputConfiguration> { outputCfg },
                executor,
                new SessionCb(_o));

            var sessionParams = camera.CreateCaptureRequest(CameraTemplate.Preview);
            sessionParams.Set(CaptureRequest.ControlAeTargetFpsRange,
                new global::Android.Util.Range(Integer.ValueOf(60), Integer.ValueOf(60)));
            sessionParams.Set(CaptureRequest.SensorFrameDuration,
                Java.Lang.Long.ValueOf(16_666_666L));
            sessionCfg.SessionParameters = sessionParams.Build();

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
                // ВАЖНО: используем PREVIEW шаблон, а не RECORD. На многих HAL'ах RECORD
                // включает по умолчанию HDR/EIS/heavy-NR pipeline, который физически
                // ограничивает sensor 30fps. PREVIEW даёт минимум обработки.
                var b = _o._device.CreateCaptureRequest(CameraTemplate.Preview)!;
                b.AddTarget(_o._inputSurface!);

                // ── Все 3A — выключены ────────────────────────────────────────────────
                b.Set(CaptureRequest.ControlMode,    Integer.ValueOf(0));   // CONTROL_MODE_OFF
                b.Set(CaptureRequest.ControlAeMode,  Integer.ValueOf(0));   // AE_OFF
                b.Set(CaptureRequest.ControlAwbMode, Integer.ValueOf(0));   // AWB_OFF
                b.Set(CaptureRequest.ControlAfMode,  Integer.ValueOf(0));   // AF_OFF
                b.Set(CaptureRequest.ControlSceneMode,
                      Integer.ValueOf(0));                                  // SCENE_DISABLED
                b.Set(CaptureRequest.ControlVideoStabilizationMode,
                      Integer.ValueOf(0));                                  // STAB_OFF (EIS убираем)

                // ── Sensor — ручной режим, frame duration фиксирован под целевой fps ──
                long frameDurationNs = 1_000_000_000L / _o._fps;
                long exposureNs      = frameDurationNs; // не больше периода кадра
                b.Set(CaptureRequest.SensorExposureTime,
                      Java.Lang.Long.ValueOf(exposureNs));
                b.Set(CaptureRequest.SensorFrameDuration,
                      Java.Lang.Long.ValueOf(frameDurationNs));
                b.Set(CaptureRequest.SensorSensitivity,
                      Integer.ValueOf(800));

                // ── Image-processing pipeline — на FAST/OFF, чтобы HAL не делал HQ-прогон ──
                // На многих HAL'ах HIGH_QUALITY-режим NR/Edge/Tonemap физически работает
                // только на 30fps (двухпроходная обработка). FAST разблокирует 60fps.
                b.Set(CaptureRequest.NoiseReductionMode,            Integer.ValueOf(1)); // FAST
                b.Set(CaptureRequest.EdgeMode,                      Integer.ValueOf(1)); // FAST
                b.Set(CaptureRequest.TonemapMode,                   Integer.ValueOf(1)); // FAST
                b.Set(CaptureRequest.ColorCorrectionAberrationMode, Integer.ValueOf(1)); // FAST
                b.Set(CaptureRequest.HotPixelMode,                  Integer.ValueOf(1)); // FAST
                b.Set(CaptureRequest.StatisticsFaceDetectMode,      Integer.ValueOf(0)); // OFF
                b.Set(CaptureRequest.StatisticsHotPixelMapMode,     Java.Lang.Boolean.False);
                b.Set(CaptureRequest.StatisticsLensShadingMapMode,  Integer.ValueOf(0)); // OFF
                b.Set(CaptureRequest.BlackLevelLock,                Java.Lang.Boolean.False);

                // AE FPS range — на всякий случай (даже при AE_OFF некоторые HAL читают это
                // как hint для sensor mode selection).
                b.Set(CaptureRequest.ControlAeTargetFpsRange,
                      new global::Android.Util.Range(
                          Integer.ValueOf(_o._fps),
                          Integer.ValueOf(_o._fps)));

                session.SetRepeatingRequest(b.Build(), new FpsCaptureCb(_o), _o._cameraHandler);
                System.Diagnostics.Debug.WriteLine(
                    $"[PCam][H264] Capture session running @ {_o._fps}fps " +
                    $"(template=PREVIEW, NR/Edge/Tonemap=FAST, EIS/SceneMode=OFF, " +
                    $"forced sensor frameDuration={frameDurationNs}ns / {frameDurationNs / 1_000_000.0:F2}ms)");
            }
            catch (System.Exception ex) { _o.ReportError($"OnConfigured: {ex.Message}"); }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            _o.ReportError("Capture session configure failed");
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
