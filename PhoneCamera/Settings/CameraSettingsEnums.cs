namespace PhoneCamera.Settings;

// ╔════════════════════════════════════════════════════════════════════╗
// ║   Типизированные enum'ы для CameraSettings и UI настроек.          ║
// ║   Числовые значения соответствуют Camera2 API константам, где это  ║
// ║   уместно — Android-сторона (CameraSettings.cs) может кастить       ║
// ║   (int) и сразу класть в CaptureRequest.*.                          ║
// ║                                                                     ║
// ║   Эти enum'ы — cross-platform; SettingsPage и Android CameraSettings║
// ║   используют общие типы.                                            ║
// ╚════════════════════════════════════════════════════════════════════╝

/// <summary>Auto vs ручной режим экспозиции / 3A.</summary>
public enum ExposureModeSetting
{
    Auto   = 1,   // CONTROL_MODE = AUTO
    Manual = 0,   // CONTROL_MODE = OFF
}

/// <summary>CONTROL_AWB_MODE (Camera2).</summary>
public enum AwbModeSetting
{
    Off              = 0,
    Auto             = 1,
    Incandescent     = 2,
    Fluorescent      = 3,
    WarmFluorescent  = 4,
    Daylight         = 5,
    CloudyDaylight   = 6,
    Twilight         = 7,
    Shade            = 8,
}

/// <summary>CONTROL_AF_MODE + псевдо-значение Manual (= AF_OFF + ручной LENS_FOCUS_DISTANCE).</summary>
public enum AfModeSetting
{
    Off               = 0,   // AF выкл, фокус как есть
    Auto              = 1,   // одноразовая авто-наводка
    Macro             = 2,
    ContinuousVideo   = 3,   // оптимум для стрима
    ContinuousPicture = 4,
    Manual            = 100, // не Camera2 — наш UI режим: AF_OFF + LENS_FOCUS_DISTANCE из ManualFocusDistance
}

/// <summary>CONTROL_AE_ANTIBANDING_MODE.</summary>
public enum AntiBandingSetting
{
    Off  = 0,
    Hz50 = 1,
    Hz60 = 2,
    Auto = 3,
}

/// <summary>NOISE_REDUCTION_MODE.</summary>
public enum NoiseReductionSetting
{
    Off            = 0,
    Fast           = 1,
    HighQuality    = 2,
    Minimal        = 3,
    ZeroShutterLag = 4,
}

/// <summary>EDGE_MODE.</summary>
public enum EdgeSetting
{
    Off            = 0,
    Fast           = 1,
    HighQuality    = 2,
    ZeroShutterLag = 3,
}

/// <summary>TONEMAP_MODE — только два «процедурных» режима (Fast/HighQuality) удобны
/// без задания curve points; ContrastCurve/GammaValue/PresetCurve требуют дополнительных
/// параметров, которые не выставить через UI.</summary>
public enum TonemapSetting
{
    Fast        = 1,
    HighQuality = 2,
}

/// <summary>COLOR_CORRECTION_ABERRATION_MODE / HOT_PIXEL_MODE — общий enum.</summary>
public enum SimpleProcessingSetting
{
    Off         = 0,
    Fast        = 1,
    HighQuality = 2,
}

/// <summary>FLASH_MODE + влияние на CONTROL_AE_MODE.</summary>
public enum FlashSetting
{
    Off    = 0,  // FLASH_MODE=OFF (CONTROL_AE_MODE=ON)
    Auto   = 1,  // FLASH_MODE=SINGLE (CONTROL_AE_MODE=ON_AUTO_FLASH)
    Single = 2,  // FLASH_MODE=SINGLE (CONTROL_AE_MODE=ON_ALWAYS_FLASH)
    Torch  = 3,  // FLASH_MODE=TORCH (постоянный свет)
}

/// <summary>CameraTemplate — на Android маппится в Android.Hardware.Camera2.CameraTemplate.</summary>
public enum CaptureTemplateSetting
{
    Preview        = 0,
    Record         = 1,
    VideoSnapshot  = 2,
}
