using Android.Hardware.Camera2;
using AndroidX.Camera.Camera2.InterOp;
using Java.Lang;
using Microsoft.Maui.Storage;
using PhoneCamera.Settings;

namespace PhoneCamera.Platforms.Android;

// ╔═══════════════════════════════════════════════════════════════════════════╗
// ║                    НАСТРОЙКИ КАМЕРЫ И ЭНКОДЕРА                            ║
// ║                                                                           ║
// ║  Раньше тут были const поля — теперь свойства, читающие/пишущие в         ║
// ║  Preferences (Microsoft.Maui.Storage). Меняются из UI SettingsPage,       ║
// ║  применяются при следующем StartCamera.                                   ║
// ║                                                                           ║
// ║  Применители значений:                                                    ║
// ║    • ApplyToBuilder  — H.264/H.265 ветка (Camera2 direct)                 ║
// ║    • ApplyJpegFull   — JPEG ветка (CameraX через Camera2Interop)         ║
// ║                                                                           ║
// ║  Сохраняются backward-compat алиасы (CAPTURE_TEMPLATE и т.д.), чтобы      ║
// ║  существующий код продолжал компилироваться без изменений.                ║
// ╚═══════════════════════════════════════════════════════════════════════════╝
public static class CameraSettings
{
    // ── Preferences keys ───────────────────────────────────────────────────
    private const string K_ExposureMode             = "cs_exposure_mode";
    private const string K_EvCompensation           = "cs_ev_compensation";
    private const string K_AeLockEnabled            = "cs_ae_lock";
    private const string K_ManualExposureNs         = "cs_manual_exposure_ns";
    private const string K_ManualIso                = "cs_manual_iso";
    private const string K_AntiBanding              = "cs_anti_banding";
    private const string K_AwbMode                  = "cs_awb_mode";
    private const string K_AwbLockEnabled           = "cs_awb_lock";
    private const string K_AfMode                   = "cs_af_mode";
    private const string K_ManualFocusDistance      = "cs_manual_focus_distance";
    private const string K_NoiseReduction           = "cs_noise_reduction";
    private const string K_Edge                     = "cs_edge";
    private const string K_Tonemap                  = "cs_tonemap";
    private const string K_ColorAberration          = "cs_color_aberration";
    private const string K_HotPixel                 = "cs_hot_pixel";
    private const string K_VideoStabilization       = "cs_video_stabilization";
    private const string K_ZoomLevel                = "cs_zoom_level";
    private const string K_FlashMode                = "cs_flash_mode";
    private const string K_CaptureTemplate          = "cs_capture_template";
    private const string K_IFrameInterval           = "cs_iframe_interval";
    private const string K_H264BitsPerPixel         = "cs_h264_bpp";
    private const string K_H265BitsPerPixel         = "cs_h265_bpp";
    private const string K_BitrateCapBps            = "cs_bitrate_cap";
    private const string K_BitrateMinBps            = "cs_bitrate_min";
    private const string K_AdaptiveEnabled          = "cs_adaptive_enabled";
    private const string K_AdaptiveWindowMs         = "cs_adaptive_window_ms";
    private const string K_AdaptiveBpThreshold      = "cs_adaptive_bp_threshold";
    private const string K_AdaptiveClearThreshold   = "cs_adaptive_clear_threshold";
    private const string K_AdaptiveClearWindows     = "cs_adaptive_clear_windows";
    private const string K_AdaptiveDecreaseFactor   = "cs_adaptive_decrease";
    private const string K_AdaptiveIncreaseFactor   = "cs_adaptive_increase";
    private const string K_JpegQuality              = "cs_jpeg_quality";

    // ── Defaults (стартовые значения «из коробки») ─────────────────────────
    public const ExposureModeSetting     DEF_ExposureMode              = ExposureModeSetting.Auto;
    public const int                     DEF_EvCompensation            = 0;       // ступенях AE compensation
    public const bool                    DEF_AeLockEnabled             = false;
    public const long                    DEF_ManualExposureNs          = 16_666_666L;  // 1/60 сек
    public const int                     DEF_ManualIso                 = 800;
    public const AntiBandingSetting      DEF_AntiBanding               = AntiBandingSetting.Auto;
    public const AwbModeSetting          DEF_AwbMode                   = AwbModeSetting.Auto;
    public const bool                    DEF_AwbLockEnabled            = false;
    public const AfModeSetting           DEF_AfMode                    = AfModeSetting.ContinuousVideo;
    public const float                   DEF_ManualFocusDistance       = 0f;       // диоптрий, 0 = бесконечность
    public const NoiseReductionSetting   DEF_NoiseReduction            = NoiseReductionSetting.Fast;
    public const EdgeSetting             DEF_Edge                      = EdgeSetting.Fast;
    public const TonemapSetting          DEF_Tonemap                   = TonemapSetting.Fast;
    public const SimpleProcessingSetting DEF_ColorAberration           = SimpleProcessingSetting.Fast;
    public const SimpleProcessingSetting DEF_HotPixel                  = SimpleProcessingSetting.Fast;
    public const bool                    DEF_VideoStabilization        = false;
    public const float                   DEF_ZoomLevel                 = 1.0f;
    public const FlashSetting            DEF_Flash                     = FlashSetting.Off;
    public const CaptureTemplateSetting  DEF_CaptureTemplate           = CaptureTemplateSetting.Preview;
    public const int                     DEF_IFrameInterval            = 1;        // секунд
    public const double                  DEF_H264BitsPerPixel          = 0.083;
    public const double                  DEF_H265BitsPerPixel          = 0.083;
    public const int                     DEF_BitrateCapBps             = 25_000_000;
    public const int                     DEF_BitrateMinBps             = 2_000_000;
    public const bool                    DEF_AdaptiveEnabled           = true;
    public const int                     DEF_AdaptiveWindowMs          = 2000;
    public const double                  DEF_AdaptiveBpThreshold       = 0.05;
    public const double                  DEF_AdaptiveClearThreshold    = 0.01;
    public const int                     DEF_AdaptiveClearWindows      = 3;
    public const double                  DEF_AdaptiveDecreaseFactor    = 0.85;
    public const double                  DEF_AdaptiveIncreaseFactor    = 1.10;
    public const int                     DEF_JpegQuality               = 25;

    // ── Свойства (читают/пишут Preferences) ────────────────────────────────
    // ── Экспозиция и сенсор ────────────────────────────────────────────────
    public static ExposureModeSetting ExposureMode
    {
        get => (ExposureModeSetting)Preferences.Get(K_ExposureMode, (int)DEF_ExposureMode);
        set => Preferences.Set(K_ExposureMode, (int)value);
    }
    /// <summary>Шаги AE compensation. Реальный диапазон/шаг берётся из CameraCharacteristics
    /// CONTROL_AE_COMPENSATION_RANGE и CONTROL_AE_COMPENSATION_STEP — UI преобразует
    /// "0.5 EV" в число шагов исходя из step.</summary>
    public static int EvCompensation
    {
        get => Preferences.Get(K_EvCompensation, DEF_EvCompensation);
        set => Preferences.Set(K_EvCompensation, value);
    }
    public static bool AeLockEnabled
    {
        get => Preferences.Get(K_AeLockEnabled, DEF_AeLockEnabled);
        set => Preferences.Set(K_AeLockEnabled, value);
    }
    public static long ManualExposureNs
    {
        get => (long)Preferences.Get(K_ManualExposureNs, (double)DEF_ManualExposureNs);
        set => Preferences.Set(K_ManualExposureNs, (double)value);
    }
    public static int ManualIso
    {
        get => Preferences.Get(K_ManualIso, DEF_ManualIso);
        set => Preferences.Set(K_ManualIso, value);
    }
    public static AntiBandingSetting AntiBanding
    {
        get => (AntiBandingSetting)Preferences.Get(K_AntiBanding, (int)DEF_AntiBanding);
        set => Preferences.Set(K_AntiBanding, (int)value);
    }

    // ── Баланс белого ──────────────────────────────────────────────────────
    public static AwbModeSetting WhiteBalanceMode
    {
        get => (AwbModeSetting)Preferences.Get(K_AwbMode, (int)DEF_AwbMode);
        set => Preferences.Set(K_AwbMode, (int)value);
    }
    public static bool AwbLockEnabled
    {
        get => Preferences.Get(K_AwbLockEnabled, DEF_AwbLockEnabled);
        set => Preferences.Set(K_AwbLockEnabled, value);
    }

    // ── Фокус ──────────────────────────────────────────────────────────────
    public static AfModeSetting FocusMode
    {
        get => (AfModeSetting)Preferences.Get(K_AfMode, (int)DEF_AfMode);
        set => Preferences.Set(K_AfMode, (int)value);
    }
    public static float ManualFocusDistance
    {
        get => Preferences.Get(K_ManualFocusDistance, DEF_ManualFocusDistance);
        set => Preferences.Set(K_ManualFocusDistance, value);
    }

    // ── Обработка изображения ──────────────────────────────────────────────
    public static NoiseReductionSetting NoiseReduction
    {
        get => (NoiseReductionSetting)Preferences.Get(K_NoiseReduction, (int)DEF_NoiseReduction);
        set => Preferences.Set(K_NoiseReduction, (int)value);
    }
    public static EdgeSetting EdgeEnhancement
    {
        get => (EdgeSetting)Preferences.Get(K_Edge, (int)DEF_Edge);
        set => Preferences.Set(K_Edge, (int)value);
    }
    public static TonemapSetting Tonemap
    {
        get => (TonemapSetting)Preferences.Get(K_Tonemap, (int)DEF_Tonemap);
        set => Preferences.Set(K_Tonemap, (int)value);
    }
    public static SimpleProcessingSetting ColorAberration
    {
        get => (SimpleProcessingSetting)Preferences.Get(K_ColorAberration, (int)DEF_ColorAberration);
        set => Preferences.Set(K_ColorAberration, (int)value);
    }
    public static SimpleProcessingSetting HotPixel
    {
        get => (SimpleProcessingSetting)Preferences.Get(K_HotPixel, (int)DEF_HotPixel);
        set => Preferences.Set(K_HotPixel, (int)value);
    }
    public static bool VideoStabilization
    {
        get => Preferences.Get(K_VideoStabilization, DEF_VideoStabilization);
        set => Preferences.Set(K_VideoStabilization, value);
    }

    // ── Кадр ───────────────────────────────────────────────────────────────
    public static float ZoomLevel
    {
        get => Preferences.Get(K_ZoomLevel, DEF_ZoomLevel);
        set => Preferences.Set(K_ZoomLevel, value);
    }

    // ── Вспышка ────────────────────────────────────────────────────────────
    public static FlashSetting Flash
    {
        get => (FlashSetting)Preferences.Get(K_FlashMode, (int)DEF_Flash);
        set => Preferences.Set(K_FlashMode, (int)value);
    }

    // ── Кодирование (H.264 / H.265) ────────────────────────────────────────
    public static CaptureTemplateSetting CaptureTemplate
    {
        get => (CaptureTemplateSetting)Preferences.Get(K_CaptureTemplate, (int)DEF_CaptureTemplate);
        set => Preferences.Set(K_CaptureTemplate, (int)value);
    }
    public static int IFrameIntervalSeconds
    {
        get => Preferences.Get(K_IFrameInterval, DEF_IFrameInterval);
        set => Preferences.Set(K_IFrameInterval, value);
    }
    public static double H264BitsPerPixel
    {
        get => Preferences.Get(K_H264BitsPerPixel, DEF_H264BitsPerPixel);
        set => Preferences.Set(K_H264BitsPerPixel, value);
    }
    public static double H265BitsPerPixel
    {
        get => Preferences.Get(K_H265BitsPerPixel, DEF_H265BitsPerPixel);
        set => Preferences.Set(K_H265BitsPerPixel, value);
    }
    public static int BitrateCapBps
    {
        get => Preferences.Get(K_BitrateCapBps, DEF_BitrateCapBps);
        set => Preferences.Set(K_BitrateCapBps, value);
    }
    public static int BitrateMinBps
    {
        get => Preferences.Get(K_BitrateMinBps, DEF_BitrateMinBps);
        set => Preferences.Set(K_BitrateMinBps, value);
    }

    // ── Адаптивный битрейт ─────────────────────────────────────────────────
    public static bool AdaptiveBitrateEnabled
    {
        get => Preferences.Get(K_AdaptiveEnabled, DEF_AdaptiveEnabled);
        set => Preferences.Set(K_AdaptiveEnabled, value);
    }
    public static int AdaptiveWindowMs
    {
        get => Preferences.Get(K_AdaptiveWindowMs, DEF_AdaptiveWindowMs);
        set => Preferences.Set(K_AdaptiveWindowMs, value);
    }
    public static double AdaptiveBackpressureThreshold
    {
        get => Preferences.Get(K_AdaptiveBpThreshold, DEF_AdaptiveBpThreshold);
        set => Preferences.Set(K_AdaptiveBpThreshold, value);
    }
    public static double AdaptiveClearThreshold
    {
        get => Preferences.Get(K_AdaptiveClearThreshold, DEF_AdaptiveClearThreshold);
        set => Preferences.Set(K_AdaptiveClearThreshold, value);
    }
    public static int AdaptiveClearWindowsToRaise
    {
        get => Preferences.Get(K_AdaptiveClearWindows, DEF_AdaptiveClearWindows);
        set => Preferences.Set(K_AdaptiveClearWindows, value);
    }
    public static double AdaptiveDecreaseFactor
    {
        get => Preferences.Get(K_AdaptiveDecreaseFactor, DEF_AdaptiveDecreaseFactor);
        set => Preferences.Set(K_AdaptiveDecreaseFactor, value);
    }
    public static double AdaptiveIncreaseFactor
    {
        get => Preferences.Get(K_AdaptiveIncreaseFactor, DEF_AdaptiveIncreaseFactor);
        set => Preferences.Set(K_AdaptiveIncreaseFactor, value);
    }

    // ── JPEG-кодирование ───────────────────────────────────────────────────
    /// <summary>Качество JPEG (1..100). Чем выше — меньше артефактов и крупнее файл.
    /// Применяется в YuvImage.CompressToJpeg на каждом кадре в JPEG-режиме.</summary>
    public static int JpegQuality
    {
        get => Preferences.Get(K_JpegQuality, DEF_JpegQuality);
        set => Preferences.Set(K_JpegQuality, System.Math.Clamp(value, 1, 100));
    }

    // ── Сброс к значениям по умолчанию ─────────────────────────────────────
    public static void ResetToDefaults()
    {
        Preferences.Remove(K_ExposureMode);
        Preferences.Remove(K_EvCompensation);
        Preferences.Remove(K_AeLockEnabled);
        Preferences.Remove(K_ManualExposureNs);
        Preferences.Remove(K_ManualIso);
        Preferences.Remove(K_AntiBanding);
        Preferences.Remove(K_AwbMode);
        Preferences.Remove(K_AwbLockEnabled);
        Preferences.Remove(K_AfMode);
        Preferences.Remove(K_ManualFocusDistance);
        Preferences.Remove(K_NoiseReduction);
        Preferences.Remove(K_Edge);
        Preferences.Remove(K_Tonemap);
        Preferences.Remove(K_ColorAberration);
        Preferences.Remove(K_HotPixel);
        Preferences.Remove(K_VideoStabilization);
        Preferences.Remove(K_ZoomLevel);
        Preferences.Remove(K_FlashMode);
        Preferences.Remove(K_CaptureTemplate);
        Preferences.Remove(K_IFrameInterval);
        Preferences.Remove(K_H264BitsPerPixel);
        Preferences.Remove(K_H265BitsPerPixel);
        Preferences.Remove(K_BitrateCapBps);
        Preferences.Remove(K_BitrateMinBps);
        Preferences.Remove(K_AdaptiveEnabled);
        Preferences.Remove(K_AdaptiveWindowMs);
        Preferences.Remove(K_AdaptiveBpThreshold);
        Preferences.Remove(K_AdaptiveClearThreshold);
        Preferences.Remove(K_AdaptiveClearWindows);
        Preferences.Remove(K_AdaptiveDecreaseFactor);
        Preferences.Remove(K_AdaptiveIncreaseFactor);
        Preferences.Remove(K_JpegQuality);
    }

    // ╔══════════════ Backward-compat aliases для существующего кода ═══════╗
    // Старые имена-константы из const-эпохи. Их используют H264EncoderPipeline,
    // CameraPreviewHandler и места в этом файле ниже. Возвращают актуальное
    // значение свойства из Preferences.
    public static CameraTemplate CAPTURE_TEMPLATE => CaptureTemplate switch
    {
        CaptureTemplateSetting.Record        => CameraTemplate.Record,
        CaptureTemplateSetting.VideoSnapshot => CameraTemplate.VideoSnapshot,
        _                                    => CameraTemplate.Preview,
    };
    public static bool   ENABLE_3A                    => ExposureMode == ExposureModeSetting.Auto;
    public static long   MANUAL_EXPOSURE_NS           => ManualExposureNs;
    public static int    MANUAL_ISO                   => ManualIso;
    public static bool   ENABLE_VIDEO_STABILIZATION   => VideoStabilization;
    public static int    NOISE_REDUCTION_MODE         => (int)NoiseReduction;
    public static int    EDGE_MODE                    => (int)EdgeEnhancement;
    public static int    TONEMAP_MODE                 => (int)Tonemap;
    public static int    COLOR_ABERRATION_MODE        => (int)ColorAberration;
    public static int    HOT_PIXEL_MODE               => (int)HotPixel;
    public static int    H264_I_FRAME_INTERVAL_SECONDS=> IFrameIntervalSeconds;
    public static double H264_BITS_PER_PIXEL          => H264BitsPerPixel;
    public static double H265_BITS_PER_PIXEL          => H265BitsPerPixel;
    public static int    BITRATE_CAP_BPS              => BitrateCapBps;
    public static int    BITRATE_MIN_BPS              => BitrateMinBps;
    public static bool   ADAPTIVE_BITRATE_ENABLED     => AdaptiveBitrateEnabled;
    public static int    ADAPTIVE_WINDOW_MS           => AdaptiveWindowMs;
    public static double ADAPTIVE_BACKPRESSURE_THRESHOLD => AdaptiveBackpressureThreshold;
    public static double ADAPTIVE_CLEAR_THRESHOLD     => AdaptiveClearThreshold;
    public static int    ADAPTIVE_CLEAR_WINDOWS_TO_RAISE => AdaptiveClearWindowsToRaise;
    public static double ADAPTIVE_DECREASE_FACTOR     => AdaptiveDecreaseFactor;
    public static double ADAPTIVE_INCREASE_FACTOR     => AdaptiveIncreaseFactor;

    // ╔══════════════ Helpers ══════════════════════════════════════════════╗

    // FLASH_MODE значение для CaptureRequest. AE-режим выставляется отдельно ниже.
    private static int FlashToFlashMode() => Flash switch
    {
        FlashSetting.Torch => 2,   // FLASH_MODE_TORCH
        FlashSetting.Single
            or FlashSetting.Auto => 1, // FLASH_MODE_SINGLE
        _                       => 0,  // FLASH_MODE_OFF
    };

    // CONTROL_AE_MODE значение в зависимости от Flash:
    //   OFF (manual) → 0
    //   Flash.Off    → 1 (ON)
    //   Flash.Auto   → 2 (ON_AUTO_FLASH)
    //   Flash.Single → 3 (ON_ALWAYS_FLASH)
    //   Flash.Torch  → 1 (ON), torch управляется FLASH_MODE отдельно
    private static int AeModeForFlash(bool autoExposure)
    {
        if (!autoExposure) return 0;
        return Flash switch
        {
            FlashSetting.Auto   => 2,  // ON_AUTO_FLASH
            FlashSetting.Single => 3,  // ON_ALWAYS_FLASH
            _                   => 1,  // ON (Off, Torch)
        };
    }

    // ── Применение настроек к Camera2 CaptureRequest.Builder ───────────────
    // Используется в H.264/H.265 pipeline (H264EncoderPipeline.cs).
    public static void ApplyToBuilder(CaptureRequest.Builder b, int fps)
    {
        bool auto = ENABLE_3A;
        b.Set(CaptureRequest.ControlMode,    Integer.ValueOf(auto ? 1 : 0));
        b.Set(CaptureRequest.ControlAeMode,  Integer.ValueOf(AeModeForFlash(auto)));
        b.Set(CaptureRequest.ControlAwbMode, Integer.ValueOf(auto ? (int)WhiteBalanceMode : 0));

        // AF_MODE: при auto = выбор из FocusMode (continuous video / picture / auto / off);
        // при manual экспозиции отдельно решает FocusMode == Manual (применяем LENS_FOCUS_DISTANCE).
        int afMode = FocusMode == AfModeSetting.Manual ? 0 : (int)FocusMode;
        b.Set(CaptureRequest.ControlAfMode, Integer.ValueOf(afMode));
        if (FocusMode == AfModeSetting.Manual)
        {
            b.Set(CaptureRequest.LensFocusDistance, Float.ValueOf(ManualFocusDistance));
        }

        if (auto)
        {
            b.Set(CaptureRequest.ControlAeExposureCompensation, Integer.ValueOf(EvCompensation));
            b.Set(CaptureRequest.ControlAeLock,  Java.Lang.Boolean.ValueOf(AeLockEnabled));
            b.Set(CaptureRequest.ControlAwbLock, Java.Lang.Boolean.ValueOf(AwbLockEnabled));
        }
        else
        {
            long frameDurationNs = 1_000_000_000L / fps;
            b.Set(CaptureRequest.SensorExposureTime,
                  Long.ValueOf(System.Math.Min(MANUAL_EXPOSURE_NS, frameDurationNs)));
            b.Set(CaptureRequest.SensorFrameDuration, Long.ValueOf(frameDurationNs));
            b.Set(CaptureRequest.SensorSensitivity,   Integer.ValueOf(MANUAL_ISO));
        }

        // Anti-banding (50/60 Hz фликкер)
        b.Set(CaptureRequest.ControlAeAntibandingMode, Integer.ValueOf((int)AntiBanding));

        // SCENE_MODE всегда DISABLED — не нужны HAL'овские пресеты.
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

        // Flash
        b.Set(CaptureRequest.FlashMode, Integer.ValueOf(FlashToFlashMode()));

        // Zoom (CONTROL_ZOOM_RATIO появилось в Android 11 / API 30)
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R)
        {
            try { b.Set(CaptureRequest.ControlZoomRatio, Float.ValueOf(ZoomLevel)); } catch { }
        }

        // Статистика — выключаем
        b.Set(CaptureRequest.StatisticsFaceDetectMode,     Integer.ValueOf(0));
        b.Set(CaptureRequest.StatisticsHotPixelMapMode,    Java.Lang.Boolean.False);
        b.Set(CaptureRequest.StatisticsLensShadingMapMode, Integer.ValueOf(0));
        b.Set(CaptureRequest.BlackLevelLock,               Java.Lang.Boolean.False);

        // AE target FPS range — фиксируем частоту
        b.Set(CaptureRequest.ControlAeTargetFpsRange,
              new global::Android.Util.Range(Integer.ValueOf(fps), Integer.ValueOf(fps)));
    }

    // ── Применение к CameraX через Camera2Interop.Extender (JPEG pipeline) ─
    // Идентично ApplyToBuilder, только API extender'а вместо CaptureRequest.Builder.
    public static void ApplyJpegFull(Camera2Interop.Extender extender, int fps)
    {
        bool auto = ENABLE_3A;
        extender.SetCaptureRequestOption(CaptureRequest.ControlMode,    Integer.ValueOf(auto ? 1 : 0));
        extender.SetCaptureRequestOption(CaptureRequest.ControlAeMode,  Integer.ValueOf(AeModeForFlash(auto)));
        extender.SetCaptureRequestOption(CaptureRequest.ControlAwbMode, Integer.ValueOf(auto ? (int)WhiteBalanceMode : 0));

        int afMode = FocusMode == AfModeSetting.Manual ? 0 : (int)FocusMode;
        extender.SetCaptureRequestOption(CaptureRequest.ControlAfMode, Integer.ValueOf(afMode));
        if (FocusMode == AfModeSetting.Manual)
        {
            extender.SetCaptureRequestOption(CaptureRequest.LensFocusDistance, Float.ValueOf(ManualFocusDistance));
        }

        if (auto)
        {
            extender.SetCaptureRequestOption(CaptureRequest.ControlAeExposureCompensation, Integer.ValueOf(EvCompensation));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAeLock,  Java.Lang.Boolean.ValueOf(AeLockEnabled));
            extender.SetCaptureRequestOption(CaptureRequest.ControlAwbLock, Java.Lang.Boolean.ValueOf(AwbLockEnabled));
        }
        else
        {
            long frameDurationNs = 1_000_000_000L / fps;
            extender.SetCaptureRequestOption(CaptureRequest.SensorExposureTime,
                Long.ValueOf(System.Math.Min(MANUAL_EXPOSURE_NS, frameDurationNs)));
            extender.SetCaptureRequestOption(CaptureRequest.SensorFrameDuration,
                Long.ValueOf(frameDurationNs));
            extender.SetCaptureRequestOption(CaptureRequest.SensorSensitivity,
                Integer.ValueOf(MANUAL_ISO));
        }

        extender.SetCaptureRequestOption(CaptureRequest.ControlAeAntibandingMode, Integer.ValueOf((int)AntiBanding));
        extender.SetCaptureRequestOption(CaptureRequest.ControlSceneMode, Integer.ValueOf(0));
        extender.SetCaptureRequestOption(CaptureRequest.ControlVideoStabilizationMode,
            Integer.ValueOf(ENABLE_VIDEO_STABILIZATION ? 1 : 0));

        extender.SetCaptureRequestOption(CaptureRequest.NoiseReductionMode,            Integer.ValueOf(NOISE_REDUCTION_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.EdgeMode,                      Integer.ValueOf(EDGE_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.TonemapMode,                   Integer.ValueOf(TONEMAP_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.ColorCorrectionAberrationMode, Integer.ValueOf(COLOR_ABERRATION_MODE));
        extender.SetCaptureRequestOption(CaptureRequest.HotPixelMode,                  Integer.ValueOf(HOT_PIXEL_MODE));

        extender.SetCaptureRequestOption(CaptureRequest.FlashMode, Integer.ValueOf(FlashToFlashMode()));

        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R)
        {
            try { extender.SetCaptureRequestOption(CaptureRequest.ControlZoomRatio, Float.ValueOf(ZoomLevel)); } catch { }
        }

        extender.SetCaptureRequestOption(CaptureRequest.StatisticsFaceDetectMode,    Integer.ValueOf(0));
        extender.SetCaptureRequestOption(CaptureRequest.StatisticsHotPixelMapMode,   Java.Lang.Boolean.False);
        extender.SetCaptureRequestOption(CaptureRequest.StatisticsLensShadingMapMode,Integer.ValueOf(0));
        extender.SetCaptureRequestOption(CaptureRequest.BlackLevelLock,              Java.Lang.Boolean.False);

        extender.SetCaptureRequestOption(CaptureRequest.ControlAeTargetFpsRange,
            new global::Android.Util.Range(Integer.ValueOf(fps), Integer.ValueOf(fps)));
    }

    // Legacy alias: код вызывал ApplyJpegMinimal — теперь редиректится в ApplyJpegFull.
    public static void ApplyJpegMinimal(Camera2Interop.Extender extender, int fps)
        => ApplyJpegFull(extender, fps);
}
