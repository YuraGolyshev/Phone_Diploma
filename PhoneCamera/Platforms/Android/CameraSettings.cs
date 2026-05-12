using Android.Hardware.Camera2;
using AndroidX.Camera.Camera2.InterOp;
using Java.Lang;

namespace PhoneCamera.Platforms.Android;

// ╔═══════════════════════════════════════════════════════════════════════════╗
// ║                    НАСТРОЙКИ КАМЕРЫ И ЭНКОДЕРА                            ║
// ║                                                                           ║
// ║  Тыкай только const поля ниже. Все pipeline'ы (JPEG / H.264 / будущий     ║
// ║  третий) применяют ОДНИ И ТЕ ЖЕ значения через ApplyToBuilder /           ║
// ║  ApplyToExtender. Изменения подхватываются при следующем StartCamera      ║
// ║  (нажми Stop → Start или поменяй разрешение/fps в шторке).                ║
// ║                                                                           ║
// ║  Исходники, в которых этот класс используется:                            ║
// ║    • H264EncoderPipeline.cs  — H.264 ветка (Camera2 + MediaCodec)         ║
// ║    • CameraPreviewHandler.cs — JPEG ветка (CameraX + Camera2Interop)      ║
// ╚═══════════════════════════════════════════════════════════════════════════╝
public static class CameraSettings
{
    // ── Шаблон CaptureRequest ──────────────────────────────────────────────
    // Влияет на дефолтные значения, которые HAL подставляет в запрос. Мы их
    // потом всё равно переопределяем, но шаблон HAL также использует как hint
    // для выбора внутреннего sensor pipeline (CONTROL_CAPTURE_INTENT).
    //   CameraTemplate.Preview        — лёгкий путь, минимум обработки.
    //                                   На многих HAL разблокирует 60 fps.
    //                                   Качество чуть хуже чем у Record.
    //   CameraTemplate.Record         — pipeline для записи. На некоторых HAL
    //                                   включает HDR/EIS, что лочит sensor
    //                                   на 30 fps. Качество выше.
    //   CameraTemplate.VideoSnapshot  — гибрид (preview + still).
    //   ВНИМАНИЕ: используется только в H.264 ветке. CameraX (JPEG) сам
    //   выбирает шаблон под use case ImageAnalysis.
    public const CameraTemplate CAPTURE_TEMPLATE = CameraTemplate.Preview;

    // ── 3A (auto-exposure / auto-white-balance / auto-focus) ───────────────
    // Включаем для нормального изображения. Без 3A картинка тёмная, блёклая
    // и без фокуса.
    //   ENABLE_3A = true   — AE/AWB/AF автоматические (РЕКОМЕНДУЕТСЯ).
    //   ENABLE_3A = false  — ручной режим, ниже фиксированные значения
    //                        MANUAL_EXPOSURE_NS / MANUAL_ISO применятся.
    public const bool ENABLE_3A = true;

    // Только если ENABLE_3A=false — ручные значения для сенсора.
    // Влияют только если выключили автоматику (для эксперимента/закреплённой
    // сцены/принудительного 60 fps на проблемном HAL).
    public const long MANUAL_EXPOSURE_NS = 16_666_666;   // 1/60 сек
    public const int  MANUAL_ISO         = 800;

    // ── Видео-стабилизация (EIS) ───────────────────────────────────────────
    // EIS обрезает кадр, добавляет latency и на ряде HAL лочит sensor 30 fps.
    // Если изображение качается — включить.
    //   true  — стабилизация ON
    //   false — без стабилизации (по умолчанию)
    public const bool ENABLE_VIDEO_STABILIZATION = false;

    // ── Image processing pipeline (NR / Edge / Tonemap) ────────────────────
    // Уровень внутри-HAL обработки. На многих HAL HIGH_QUALITY = двухпроходная
    // обработка → sensor лочится на 30 fps. FAST — однопроходная, разблокирует
    // 60 fps на чувствительных устройствах, но картинка чуть менее «отполирована».
    //   0 = OFF             — без обработки (грязное, шумное)
    //   1 = FAST            — быстрая (РЕКОМЕНДУЕТСЯ для 60 fps)
    //   2 = HIGH_QUALITY    — качественная (может ронять fps)
    //   3 = MINIMAL / 4=ZSL — спецрежимы, обычно не нужны
    public const int NOISE_REDUCTION_MODE  = 1;    // FAST
    public const int EDGE_MODE             = 1;    // FAST
    public const int TONEMAP_MODE          = 1;    // FAST
    public const int COLOR_ABERRATION_MODE = 1;    // FAST
    public const int HOT_PIXEL_MODE        = 1;    // FAST

    // ── H.264 кодирование (используется только в H.264 ветке) ──────────────
    // KEY_I_FRAME_INTERVAL — как часто encoder вставляет ключевой кадр (IDR).
    // Меньше = быстрее восстановление при потерях, больше = меньше bitrate.
    //   1 сек  = быстрая reconnection (РЕКОМЕНДУЕТСЯ для live-стрима)
    //   2-5    = меньше bitrate, дольше «приход в себя» при потере пакета
    public const int H264_I_FRAME_INTERVAL_SECONDS = 1;

    // KEY_BIT_RATE считается автоматически по ширине×высоте×fps в
    // H264EncoderPipeline.ChooseBitrate. Для своего значения — поправь
    // прямо там. KEY_OPERATING_RATE / KEY_LOW_LATENCY включаем всегда —
    // это hint энкодеру «работай быстро», на качество не влияет.

    // ╚═════════════════════ конец полей-настроек ══════════════════════════╝

    // ── Применение настроек к Camera2 CaptureRequest.Builder ───────────────
    // Используется в H.264 pipeline:
    //   1) per-frame в repeating request (с каждым кадром)
    //   2) в session parameters (HAL читает при выборе sensor mode)
    // Оба места должны иметь идентичные значения по ключам, иначе кадры
    // будут отбрасываться (требование Camera2).
    public static void ApplyToBuilder(CaptureRequest.Builder b, int fps)
    {
        if (ENABLE_3A)
        {
            // CONTROL_MODE = AUTO (1): включает все 3A алгоритмы
            b.Set(CaptureRequest.ControlMode,    Integer.ValueOf(1));
            // AE_MODE = ON (1): автоматическая экспозиция
            b.Set(CaptureRequest.ControlAeMode,  Integer.ValueOf(1));
            // AWB_MODE = AUTO (1): автоматический баланс белого
            b.Set(CaptureRequest.ControlAwbMode, Integer.ValueOf(1));
            // AF_MODE = CONTINUOUS_VIDEO (3): непрерывная фокусировка для видео
            b.Set(CaptureRequest.ControlAfMode,  Integer.ValueOf(3));
        }
        else
        {
            // CONTROL_MODE = OFF (0): полностью ручной режим
            b.Set(CaptureRequest.ControlMode,    Integer.ValueOf(0));
            b.Set(CaptureRequest.ControlAeMode,  Integer.ValueOf(0));
            b.Set(CaptureRequest.ControlAwbMode, Integer.ValueOf(0));
            b.Set(CaptureRequest.ControlAfMode,  Integer.ValueOf(0));

            long frameDurationNs = 1_000_000_000L / fps;
            b.Set(CaptureRequest.SensorExposureTime,
                  Long.ValueOf(System.Math.Min(MANUAL_EXPOSURE_NS, frameDurationNs)));
            b.Set(CaptureRequest.SensorFrameDuration, Long.ValueOf(frameDurationNs));
            b.Set(CaptureRequest.SensorSensitivity,   Integer.ValueOf(MANUAL_ISO));
        }

        // SCENE_MODE: всегда выключаем (DISABLED=0). HAL мог бы навязать какой-нибудь
        // «night» / «portrait» режим — нам не нужно.
        b.Set(CaptureRequest.ControlSceneMode, Integer.ValueOf(0));

        // Видео-стабилизация
        b.Set(CaptureRequest.ControlVideoStabilizationMode,
              Integer.ValueOf(ENABLE_VIDEO_STABILIZATION ? 1 : 0));

        // Image-processing
        b.Set(CaptureRequest.NoiseReductionMode,            Integer.ValueOf(NOISE_REDUCTION_MODE));
        b.Set(CaptureRequest.EdgeMode,                      Integer.ValueOf(EDGE_MODE));
        b.Set(CaptureRequest.TonemapMode,                   Integer.ValueOf(TONEMAP_MODE));
        b.Set(CaptureRequest.ColorCorrectionAberrationMode, Integer.ValueOf(COLOR_ABERRATION_MODE));
        b.Set(CaptureRequest.HotPixelMode,                  Integer.ValueOf(HOT_PIXEL_MODE));

        // Статистика — выключаем (нагружает HAL, мы её не читаем)
        b.Set(CaptureRequest.StatisticsFaceDetectMode,     Integer.ValueOf(0));
        b.Set(CaptureRequest.StatisticsHotPixelMapMode,    Java.Lang.Boolean.False);
        b.Set(CaptureRequest.StatisticsLensShadingMapMode, Integer.ValueOf(0));
        b.Set(CaptureRequest.BlackLevelLock,               Java.Lang.Boolean.False);

        // Целевой FPS для AE-подсистемы и для выбора sensor mode HAL'ом.
        b.Set(CaptureRequest.ControlAeTargetFpsRange,
              new global::Android.Util.Range(Integer.ValueOf(fps), Integer.ValueOf(fps)));
    }

    // ── Применение настроек к CameraX через Camera2Interop.Extender ────────
    // Используется в JPEG pipeline (CameraPreviewHandler). API extender'а
    // отличается от builder'а (метод SetCaptureRequestOption вместо Set), но
    // Capture-keys те же. Дублируем вызовы один-в-один, чтобы значения
    // совпадали с H.264 веткой.
    public static void ApplyToExtender(Camera2Interop.Extender extender, int fps)
    {
        if (ENABLE_3A)
        {
            extender.SetCaptureRequestOption(CaptureRequest.ControlMode,    Integer.ValueOf(1));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAeMode,  Integer.ValueOf(1));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAwbMode, Integer.ValueOf(1));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAfMode,  Integer.ValueOf(3));
        }
        else
        {
            extender.SetCaptureRequestOption(CaptureRequest.ControlMode,    Integer.ValueOf(0));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAeMode,  Integer.ValueOf(0));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAwbMode, Integer.ValueOf(0));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAfMode,  Integer.ValueOf(0));

            long frameDurationNs = 1_000_000_000L / fps;
            extender.SetCaptureRequestOption(CaptureRequest.SensorExposureTime,
                Long.ValueOf(System.Math.Min(MANUAL_EXPOSURE_NS, frameDurationNs)));
            extender.SetCaptureRequestOption(CaptureRequest.SensorFrameDuration,
                Long.ValueOf(frameDurationNs));
            extender.SetCaptureRequestOption(CaptureRequest.SensorSensitivity,
                Integer.ValueOf(MANUAL_ISO));
        }

        extender.SetCaptureRequestOption(CaptureRequest.ControlSceneMode, Integer.ValueOf(0));
        extender.SetCaptureRequestOption(CaptureRequest.ControlVideoStabilizationMode,
            Integer.ValueOf(ENABLE_VIDEO_STABILIZATION ? 1 : 0));

        extender.SetCaptureRequestOption(CaptureRequest.NoiseReductionMode,
            Integer.ValueOf(NOISE_REDUCTION_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.EdgeMode,
            Integer.ValueOf(EDGE_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.TonemapMode,
            Integer.ValueOf(TONEMAP_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.ColorCorrectionAberrationMode,
            Integer.ValueOf(COLOR_ABERRATION_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.HotPixelMode,
            Integer.ValueOf(HOT_PIXEL_MODE));

        extender.SetCaptureRequestOption(CaptureRequest.StatisticsFaceDetectMode,
            Integer.ValueOf(0));
        extender.SetCaptureRequestOption(CaptureRequest.StatisticsHotPixelMapMode,
            Java.Lang.Boolean.False);
        extender.SetCaptureRequestOption(CaptureRequest.StatisticsLensShadingMapMode,
            Integer.ValueOf(0));
        extender.SetCaptureRequestOption(CaptureRequest.BlackLevelLock,
            Java.Lang.Boolean.False);

        extender.SetCaptureRequestOption(CaptureRequest.ControlAeTargetFpsRange,
            new global::Android.Util.Range(Integer.ValueOf(fps), Integer.ValueOf(fps)));
    }

    // ── Минимальный набор для CameraX / JPEG-ветки ────────────────────────
    // Выставляет ТОЛЬКО AE target FPS range. Всё остальное — image-processing,
    // AF mode, scene mode, статистика — оставляем CameraX'у и HAL'у дефолтным.
    // ApplyToExtender (полный) корректен для прямого Camera2, но при подаче в
    // CameraX через Camera2Interop часть ключей конфликтует с CameraX'овым
    // выбором sensor mode для use case ImageAnalysis → HAL фоллбэчит на
    // меньший fps (на 1080p мы теряли честные 30 fps). Минимальный набор
    // фиксирует только частоту, и HAL свободно выбирает оптимальный pipeline.
    public static void ApplyJpegMinimal(Camera2Interop.Extender extender, int fps)
    {
        extender.SetCaptureRequestOption(CaptureRequest.ControlAeTargetFpsRange,
            new global::Android.Util.Range(Integer.ValueOf(fps), Integer.ValueOf(fps)));
    }
}
