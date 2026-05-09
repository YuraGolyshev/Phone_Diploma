using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Core.Content;
using Java.Interop;
using Microsoft.Maui.Handlers;
using PhoneCamera.Controls;
using ZXing;
using ZXing.Common;
using Android.Runtime;
using System.Runtime.InteropServices;
using AndroidX.Camera.Camera2.InterOp;

namespace PhoneCamera.Platforms.Android;

// Platform view is ImageView — frames decoded directly to Bitmap and set via
// SetImageBitmap(), which replaces the image in-place with no intermediate blank
// state (contrast: MAUI ImageSource.FromStream clears old source first → flicker).
public class CameraPreviewHandler : ViewHandler<CameraPreviewView, ImageView>
{
    private ProcessCameraProvider? cameraProvider;
    private ImageAnalysis? currentAnalysis;
    private Java.Util.Concurrent.IExecutorService? _analyzerExecutor;
    private int _previewGeneration;

    public static PropertyMapper<CameraPreviewView, CameraPreviewHandler> Mapper =
        new PropertyMapper<CameraPreviewView, CameraPreviewHandler>(ViewMapper)
        {
            [nameof(CameraPreviewView.IsRunning)] = MapIsRunning,
            [nameof(CameraPreviewView.SelectedConfig)] = MapSelectedConfig,
            [nameof(CameraPreviewView.Format)] = MapFormat,
            [nameof(CameraPreviewView.PreviewEnabled)] = MapPreviewEnabled,
        };

    // ── H.264 pipeline (активен только когда VirtualView.Format == StreamFormat.H264) ──
    private H264EncoderPipeline? _h264;
    private string? _backCameraId;
    // Текущий показанный preview-Bitmap для H.264 ветки. Recycle'ится при замене.
    private global::Android.Graphics.Bitmap? _h264PrevBitmap;
    // Состояние превью
    private bool _previewEnabled = true;
    private global::Android.Graphics.Paint? _previewDisabledPaint;

    public CameraPreviewHandler() : base(Mapper) { }

    protected override ImageView CreatePlatformView()
    {
        var view = new ImageView(Context);
        view.SetScaleType(ImageView.ScaleType.CenterCrop);
        return view;
    }

    protected override void ConnectHandler(ImageView platformView)
    {
        base.ConnectHandler(platformView);
        System.Diagnostics.Debug.WriteLine("[PCam][Android] ConnectHandler — handler attached");
        Task.Run(QueryConfigs);
        if (VirtualView.IsRunning)
            StartCamera();
    }

    protected override void DisconnectHandler(ImageView platformView)
    {
        System.Diagnostics.Debug.WriteLine("[PCam][Android] DisconnectHandler — handler detached");
        StopCamera();
        base.DisconnectHandler(platformView);
    }

    private void QueryConfigs()
    {
        System.Diagnostics.Debug.WriteLine("[PCam][Android] QueryConfigs starting...");
        try
        {
            var cameraManager = (CameraManager)Context.GetSystemService("camera")!;
            var configs = new List<CameraConfig>();

            foreach (var id in cameraManager.GetCameraIdList())
            {
                var chars = cameraManager.GetCameraCharacteristics(id);
                var facing = chars.Get(CameraCharacteristics.LensFacing);
                if (facing == null || ((Java.Lang.Integer)facing).IntValue() != 1)
                    continue;

                _backCameraId = id;

                var streamMap = chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
                    as StreamConfigurationMap;
                if (streamMap == null)
                    continue;

                // Диагностика: диапазоны AE FPS, которые аппаратно поддерживает камера
                try
                {
                    var aeObj = chars.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges);
                    if (aeObj != null)
                    {
                        int cnt = JNIEnv.GetArrayLength(aeObj.Handle);
                        var sb = new System.Text.StringBuilder();
                        for (int k = 0; k < cnt; k++)
                        {
                            var elem = Java.Lang.Object.GetObject<Java.Lang.Object>(
                                JNIEnv.GetObjectArrayElement(aeObj.Handle, k),
                                global::Android.Runtime.JniHandleOwnership.TransferLocalRef);
                            if (k > 0) sb.Append(", ");
                            sb.Append(elem?.ToString());
                        }
                        System.Diagnostics.Debug.WriteLine($"[PCam][Android] QueryConfigs AE FPS ranges: {sb}");
                    }
                }
                catch { }

                // YUV_420_888 — именно этот формат использует ImageAnalysis.
                int yuvFormat = (int)ImageFormatType.Yuv420888;
                var sizes = streamMap.GetOutputSizes(yuvFormat);
                if (sizes == null)
                    continue;

                foreach (var size in sizes)
                {
                    long minDuration = streamMap.GetOutputMinFrameDuration(yuvFormat, size);
                    if (minDuration <= 0) continue;

                    double maxFps = 1_000_000_000.0 / minDuration;
                    foreach (int fps in new[] { 120, 60, 30, 24, 15 })
                    {
                        if (fps <= maxFps + 0.5)
                            configs.Add(new CameraConfig(size.Width, size.Height, fps, fps));
                    }
                }

                // ── Диагностика: high-speed video возможности (≥120fps preview/recording-only path) ──
                // Если устройство объявляет HighSpeedVideoSizes/Ranges — у нас есть теоретическая
                // возможность гнать 60+fps через CameraConstrainedHighSpeedCaptureSession,
                // но НЕ через ImageReader (только SurfaceTexture/MediaRecorder targets).
                try
                {
                    var hsSizes = streamMap.GetHighSpeedVideoSizes();
                    if (hsSizes == null || hsSizes.Length == 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[PCam][Android] HighSpeed: device does NOT advertise high-speed video sizes");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[PCam][Android] HighSpeed: {hsSizes.Length} size(s) advertised:");
                        foreach (var sz in hsSizes)
                        {
                            var rangesForSize = streamMap.GetHighSpeedVideoFpsRangesFor(sz);
                            var srb = new System.Text.StringBuilder();
                            if (rangesForSize != null)
                            {
                                for (int i = 0; i < rangesForSize.Length; i++)
                                {
                                    if (i > 0) srb.Append(", ");
                                    srb.Append(rangesForSize[i].ToString());
                                }
                            }
                            System.Diagnostics.Debug.WriteLine(
                                $"[PCam][Android]   HS {sz.Width}×{sz.Height} → ranges: [{srb}]");
                        }
                    }

                    // Также общие диапазоны (без привязки к размеру)
                    var hsRanges = streamMap.GetHighSpeedVideoFpsRanges();
                    if (hsRanges != null && hsRanges.Length > 0)
                    {
                        var rb = new System.Text.StringBuilder();
                        for (int i = 0; i < hsRanges.Length; i++)
                        {
                            if (i > 0) rb.Append(", ");
                            rb.Append(hsRanges[i].ToString());
                        }
                        System.Diagnostics.Debug.WriteLine(
                            $"[PCam][Android] HighSpeed: union FPS ranges: [{rb}]");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][Android] HighSpeed query ERROR: {ex.Message}");
                }

                break;
            }

            var sorted = configs
                .OrderByDescending(c => (long)c.Width * c.Height)
                .ThenByDescending(c => c.MaxFps)
                .ToList();

            var defaultConfig = CameraConfig.FindDefault(sorted);

            System.Diagnostics.Debug.WriteLine($"[PCam][Android] QueryConfigs done: {sorted.Count} configs, default={defaultConfig?.DisplayName ?? "none"}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (VirtualView == null) return;
                VirtualView.SelectedConfig = defaultConfig;
                VirtualView.ConfigurationsReady?.Invoke(sorted);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PCam][Android] QueryConfigs ERROR: {ex.Message}");
        }
    }

    private void StartCamera()
    {
        var config = VirtualView.SelectedConfig;

        // ── Ветка H.264: МИНУЯ CameraX, прямой Camera2+MediaCodec → 60fps ────────────
        if (VirtualView.Format == StreamFormat.H264 && !VirtualView.IsScanning)
        {
            StopCameraXOnly();
            StartH264();
            return;
        }
        // ── Ветка JPEG (CameraX ImageAnalysis): сюда же при IsScanning=true ────────
        StopH264();

        int targetRotation = (config != null && config.Width >= config.Height) ? 1 : 0;
        System.Diagnostics.Debug.WriteLine($"[PCam][Android] StartCamera → config={config?.DisplayName ?? "none"}, targetRotation={targetRotation} ({(targetRotation == 1 ? "landscape" : "portrait")})");

        // Capture PlatformView reference — used to display frames directly.
        var imageView = PlatformView;

        var future = ProcessCameraProvider.GetInstance(Context);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            cameraProvider = (ProcessCameraProvider)future.Get()!;
            cameraProvider.UnbindAll();

            // Preview use case удалён: только YUV ImageAnalysis.
            // Комбинация PRIV+YUV(1920×1080) аппаратно ограничена 30fps;
            // одиночный YUV(1920×1080) снимает это ограничение → 60fps.
            var analysisBuilder = new ImageAnalysis.Builder()
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
                .SetTargetRotation(targetRotation);

            int targetFps = 0;
            if (config != null)
            {
                int rW = Math.Max(config.Width, config.Height);
                int rH = Math.Min(config.Width, config.Height);
                System.Diagnostics.Debug.WriteLine($"[PCam][Android] StartCamera resRequest={rW}×{rH} (sensor landscape)");
                var targetSize = new global::Android.Util.Size(rW, rH);
                var resSel = new ResolutionSelector.Builder()
                    .SetResolutionFilter(new ExactResolutionFilter(targetSize))
                    .Build();
                analysisBuilder.SetResolutionSelector(resSel);

                targetFps = (int)Math.Round(config.MaxFps);
                if (targetFps > 0)
                {
                    // Все Camera2-настройки (3A, EIS, image-processing, sensor manual,
                    // AE FPS range) применяются единообразно через общий CameraSettings.
                    // Тыкать настройки → Platforms/Android/CameraSettings.cs.
                    var extender = new Camera2Interop.Extender(analysisBuilder);
                    CameraSettings.ApplyToExtender(extender, targetFps);
                    System.Diagnostics.Debug.WriteLine(
                        $"[PCam][Android] StartCamera fps={targetFps} " +
                        $"(3A={(CameraSettings.ENABLE_3A ? "AUTO" : "OFF")}, " +
                        $"EIS={(CameraSettings.ENABLE_VIDEO_STABILIZATION ? "ON" : "OFF")}, " +
                        $"NR={CameraSettings.NOISE_REDUCTION_MODE}/Edge={CameraSettings.EDGE_MODE}/TM={CameraSettings.TONEMAP_MODE})");
                }
            }

            _analyzerExecutor?.Shutdown();
            _analyzerExecutor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor();

            currentAnalysis = analysisBuilder.Build();
            currentAnalysis.SetAnalyzer(_analyzerExecutor, new DispatchingAnalyzer(VirtualView, imageView, 2, this));
            System.Diagnostics.Debug.WriteLine("[PCam][Android] StartCamera using 2-worker DispatchingAnalyzer");

            try
            {
                cameraProvider.BindToLifecycle(
                    (AndroidX.Lifecycle.ILifecycleOwner)Platform.CurrentActivity!,
                    CameraSelector.DefaultBackCamera,
                    currentAnalysis);
                System.Diagnostics.Debug.WriteLine("[PCam][Android] StartCamera ✓ camera bound successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][Android] StartCamera ERROR BindToLifecycle: {ex.Message}");
            }
        }), ContextCompat.GetMainExecutor(Context));
    }

    private void StopCamera()
    {
        System.Diagnostics.Debug.WriteLine("[PCam][Android] StopCamera");
        StopCameraXOnly();
        StopH264();
    }

    private void StopCameraXOnly()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            currentAnalysis?.ClearAnalyzer();
            cameraProvider?.UnbindAll();
        });
        _analyzerExecutor?.Shutdown();
        _analyzerExecutor = null;
    }

    // ── H.264 pipeline (Camera2 → MediaCodec → NAL chunks → CameraStreamingService) ──
    private void StartH264()
    {
        var cfg = VirtualView.SelectedConfig;
        int w   = cfg != null ? Math.Max(cfg.Width, cfg.Height) : 1920;
        int h   = cfg != null ? Math.Min(cfg.Width, cfg.Height) : 1080;
        int fps = cfg != null ? (int)Math.Round(cfg.MaxFps)     : 60;

        if (string.IsNullOrEmpty(_backCameraId))
        {
            // QueryConfigs ещё не отработал — подождём, повторим из MapFormat позднее.
            System.Diagnostics.Debug.WriteLine("[PCam][Android] StartH264: backCameraId not ready, skipping");
            return;
        }

        StopH264();

        _h264 = new H264EncoderPipeline();
        _h264.PreviewEnabled = _previewEnabled;
        _h264.OnNalChunk = (data, len, isKey) =>
        {
            // VirtualView.H264NalReady берётся в момент вызова — MainPage его подвязывает
            // на _streaming.EnqueueRawBytes. Если ещё не подвязан — кадр выкидывается.
            try { VirtualView?.H264NalReady?.Invoke(data, len, isKey); } catch { }
        };
        _h264.OnError = (msg) =>
            System.Diagnostics.Debug.WriteLine($"[PCam][Android] H264 pipeline error: {msg}");

        // Preview: рисуем 640×480 ARGB Bitmap прямо в наш ImageView через Post().
        // Вызывается с camera-thread'а; Post переводит в UI-thread.
        var preview = PlatformView;
        if (_previewGeneration == 0)
            _previewGeneration = 1; // первый запуск
        int gen = _previewGeneration; // захватываем текущее поколение

        _h264.OnPreviewBitmap = bmp =>
        {
            // Если поколение сменилось (произошёл перезапуск), игнорируем битмап
            if (gen != _previewGeneration)
            {
                bmp?.Recycle();
                return;
            }
            try
            {
                var preview = PlatformView;
                preview.Post(() =>
                {
                    // Дополнительная проверка внутри Post на случай, если за время ожидания поколение опять сменилось
                    if (gen != _previewGeneration)
                    {
                        bmp?.Recycle();
                        return;
                    }
                    var prev = _h264PrevBitmap;
                    _h264PrevBitmap = bmp;
                    preview.SetImageBitmap(bmp);
                    prev?.Recycle();
                });
            }
            catch
            {
                try { bmp?.Recycle(); } catch { }
            }
        };

        System.Diagnostics.Debug.WriteLine(
            $"[PCam][Android] StartH264 → cam={_backCameraId} {w}x{h}@{fps}fps");
        _h264.Start(_backCameraId!, w, h, fps);
    }

    private void StopH264()
    {
        if (_h264 == null) return;
        System.Diagnostics.Debug.WriteLine("[PCam][Android] StopH264");
        try { _h264.Stop(); } catch { }
        try { _h264.Dispose(); } catch { }
        _h264 = null;

        // Удаляем последний preview Bitmap — он живёт на ImageView, после Stop
        // он там и остаётся «зависнувшим». Через Post переключим на CameraX-поток
        // (он сам перезапишет ImageView когда стартует), а наш Bitmap recycle'нем.
        var iv = PlatformView;
        var bmp = _h264PrevBitmap;
        _h264PrevBitmap = null;
        if (bmp != null)
        {
            iv.Post(() => { try { bmp.Recycle(); } catch { } });
        }

    }

    private void DisplayPreviewDisabledMessage()
    {
        var iv = PlatformView;
        const int width = 960;
        const int height = 540;

        iv.Post(() =>
        {
            try
            {
                // Создаём чёрный Bitmap с белым текстом "Превью отключено"
                var bmp = global::Android.Graphics.Bitmap.CreateBitmap(width, height, global::Android.Graphics.Bitmap.Config.Argb8888!);
                if (bmp == null) return;

                var canvas = new global::Android.Graphics.Canvas(bmp);
                canvas.DrawColor(global::Android.Graphics.Color.Black);

                if (_previewDisabledPaint == null)
                {
                    _previewDisabledPaint = new global::Android.Graphics.Paint
                    {
                        Color = global::Android.Graphics.Color.White,
                        TextSize = 48f,
                        TextAlign = global::Android.Graphics.Paint.Align.Center
                    };
                    _previewDisabledPaint.SetTypeface(global::Android.Graphics.Typeface.Create("sans-serif", global::Android.Graphics.TypefaceStyle.Bold));
                }

                string text = "Превью отключено";
                canvas.DrawText(text, width / 2f, height / 2f, _previewDisabledPaint);

                iv.SetImageBitmap(bmp);

                // Recycle старый Bitmap
                if (_h264PrevBitmap != null)
                {
                    try { _h264PrevBitmap.Recycle(); } catch { }
                    _h264PrevBitmap = null;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][Android] DisplayPreviewDisabledMessage error: {ex.Message}");
            }
        });
    }

    private static void MapIsRunning(CameraPreviewHandler handler, CameraPreviewView view)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][Android] MapIsRunning → IsRunning={view.IsRunning}");
        if (view.IsRunning)
            handler.StartCamera();
        else
            handler.StopCamera();
    }

    private static void MapSelectedConfig(CameraPreviewHandler handler, CameraPreviewView view)
    {
        System.Diagnostics.Debug.WriteLine($"[PCam][Android] MapSelectedConfig → config={view.SelectedConfig?.DisplayName ?? "none"}, running={view.IsRunning}");
        if (view.IsRunning)
        {
            handler.cameraProvider?.UnbindAll();
            handler.StartCamera();
        }
    }

    private static void MapFormat(CameraPreviewHandler handler, CameraPreviewView view)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PCam][Android] MapFormat → format={view.Format}, scanning={view.IsScanning}, running={view.IsRunning}");
        if (view.IsRunning)
        {
            // Полный рестарт pipeline — StartCamera сам выберет ветку (CameraX или H.264).
            handler.StopCamera();
            handler.StartCamera();
        }
    }

    private static void MapPreviewEnabled(CameraPreviewHandler handler, CameraPreviewView view)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PCam][Android] MapPreviewEnabled → PreviewEnabled={view.PreviewEnabled}");

        handler._previewEnabled = view.PreviewEnabled;

        if (view.IsRunning && view.Format == StreamFormat.H264)
        {
            // Увеличиваем поколение ДО остановки, чтобы старые колбэки начали игнорироваться
            Interlocked.Increment(ref handler._previewGeneration);
            handler.StopCamera();
            handler.StartCamera();

            if (!view.PreviewEnabled)
                handler.DisplayPreviewDisabledMessage();
        }
        else if (!view.PreviewEnabled)
        {
            handler.DisplayPreviewDisabledMessage();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FrameAnalyzer: YUV→JPEG для стриминга, Gray8→ZXing для сканирования QR.
    // Превью: кадры декодируются из JPEG прямо в Bitmap и выставляются на
    // ImageView через SetImageBitmap() — без мигания (не как ImageSource.FromStream).
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class FrameAnalyzer : Java.Lang.Object
    {
        private readonly CameraPreviewView _view;
        private readonly ImageView         _imageView;
        private readonly OrderedDelivery   _delivery;
        private readonly CameraPreviewHandler _handler;
        private int  _count;

        // Shared across all workers so that only one fires QR callback per 200ms window.
        private readonly long[] _sharedLastQrMs;
        // Shared across all workers: display throttle (~30fps max to avoid unnecessary Bitmap allocs).
        private readonly long[] _sharedLastDisplayMs;

        private int  _busy;  // 0=свободен, 1=кодирует; доступ только через Interlocked

        // ── Замер времени ─────────────────────────────────────────────────────
        private readonly System.Diagnostics.Stopwatch _diagSw = System.Diagnostics.Stopwatch.StartNew();
        private long _lastAnalyzeMs;
        private long _totalCopyMs;
        private long _totalJpegMs;
        private int  _jpegSamples;

        // Переиспользуемые буферы — выделяются один раз, пересоздаются только при смене разрешения
        private byte[]? _nv21;
        private byte[]? _nv21Rotated;
        private byte[]? _yDataQr;
        private readonly System.IO.MemoryStream _jpegMs = new(300_000);

        // Bitmap, отображаемый сейчас на ImageView (только UI-поток)
        private Bitmap? _prevDisplayBitmap;

        // Опции decode для превью: downsample 2× по стороне (4× по площади).
        // 1920×1080 JPEG → 960×540 Bitmap. Decode ~3-5ms вместо ~15-20ms,
        // bitmap 2MB вместо 8MB → меньше нагрузки на GC и UI thread,
        // на экране всё равно scale CenterCrop, разница не видна.
        private readonly BitmapFactory.Options _displayDecodeOpts =
            new() { InSampleSize = 2 };

        // Шаг 5: переиспользуемый Java byte[] для YuvImage (устраняет ~3MB LOS-аллокацию per frame)
        private IntPtr _jNv21Ref;
        private int    _jNv21Len;
        private sbyte[]? _sbyteBuffer;
        private static IntPtr     _sYuvImageClass;
        private static IntPtr     _sYuvImageCtor;
        private static readonly object _sJniLock = new();
        private static bool       _sJniReady;

        public FrameAnalyzer(CameraPreviewView view, ImageView imageView,
                             OrderedDelivery delivery,
                             long[] sharedLastQrMs, long[] sharedLastDisplayMs, CameraPreviewHandler handler)
        {
            _view                = view;
            _imageView           = imageView;
            _delivery            = delivery;
            _handler             = handler;
            _sharedLastQrMs      = sharedLastQrMs;
            _sharedLastDisplayMs = sharedLastDisplayMs;
            EnsureJniIds();
        }

        public bool TryBeginProcess(IImageProxy image, long seq)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return false;

            _count++;
            long nowMs = _diagSw.ElapsedMilliseconds;
            long gapMs = nowMs - _lastAnalyzeMs;
            _lastAnalyzeMs = nowMs;
            int count = _count;

            int imgW   = image.Width;
            int imgH   = image.Height;
            int imgRot = image.ImageInfo?.RotationDegrees ?? 0;

            if (_view.IsScanning)
            {
                long prevQrMs = Volatile.Read(ref _sharedLastQrMs[0]);
                bool doScan = nowMs - prevQrMs >= 200
                    && Interlocked.CompareExchange(ref _sharedLastQrMs[0], nowMs, prevQrMs) == prevQrMs;
                if (doScan)
                    CopyYFromProxy(image, imgW, imgH);
                image.Close();
                if (!doScan)
                {
                    _delivery.Post(seq, null, 0, null);
                    Interlocked.Exchange(ref _busy, 0);
                    return true;
                }
                Task.Run(() =>
                {
                    try
                    {
                        var qr = DecodeQrFromBuffer(imgW, imgH);
                        if (qr != null)
                            MainThread.BeginInvokeOnMainThread(() => _view.QrCodeDetected?.Invoke(qr));
                    }
                    finally
                    {
                        _delivery.Post(seq, null, 0, null);
                        Interlocked.Exchange(ref _busy, 0);
                    }
                });
            }
            else
            {
                var cb = _view.FrameReady;
                long t0 = _diagSw.ElapsedMilliseconds;
                CopyNv21FromProxy(image, imgW, imgH);
                long copyMs = _diagSw.ElapsedMilliseconds - t0;
                image.Close();
                Task.Run(() =>
                {
                    try
                    {
                        bool isStreaming = cb != null;
                        int quality      = isStreaming ? 25 : 25;
                        int effectiveRot = isStreaming ? 0 : imgRot;
                        long t1 = _diagSw.ElapsedMilliseconds;
                        bool ok = EncodeNv21ToJpeg(imgW, imgH, effectiveRot, quality);
                        long encMs = _diagSw.ElapsedMilliseconds - t1;

                        // ── Превью: декодируем JPEG в Bitmap и ставим на ImageView ──────────
                        // Throttle: ~30fps max across all workers (shared CAS timestamp).
                        // SetImageBitmap() заменяет картинку без промежуточного чёрного кадра
                        // (в отличие от ImageSource.FromStream, который сбрасывает старый Source).
                        // _prevDisplayBitmap — только UI-поток, рецикл после замены.
                        if (ok && _handler._previewEnabled)
                        {
                            long prevDisp = Volatile.Read(ref _sharedLastDisplayMs[0]);
                            bool doDisp = nowMs - prevDisp >= 33   // ≤30fps per window
                                && Interlocked.CompareExchange(ref _sharedLastDisplayMs[0], nowMs, prevDisp) == prevDisp;
                            if (doDisp)
                            {
                                // Decode прямо из _jpegMs — мы на worker thread, в этой же лямбде
                                // ниже происходит _delivery.Post, который при out-of-order сам копирует
                                // буфер. Аллокация промежуточного byte[] не нужна.
                                int dLen = (int)_jpegMs.Length;
                                var bmp  = BitmapFactory.DecodeByteArray(
                                    _jpegMs.GetBuffer(), 0, dLen, _displayDecodeOpts);
                                if (bmp != null)
                                {
                                    _imageView.Post(() =>
                                    {
                                        var prev = _prevDisplayBitmap;
                                        _prevDisplayBitmap = bmp;
                                        _imageView.SetImageBitmap(bmp);
                                        prev?.Recycle();
                                    });
                                }
                            }
                        }

                        _delivery.Post(seq, ok ? _jpegMs.GetBuffer() : null, (int)_jpegMs.Length, cb);
                        _totalCopyMs += copyMs;
                        _totalJpegMs += encMs;
                        _jpegSamples++;
                        if (count % 30 == 0)
                        {
                            long avgCopy   = _jpegSamples > 0 ? _totalCopyMs / _jpegSamples : -1;
                            long avgEncode = _jpegSamples > 0 ? _totalJpegMs / _jpegSamples : -1;
                            System.Diagnostics.Debug.WriteLine(
                                $"[PCam][Android] Frame #{count}  {imgW}×{imgH}  rot={imgRot}°" +
                                $"  gap={gapMs}ms  copy={avgCopy}ms  encode={avgEncode}ms  → workerFPS≈{(gapMs > 0 ? 1000 / gapMs : 0)}");
                            _totalCopyMs = 0;
                            _totalJpegMs = 0;
                            _jpegSamples = 0;
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _busy, 0);
                    }
                });
            }
            return true;
        }

        private void CopyNv21FromProxy(IImageProxy image, int w, int h)
        {
            var planes  = image.GetPlanes();
            var yPl     = planes[0];
            var vPl     = planes[2];
            var uPl     = planes[1];

            int yStride  = yPl.RowStride;
            int uvStride = vPl.RowStride;
            int uvPixel  = vPl.PixelStride;

            var yBuf = yPl.Buffer;
            var vBuf = vPl.Buffer;
            var uBuf = uPl.Buffer;

            int nv21Len = w * h + w * h / 2;
            if (_nv21 == null || _nv21.Length != nv21Len)
                _nv21 = new byte[nv21Len];

            IntPtr yPtr = JNIEnv.GetDirectBufferAddress(yBuf!.Handle);
            if (yStride == w)
                Marshal.Copy(yPtr, _nv21, 0, w * h);
            else
                for (int r = 0; r < h; r++)
                    Marshal.Copy((nint)yPtr + r * yStride, _nv21, r * w, w);

            int uvStart = w * h;
            if (uvPixel == 2)
            {
                IntPtr uvPtr = JNIEnv.GetDirectBufferAddress(vBuf!.Handle);
                int uvCapacity = (h / 2 - 1) * uvStride + uvPixel * (w / 2 - 1) + 1;
                for (int r = 0; r < h / 2; r++)
                {
                    int count = Math.Min(w, uvCapacity - r * uvStride);
                    Marshal.Copy((nint)uvPtr + r * uvStride, _nv21, uvStart + r * w, count);
                }
            }
            else
            {
                IntPtr vPtr = JNIEnv.GetDirectBufferAddress(vBuf!.Handle);
                IntPtr uPtr = JNIEnv.GetDirectBufferAddress(uBuf!.Handle);
                int uvPos = uvStart;
                for (int r = 0; r < h / 2; r++)
                    for (int c = 0; c < w / 2; c++)
                    {
                        int idx = r * uvStride + c;
                        _nv21[uvPos++] = Marshal.ReadByte(vPtr, idx);
                        _nv21[uvPos++] = Marshal.ReadByte(uPtr, idx);
                    }
            }
        }

        private bool EncodeNv21ToJpeg(int w, int h, int rotDeg, int quality = 20)
        {
            try
            {
                byte[] nv21Enc;
                int encW, encH;
                if (rotDeg == 90 || rotDeg == 180 || rotDeg == 270)
                    (nv21Enc, encW, encH) = RotateNv21(_nv21!, w, h, rotDeg);
                else
                {
                    nv21Enc = _nv21!;
                    encW = w;
                    encH = h;
                }

                int jLen = nv21Enc.Length;
                EnsureJNv21Buffer(jLen);
                if (_sbyteBuffer == null || _sbyteBuffer.Length < jLen)
                    _sbyteBuffer = new sbyte[jLen];
                Buffer.BlockCopy(nv21Enc, 0, _sbyteBuffer, 0, jLen);
                var jRef = new JniObjectReference(_jNv21Ref, JniObjectReferenceType.Global);
                unsafe
                {
                    fixed (sbyte* p = _sbyteBuffer)
                        JniEnvironment.Arrays.SetByteArrayRegion(jRef, 0, jLen, p);
                }

                IntPtr yuvImgHandle = JNIEnv.NewObject(
                    _sYuvImageClass, _sYuvImageCtor,
                    new JValue(_jNv21Ref),
                    new JValue((int)ImageFormatType.Nv21),
                    new JValue(encW),
                    new JValue(encH),
                    new JValue(IntPtr.Zero));
                var yuvImg = Java.Lang.Object.GetObject<global::Android.Graphics.YuvImage>(
                    yuvImgHandle, JniHandleOwnership.TransferLocalRef)!;
                _jpegMs.SetLength(0);
                yuvImg.CompressToJpeg(new global::Android.Graphics.Rect(0, 0, encW, encH), quality, _jpegMs);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PCam][Android] EncodeNv21ToJpeg error: {ex.Message}");
                return false;
            }
        }

        private (byte[] data, int w, int h) RotateNv21(byte[] src, int srcW, int srcH, int rotation)
        {
            bool swap = rotation == 90 || rotation == 270;
            int dstW   = swap ? srcH : srcW;
            int dstH   = swap ? srcW : srcH;
            int dstLen = dstW * dstH + dstW * dstH / 2;
            if (_nv21Rotated == null || _nv21Rotated.Length != dstLen)
                _nv21Rotated = new byte[dstLen];

            switch (rotation)
            {
                case 90:
                    for (int j = 0; j < srcH; j++)
                    {
                        int srcOff = j * srcW;
                        int dstCol = dstW - 1 - j;
                        for (int i = 0; i < srcW; i++)
                            _nv21Rotated[i * dstW + dstCol] = src[srcOff + i];
                    }
                    break;
                case 180:
                    for (int j = 0; j < srcH; j++)
                    {
                        int srcOff = j * srcW;
                        int dstOff = (dstH - 1 - j) * dstW + (dstW - 1);
                        for (int i = 0; i < srcW; i++)
                            _nv21Rotated[dstOff - i] = src[srcOff + i];
                    }
                    break;
                case 270:
                    for (int j = 0; j < srcH; j++)
                    {
                        int srcOff  = j * srcW;
                        int dstBase = (dstH - 1) * dstW + j;
                        for (int i = 0; i < srcW; i++)
                            _nv21Rotated[dstBase - i * dstW] = src[srcOff + i];
                    }
                    break;
            }

            int uvSrcOff = srcW * srcH;
            int uvDstOff = dstW * dstH;
            int srcUvW = srcW / 2, srcUvH = srcH / 2;
            switch (rotation)
            {
                case 90:
                    for (int uvJ = 0; uvJ < srcUvH; uvJ++)
                    {
                        int srcRow = uvSrcOff + uvJ * srcW;
                        int dstCol = (srcUvH - 1 - uvJ) * 2;
                        for (int uvI = 0; uvI < srcUvW; uvI++)
                        {
                            int dstUvIdx = uvDstOff + uvI * dstW + dstCol;
                            _nv21Rotated[dstUvIdx]     = src[srcRow + uvI * 2];
                            _nv21Rotated[dstUvIdx + 1] = src[srcRow + uvI * 2 + 1];
                        }
                    }
                    break;
                case 180:
                    for (int uvJ = 0; uvJ < srcUvH; uvJ++)
                    {
                        int srcRow  = uvSrcOff + uvJ * srcW;
                        int dstBase = uvDstOff + (srcUvH - 1 - uvJ) * dstW + (srcUvW - 1) * 2;
                        for (int uvI = 0; uvI < srcUvW; uvI++)
                        {
                            int dstUvIdx = dstBase - uvI * 2;
                            _nv21Rotated[dstUvIdx]     = src[srcRow + uvI * 2];
                            _nv21Rotated[dstUvIdx + 1] = src[srcRow + uvI * 2 + 1];
                        }
                    }
                    break;
                case 270:
                    for (int uvJ = 0; uvJ < srcUvH; uvJ++)
                    {
                        int srcRow     = uvSrcOff + uvJ * srcW;
                        int dstColBase = uvJ * 2;
                        for (int uvI = 0; uvI < srcUvW; uvI++)
                        {
                            int dstUvIdx = uvDstOff + (srcUvW - 1 - uvI) * dstW + dstColBase;
                            _nv21Rotated[dstUvIdx]     = src[srcRow + uvI * 2];
                            _nv21Rotated[dstUvIdx + 1] = src[srcRow + uvI * 2 + 1];
                        }
                    }
                    break;
            }

            return (_nv21Rotated, dstW, dstH);
        }

        private void CopyYFromProxy(IImageProxy image, int w, int h)
        {
            var yPl     = image.GetPlanes()[0];
            int yStride = yPl.RowStride;
            var yBuf    = yPl.Buffer;

            if (_yDataQr == null || _yDataQr.Length != w * h)
                _yDataQr = new byte[w * h];

            IntPtr yPtr = JNIEnv.GetDirectBufferAddress(yBuf!.Handle);
            if (yStride == w)
                Marshal.Copy(yPtr, _yDataQr, 0, w * h);
            else
                for (int r = 0; r < h; r++)
                    Marshal.Copy((nint)yPtr + r * yStride, _yDataQr, r * w, w);
        }

        private string? DecodeQrFromBuffer(int w, int h)
        {
            try
            {
                var source    = new RGBLuminanceSource(_yDataQr!, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
                var binarizer = new HybridBinarizer(source);
                var bitmap    = new BinaryBitmap(binarizer);
                var hints     = new Dictionary<DecodeHintType, object>
                {
                    { DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE } }
                };
                var result = new MultiFormatReader().decode(bitmap, hints);
                return result?.Text;
            }
            catch
            {
                return null;
            }
        }

        private void EnsureJNv21Buffer(int jLen)
        {
            if (_jNv21Ref != IntPtr.Zero && _jNv21Len == jLen) return;
            if (_jNv21Ref != IntPtr.Zero)
                JNIEnv.DeleteGlobalRef(_jNv21Ref);
            var localRef = JniEnvironment.Arrays.NewByteArray(jLen);
            _jNv21Ref = JNIEnv.NewGlobalRef(localRef.Handle);
            _jNv21Len = jLen;
        }

        private static void EnsureJniIds()
        {
            if (_sJniReady) return;
            lock (_sJniLock)
            {
                if (_sJniReady) return;
                IntPtr cls = JNIEnv.FindClass("android/graphics/YuvImage");
                _sYuvImageClass = JNIEnv.NewGlobalRef(cls);
                _sYuvImageCtor = JNIEnv.GetMethodID(_sYuvImageClass, "<init>", "([BIII[I)V");
                _sJniReady = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_jNv21Ref != IntPtr.Zero)
                {
                    JNIEnv.DeleteGlobalRef(_jNv21Ref);
                    _jNv21Ref = IntPtr.Zero;
                }
            }
            base.Dispose(disposing);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DispatchingAnalyzer
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class DispatchingAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        private readonly FrameAnalyzer[] _workers;
        private readonly OrderedDelivery _delivery;
        private readonly CameraPreviewHandler _handler;
        private long _seqCounter = -1;

        private readonly long[] _sharedLastQrMs      = new long[1];
        private readonly long[] _sharedLastDisplayMs = new long[1];

        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private long _lastLogMs;

        public DispatchingAnalyzer(CameraPreviewView view, ImageView imageView, int workerCount, CameraPreviewHandler handler)
        {
            _handler  = handler;
            _delivery = new OrderedDelivery(workerCount);
            _workers  = new FrameAnalyzer[workerCount];
            for (int i = 0; i < workerCount; i++)
                _workers[i] = new FrameAnalyzer(view, imageView, _delivery,
                                                 _sharedLastQrMs, _sharedLastDisplayMs, _handler);
            System.Diagnostics.Debug.WriteLine($"[PCam][Android] DispatchingAnalyzer created with {workerCount} workers");
        }

        public global::Android.Util.Size? DefaultTargetResolution => null;

        public void Analyze(IImageProxy image)
        {
            long seq = Interlocked.Increment(ref _seqCounter);
            int preferred = (int)((ulong)seq % (ulong)_workers.Length);
            for (int i = 0; i < _workers.Length; i++)
            {
                if (_workers[(preferred + i) % _workers.Length].TryBeginProcess(image, seq))
                {
                    if (seq % 30 == 29)
                    {
                        long nowMs   = _sw.ElapsedMilliseconds;
                        long elapsed = nowMs - Interlocked.Exchange(ref _lastLogMs, nowMs);
                        System.Diagnostics.Debug.WriteLine(
                            $"[PCam][Android] Combined #{seq + 1}  combinedFPS≈{(elapsed > 0 ? 30000.0 / elapsed : 0):F1}  ({_workers.Length} workers)");
                    }
                    return;
                }
            }
            image.Close();
            _delivery.Post(seq, null, 0, null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                System.Diagnostics.Debug.WriteLine("[PCam][Android] DispatchingAnalyzer.Dispose — releasing all workers");
                foreach (var w in _workers)
                {
                    try { w.Dispose(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PCam][Android] DispatchingAnalyzer.Dispose worker error: {ex.Message}");
                    }
                }
            }
            base.Dispose(disposing);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OrderedDelivery
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class OrderedDelivery
    {
        private long _nextSeq;
        private readonly (byte[]? Buf, int Len, Action<byte[], int>? Cb)[] _slots;
        private readonly long[] _slotSeqs;
        private readonly object _lock = new();

        public OrderedDelivery(int maxInFlight)
        {
            _slots    = new (byte[]?, int, Action<byte[], int>?)[maxInFlight];
            _slotSeqs = new long[maxInFlight];
            Array.Fill(_slotSeqs, -1L);
        }

        public void Post(long seq, byte[]? buf, int len, Action<byte[], int>? cb)
        {
            lock (_lock)
            {
                if (seq == _nextSeq)
                {
                    if (buf != null && cb != null)
                        cb(buf, len);
                    _nextSeq++;
                    FlushPending();
                }
                else
                {
                    byte[]? copy = null;
                    if (buf != null && len > 0)
                    {
                        copy = new byte[len];
                        Buffer.BlockCopy(buf, 0, copy, 0, len);
                    }
                    int slot = (int)((uint)seq % (uint)_slots.Length);
                    _slots[slot]    = (copy, len, cb);
                    _slotSeqs[slot] = seq;
                }
            }
        }

        private void FlushPending()
        {
            while (true)
            {
                int slot = (int)((uint)_nextSeq % (uint)_slots.Length);
                if (_slotSeqs[slot] != _nextSeq) break;
                var (b, l, savedCb) = _slots[slot];
                _slots[slot]    = default;
                _slotSeqs[slot] = -1;
                if (b != null && savedCb != null)
                    savedCb(b, l);
                _nextSeq++;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ExactResolutionFilter
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class ExactResolutionFilter : Java.Lang.Object, IResolutionFilter
    {
        private readonly global::Android.Util.Size _target;
        public ExactResolutionFilter(global::Android.Util.Size target) => _target = target;

        public IList<global::Android.Util.Size> Filter(
            IList<global::Android.Util.Size> supportedSizes, int rotationDegrees)
        {
            var exact = supportedSizes
                .FirstOrDefault(s => s.Width == _target.Width && s.Height == _target.Height);
            if (exact == null)
                return supportedSizes;
            var result = new List<global::Android.Util.Size> { exact };
            result.AddRange(supportedSizes.Where(s => s != exact));
            return result;
        }
    }
}
