using System.Globalization;
using PhoneCamera.Settings;
#if ANDROID
using CS = PhoneCamera.Platforms.Android.CameraSettings;
#endif

namespace PhoneCamera;

// ╔════════════════════════════════════════════════════════════════════╗
// ║   Code-behind для SettingsPage.                                    ║
// ║                                                                     ║
// ║   • OnAppearing → LoadSettings() — наполняем Pickers/Switches/      ║
// ║     Entries текущими значениями из CameraSettings (Preferences).    ║
// ║   • Picker SelectedIndexChanged-обработчики раскрывают Entry        ║
// ║     с пометкой «Точное значение…» когда выбран последний пункт.     ║
// ║   • OnExposureModeChanged / OnAfModeChanged — управляют видимостью  ║
// ║     зависимых полей (Manual exposure / ISO / focus distance).       ║
// ║   • OnResetClicked → CameraSettings.ResetToDefaults() + LoadSettings ║
// ║   • OnApplyClicked → читаем UI в CameraSettings, зовём OnApplied,   ║
// ║     закрываем модал. OnApplied у MainPage перезапускает камеру.     ║
// ║                                                                     ║
// ║   Под не-Android доступ к CameraSettings обёрнут #if ANDROID —      ║
// ║   на iOS/Windows страница откроется, но ничего не сохранит.         ║
// ╚════════════════════════════════════════════════════════════════════╝
public partial class SettingsPage : ContentPage
{
    /// <summary>Вызывается после успешного «Применить и закрыть».
    /// MainPage подписывается чтобы перезапустить камеру с новыми настройками.</summary>
    public Action? OnApplied { get; set; }

    // Колбек для ручного подключения. Вызывается при нажатии «Подключиться» в
    // секции ручного подключения. MainPage передаёт сюда обёртку над ConnectToServer.
    public Action<string, int>? OnManualConnect { get; set; }

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    // ── Загрузка значений из CameraSettings в UI ───────────────────────────
    private void LoadSettings()
    {
#if ANDROID
        // ── Экспозиция и сенсор ────────────────────────────────────────────
        pExposureMode.SelectedIndex = CS.ExposureMode == ExposureModeSetting.Auto ? 0 : 1;
        pEvCompensation.SelectedIndex = EvStepsToIndex(CS.EvCompensation);
        swAeLock.IsToggled            = CS.AeLockEnabled;

        SelectExposureTime(CS.ManualExposureNs);
        SelectIso(CS.ManualIso);

        pAntiBanding.SelectedIndex = CS.AntiBanding switch
        {
            AntiBandingSetting.Auto => 0,
            AntiBandingSetting.Hz50 => 1,
            AntiBandingSetting.Hz60 => 2,
            _                       => 3,
        };

        // ── Баланс белого ──────────────────────────────────────────────────
        pWhiteBalance.SelectedIndex = CS.WhiteBalanceMode switch
        {
            AwbModeSetting.Auto            => 0,
            AwbModeSetting.Daylight        => 1,
            AwbModeSetting.CloudyDaylight  => 2,
            AwbModeSetting.Shade           => 3,
            AwbModeSetting.Incandescent    => 4,
            AwbModeSetting.Fluorescent     => 5,
            AwbModeSetting.WarmFluorescent => 6,
            AwbModeSetting.Twilight        => 7,
            _                              => 0,
        };
        swAwbLock.IsToggled = CS.AwbLockEnabled;

        // ── Фокус ──────────────────────────────────────────────────────────
        pAfMode.SelectedIndex = CS.FocusMode switch
        {
            AfModeSetting.ContinuousVideo   => 0,
            AfModeSetting.ContinuousPicture => 1,
            AfModeSetting.Auto              => 2,
            AfModeSetting.Manual            => 3,
            AfModeSetting.Off               => 4,
            _                               => 0,
        };
        SelectFocusDistance(CS.ManualFocusDistance);

        // ── Обработка изображения ──────────────────────────────────────────
        pNoiseReduction.SelectedIndex  = (int)CS.NoiseReduction;
        pEdge.SelectedIndex            = (int)CS.EdgeEnhancement;
        pTonemap.SelectedIndex         = CS.Tonemap == TonemapSetting.Fast ? 0 : 1;
        pColorAberration.SelectedIndex = (int)CS.ColorAberration;
        pHotPixel.SelectedIndex        = (int)CS.HotPixel;
        swVideoStabilization.IsToggled = CS.VideoStabilization;

        // ── Кадр ───────────────────────────────────────────────────────────
        SelectZoom(CS.ZoomLevel);

        // ── Вспышка ────────────────────────────────────────────────────────
        pFlash.SelectedIndex = (int)CS.Flash;

        // ── JPEG ───────────────────────────────────────────────────────────
        SelectJpegQuality(CS.JpegQuality);
        swApplySettingsToJpeg.IsToggled       = CS.ApplySettingsToJpeg;
        swAutoDisablePreviewOnStream.IsToggled = CS.AutoDisablePreviewOnStream;
        swShowAllResolutions.IsToggled        = CS.ShowAllResolutions;

        // ── Кодирование ────────────────────────────────────────────────────
        pCaptureTemplate.SelectedIndex = (int)CS.CaptureTemplate;
        pIFrameInterval.SelectedIndex  = IFrameSecToIndex(CS.IFrameIntervalSeconds);
        SelectH264Bpp(CS.H264BitsPerPixel);
        SelectH265Bpp(CS.H265BitsPerPixel);
        SelectBitrateCap(CS.BitrateCapBps);
        SelectBitrateMin(CS.BitrateMinBps);

        // ── Адаптивный битрейт ─────────────────────────────────────────────
        swAdaptive.IsToggled                  = CS.AdaptiveBitrateEnabled;
        pAdaptiveWindow.SelectedIndex         = AdaptiveWindowToIndex(CS.AdaptiveWindowMs);
        pAdaptiveBpThreshold.SelectedIndex    = AdaptiveBpToIndex(CS.AdaptiveBackpressureThreshold);
        pAdaptiveClearThreshold.SelectedIndex = AdaptiveClearToIndex(CS.AdaptiveClearThreshold);
        pAdaptiveClearWindows.SelectedIndex   = AdaptiveClearWindowsToIndex(CS.AdaptiveClearWindowsToRaise);
        pAdaptiveDecreaseFactor.SelectedIndex = AdaptiveDecreaseToIndex(CS.AdaptiveDecreaseFactor);
        pAdaptiveIncreaseFactor.SelectedIndex = AdaptiveIncreaseToIndex(CS.AdaptiveIncreaseFactor);
#endif

        UpdateConditionalVisibility();
    }

    // ── Сохранение значений из UI в CameraSettings ─────────────────────────
    private void SaveSettings()
    {
#if ANDROID
        // ── Экспозиция ─────────────────────────────────────────────────────
        CS.ExposureMode    = pExposureMode.SelectedIndex == 1
                                 ? ExposureModeSetting.Manual
                                 : ExposureModeSetting.Auto;
        CS.EvCompensation  = IndexToEvSteps(pEvCompensation.SelectedIndex);
        CS.AeLockEnabled   = swAeLock.IsToggled;
        CS.ManualExposureNs = ReadExposureTimeUi();
        CS.ManualIso        = ReadIsoUi();
        CS.AntiBanding      = pAntiBanding.SelectedIndex switch
        {
            1 => AntiBandingSetting.Hz50,
            2 => AntiBandingSetting.Hz60,
            3 => AntiBandingSetting.Off,
            _ => AntiBandingSetting.Auto,
        };

        // ── Баланс белого ──────────────────────────────────────────────────
        CS.WhiteBalanceMode = pWhiteBalance.SelectedIndex switch
        {
            1 => AwbModeSetting.Daylight,
            2 => AwbModeSetting.CloudyDaylight,
            3 => AwbModeSetting.Shade,
            4 => AwbModeSetting.Incandescent,
            5 => AwbModeSetting.Fluorescent,
            6 => AwbModeSetting.WarmFluorescent,
            7 => AwbModeSetting.Twilight,
            _ => AwbModeSetting.Auto,
        };
        CS.AwbLockEnabled = swAwbLock.IsToggled;

        // ── Фокус ──────────────────────────────────────────────────────────
        CS.FocusMode = pAfMode.SelectedIndex switch
        {
            1 => AfModeSetting.ContinuousPicture,
            2 => AfModeSetting.Auto,
            3 => AfModeSetting.Manual,
            4 => AfModeSetting.Off,
            _ => AfModeSetting.ContinuousVideo,
        };
        CS.ManualFocusDistance = ReadFocusDistanceUi();

        // ── Обработка ──────────────────────────────────────────────────────
        CS.NoiseReduction     = (NoiseReductionSetting)Math.Max(0, pNoiseReduction.SelectedIndex);
        CS.EdgeEnhancement    = (EdgeSetting)Math.Max(0, pEdge.SelectedIndex);
        CS.Tonemap            = pTonemap.SelectedIndex == 1 ? TonemapSetting.HighQuality : TonemapSetting.Fast;
        CS.ColorAberration    = (SimpleProcessingSetting)Math.Max(0, pColorAberration.SelectedIndex);
        CS.HotPixel           = (SimpleProcessingSetting)Math.Max(0, pHotPixel.SelectedIndex);
        CS.VideoStabilization = swVideoStabilization.IsToggled;

        // ── Кадр ───────────────────────────────────────────────────────────
        CS.ZoomLevel = ReadZoomUi();

        // ── Вспышка ────────────────────────────────────────────────────────
        CS.Flash = (FlashSetting)Math.Max(0, pFlash.SelectedIndex);

        // ── JPEG ───────────────────────────────────────────────────────────
        CS.JpegQuality = ReadJpegQualityUi();
        CS.ApplySettingsToJpeg        = swApplySettingsToJpeg.IsToggled;
        CS.AutoDisablePreviewOnStream = swAutoDisablePreviewOnStream.IsToggled;
        CS.ShowAllResolutions         = swShowAllResolutions.IsToggled;

        // ── Кодирование ────────────────────────────────────────────────────
        CS.CaptureTemplate       = (CaptureTemplateSetting)Math.Max(0, pCaptureTemplate.SelectedIndex);
        CS.IFrameIntervalSeconds = IndexToIFrameSec(pIFrameInterval.SelectedIndex);
        CS.H264BitsPerPixel      = ReadH264BppUi();
        CS.H265BitsPerPixel      = ReadH265BppUi();
        CS.BitrateCapBps         = ReadBitrateCapUi();
        CS.BitrateMinBps         = ReadBitrateMinUi();

        // ── Адаптивный битрейт ─────────────────────────────────────────────
        CS.AdaptiveBitrateEnabled         = swAdaptive.IsToggled;
        CS.AdaptiveWindowMs               = IndexToAdaptiveWindow(pAdaptiveWindow.SelectedIndex);
        CS.AdaptiveBackpressureThreshold  = IndexToAdaptiveBp(pAdaptiveBpThreshold.SelectedIndex);
        CS.AdaptiveClearThreshold         = IndexToAdaptiveClear(pAdaptiveClearThreshold.SelectedIndex);
        CS.AdaptiveClearWindowsToRaise    = IndexToAdaptiveClearWindows(pAdaptiveClearWindows.SelectedIndex);
        CS.AdaptiveDecreaseFactor         = IndexToAdaptiveDecrease(pAdaptiveDecreaseFactor.SelectedIndex);
        CS.AdaptiveIncreaseFactor         = IndexToAdaptiveIncrease(pAdaptiveIncreaseFactor.SelectedIndex);
#endif
    }

    // ── Видимость зависимых полей ──────────────────────────────────────────
    private void UpdateConditionalVisibility()
    {
        bool manualExposure = pExposureMode.SelectedIndex == 1;
        // Подсветка зависимых полей в auto/manual режиме — оставляем видимыми
        // всегда (чтобы юзер видел текущее значение и понимал, что станет
        // активно при переключении), но при auto-режиме они «бесполезны».
        // Entry-поля «Точное значение…» подчинены выбору в Picker (см. ниже).

        bool manualFocus = pAfMode.SelectedIndex == 3;
        // Аналогично — pFocusDistance видим всегда; eFocusDistance — по Picker'у.

        // Просто обновим conditional Entry-видимость в соответствии с текущим
        // SelectedIndex pickers (на случай если LoadSettings выбрал «Точное значение…»).
        SyncEntryVisibility(pExposureTime,   eExposureTime);
        SyncEntryVisibility(pIso,            eIso);
        SyncEntryVisibility(pFocusDistance,  eFocusDistance);
        SyncEntryVisibility(pZoom,           eZoom);
        SyncEntryVisibility(pH264Bpp,        eH264Bpp);
        SyncEntryVisibility(pH265Bpp,        eH265Bpp);
        SyncEntryVisibility(pBitrateCap,     eBitrateCap);
        SyncEntryVisibility(pBitrateMin,     eBitrateMin);
        SyncEntryVisibility(pJpegQuality,    eJpegQuality);
    }

    private static void SyncEntryVisibility(Picker picker, Entry entry)
    {
        if (picker == null || entry == null) return;
        int last = picker.Items.Count - 1;
        entry.IsVisible = picker.SelectedIndex == last;
    }

    // ── Event handlers: Picker → Entry visibility ─────────────────────────
    private void OnExposureTimeChanged(object? sender, EventArgs e) => SyncEntryVisibility(pExposureTime, eExposureTime);
    private void OnIsoChanged(object? sender, EventArgs e)           => SyncEntryVisibility(pIso, eIso);
    private void OnFocusDistanceChanged(object? sender, EventArgs e) => SyncEntryVisibility(pFocusDistance, eFocusDistance);
    private void OnZoomChanged(object? sender, EventArgs e)          => SyncEntryVisibility(pZoom, eZoom);
    private void OnH264BppChanged(object? sender, EventArgs e)       => SyncEntryVisibility(pH264Bpp, eH264Bpp);
    private void OnH265BppChanged(object? sender, EventArgs e)       => SyncEntryVisibility(pH265Bpp, eH265Bpp);
    private void OnBitrateCapChanged(object? sender, EventArgs e)    => SyncEntryVisibility(pBitrateCap, eBitrateCap);
    private void OnBitrateMinChanged(object? sender, EventArgs e)    => SyncEntryVisibility(pBitrateMin, eBitrateMin);
    private void OnJpegQualityChanged(object? sender, EventArgs e)   => SyncEntryVisibility(pJpegQuality, eJpegQuality);

    private void OnExposureModeChanged(object? sender, EventArgs e) => UpdateConditionalVisibility();
    private void OnAfModeChanged(object? sender, EventArgs e)       => UpdateConditionalVisibility();

    // ── Кнопки внизу ───────────────────────────────────────────────────────
    private async void OnCloseTapped(object? sender, TappedEventArgs e)
    {
        // Закрываем без сохранения.
        await Navigation.PopModalAsync();
    }

    private void OnResetClicked(object? sender, EventArgs e)
    {
#if ANDROID
        CS.ResetToDefaults();
#endif
        LoadSettings();
    }

    private async void OnApplyClicked(object? sender, EventArgs e)
    {
        SaveSettings();
        try { OnApplied?.Invoke(); } catch { }
        await Navigation.PopModalAsync();
    }

    // Ручное подключение к серверу. Делает то же, что и обработчик QR-кода —
    // вызывает MainPage.ConnectToServer(ip, port). Закрывает окно настроек,
    // чтобы пользователь сразу видел статус подключения и мог начать стрим.
    private async void OnManualConnectClicked(object? sender, EventArgs e)
    {
        string ipText   = eManualIp.Text?.Trim()   ?? "";
        string portText = eManualPort.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(ipText))
        {
            lblManualStatus.Text = "Введи IP-адрес";
            return;
        }
        if (!int.TryParse(portText, out int port) || port <= 0 || port > 65535)
        {
            lblManualStatus.Text = "Неверный порт (нужно число 1..65535)";
            return;
        }
        if (!System.Net.IPAddress.TryParse(ipText, out _))
        {
            // Не блокируем — может быть валидное hostname. Просто предупреждаем
            // в логах. ConnectAsync сам зарезолвит/упадёт.
            System.Diagnostics.Debug.WriteLine($"[PCam][UI] Manual connect: '{ipText}' не похож на IP, попробую как hostname");
        }

        lblManualStatus.Text = $"Подключение к {ipText}:{port}…";
        try { OnManualConnect?.Invoke(ipText, port); } catch { }
        await Navigation.PopModalAsync();
    }

    // ╔═══════════ Преобразование индексов Picker'ов ↔ значения ════════════╗

    // EV compensation: индекс 0..8 → шаги ÷ 0.5 EV (диапазон -2..+2 EV).
    // Camera2 EV step обычно = 1/2 (шаг), значит -2 EV = -4 шага, 0 EV = 0, +2 EV = 4 шага.
    // Мы храним int (шаги) и считаем, что step = 0.5 EV. Реальный step применяет камера.
    private static int EvStepsToIndex(int steps)
    {
        // step=0.5: -4..+4 шагов → 0..8 индексов
        int idx = (steps / 1) + 4; // steps=-4 → idx=0; steps=0 → idx=4; steps=+4 → idx=8
        return Math.Clamp(idx, 0, 8);
    }
    private static int IndexToEvSteps(int idx)
    {
        if (idx < 0) idx = 4;
        return idx - 4;
    }

    // Exposure time picker items:
    // 0=1/15, 1=1/30, 2=1/60, 3=1/125, 4=1/250, 5=1/500, 6=1/1000, 7=custom
    private static readonly long[] ExposureTimePresetsNs = new long[]
    {
        66_666_666L, // 1/15
        33_333_333L, // 1/30
        16_666_666L, // 1/60
        8_000_000L,  // 1/125
        4_000_000L,  // 1/250
        2_000_000L,  // 1/500
        1_000_000L,  // 1/1000
    };
    private void SelectExposureTime(long ns)
    {
        for (int i = 0; i < ExposureTimePresetsNs.Length; i++)
        {
            if (ExposureTimePresetsNs[i] == ns)
            {
                pExposureTime.SelectedIndex = i;
                eExposureTime.IsVisible = false;
                return;
            }
        }
        pExposureTime.SelectedIndex = ExposureTimePresetsNs.Length; // «Точное значение…»
        eExposureTime.Text = ns.ToString(CultureInfo.InvariantCulture);
        eExposureTime.IsVisible = true;
    }
    private long ReadExposureTimeUi()
    {
        int idx = pExposureTime.SelectedIndex;
        if (idx >= 0 && idx < ExposureTimePresetsNs.Length)
            return ExposureTimePresetsNs[idx];
        if (long.TryParse(eExposureTime.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out long v) && v > 0)
            return v;
        return 16_666_666L;
    }

    // ISO presets: 0=100, 1=200, 2=400, 3=800, 4=1600, 5=3200, 6=custom
    private static readonly int[] IsoPresets = new[] { 100, 200, 400, 800, 1600, 3200 };
    private void SelectIso(int iso)
    {
        for (int i = 0; i < IsoPresets.Length; i++)
        {
            if (IsoPresets[i] == iso)
            {
                pIso.SelectedIndex = i;
                eIso.IsVisible = false;
                return;
            }
        }
        pIso.SelectedIndex = IsoPresets.Length;
        eIso.Text = iso.ToString(CultureInfo.InvariantCulture);
        eIso.IsVisible = true;
    }
    private int ReadIsoUi()
    {
        int idx = pIso.SelectedIndex;
        if (idx >= 0 && idx < IsoPresets.Length) return IsoPresets[idx];
        if (int.TryParse(eIso.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int v) && v > 0)
            return v;
        return 800;
    }

    // Focus distance presets (диоптрии): 0, 0.5, 1, 2, 5, 10 + custom
    private static readonly float[] FocusDistancePresets = new[] { 0f, 0.5f, 1f, 2f, 5f, 10f };
    private void SelectFocusDistance(float diopt)
    {
        for (int i = 0; i < FocusDistancePresets.Length; i++)
        {
            if (Math.Abs(FocusDistancePresets[i] - diopt) < 0.0001f)
            {
                pFocusDistance.SelectedIndex = i;
                eFocusDistance.IsVisible = false;
                return;
            }
        }
        pFocusDistance.SelectedIndex = FocusDistancePresets.Length;
        eFocusDistance.Text = diopt.ToString("0.###", CultureInfo.InvariantCulture);
        eFocusDistance.IsVisible = true;
    }
    private float ReadFocusDistanceUi()
    {
        int idx = pFocusDistance.SelectedIndex;
        if (idx >= 0 && idx < FocusDistancePresets.Length) return FocusDistancePresets[idx];
        if (float.TryParse(eFocusDistance.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out float v) && v >= 0)
            return v;
        return 0f;
    }

    // Zoom presets: 1.0×, 1.5×, 2×, 3×, 4× + custom
    private static readonly float[] ZoomPresets = new[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f };
    private void SelectZoom(float zoom)
    {
        for (int i = 0; i < ZoomPresets.Length; i++)
        {
            if (Math.Abs(ZoomPresets[i] - zoom) < 0.0001f)
            {
                pZoom.SelectedIndex = i;
                eZoom.IsVisible = false;
                return;
            }
        }
        pZoom.SelectedIndex = ZoomPresets.Length;
        eZoom.Text = zoom.ToString("0.###", CultureInfo.InvariantCulture);
        eZoom.IsVisible = true;
    }
    private float ReadZoomUi()
    {
        int idx = pZoom.SelectedIndex;
        if (idx >= 0 && idx < ZoomPresets.Length) return ZoomPresets[idx];
        if (float.TryParse(eZoom.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out float v) && v >= 1f)
            return v;
        return 1.0f;
    }

    // IFrame interval: 1, 2, 3, 5, 10 секунд
    private static readonly int[] IFramePresets = new[] { 1, 2, 3, 5, 10 };
    private static int IFrameSecToIndex(int sec)
    {
        for (int i = 0; i < IFramePresets.Length; i++)
            if (IFramePresets[i] == sec) return i;
        return 0;
    }
    private static int IndexToIFrameSec(int idx) =>
        idx >= 0 && idx < IFramePresets.Length ? IFramePresets[idx] : 1;

    // H.264 bpp: 0.04, 0.06, 0.083, 0.10, 0.15 + custom
    private static readonly double[] H264BppPresets = new[] { 0.04, 0.06, 0.083, 0.10, 0.15 };
    private void SelectH264Bpp(double bpp)
    {
        for (int i = 0; i < H264BppPresets.Length; i++)
        {
            if (Math.Abs(H264BppPresets[i] - bpp) < 0.0005)
            {
                pH264Bpp.SelectedIndex = i;
                eH264Bpp.IsVisible = false;
                return;
            }
        }
        pH264Bpp.SelectedIndex = H264BppPresets.Length;
        eH264Bpp.Text = bpp.ToString("0.####", CultureInfo.InvariantCulture);
        eH264Bpp.IsVisible = true;
    }
    private double ReadH264BppUi()
    {
        int idx = pH264Bpp.SelectedIndex;
        if (idx >= 0 && idx < H264BppPresets.Length) return H264BppPresets[idx];
        if (double.TryParse(eH264Bpp.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v > 0)
            return v;
        return 0.083;
    }

    // H.265 bpp: 0.03, 0.05, 0.083, 0.10 + custom
    private static readonly double[] H265BppPresets = new[] { 0.03, 0.05, 0.083, 0.10 };
    private void SelectH265Bpp(double bpp)
    {
        for (int i = 0; i < H265BppPresets.Length; i++)
        {
            if (Math.Abs(H265BppPresets[i] - bpp) < 0.0005)
            {
                pH265Bpp.SelectedIndex = i;
                eH265Bpp.IsVisible = false;
                return;
            }
        }
        pH265Bpp.SelectedIndex = H265BppPresets.Length;
        eH265Bpp.Text = bpp.ToString("0.####", CultureInfo.InvariantCulture);
        eH265Bpp.IsVisible = true;
    }
    private double ReadH265BppUi()
    {
        int idx = pH265Bpp.SelectedIndex;
        if (idx >= 0 && idx < H265BppPresets.Length) return H265BppPresets[idx];
        if (double.TryParse(eH265Bpp.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v > 0)
            return v;
        return 0.083;
    }

    // Bitrate cap: 5, 10, 15, 25, 40 Mbps + custom
    private static readonly int[] BitrateCapPresets = new[] { 5_000_000, 10_000_000, 15_000_000, 25_000_000, 40_000_000 };
    private void SelectBitrateCap(int bps)
    {
        for (int i = 0; i < BitrateCapPresets.Length; i++)
        {
            if (BitrateCapPresets[i] == bps)
            {
                pBitrateCap.SelectedIndex = i;
                eBitrateCap.IsVisible = false;
                return;
            }
        }
        pBitrateCap.SelectedIndex = BitrateCapPresets.Length;
        eBitrateCap.Text = bps.ToString(CultureInfo.InvariantCulture);
        eBitrateCap.IsVisible = true;
    }
    private int ReadBitrateCapUi()
    {
        int idx = pBitrateCap.SelectedIndex;
        if (idx >= 0 && idx < BitrateCapPresets.Length) return BitrateCapPresets[idx];
        if (int.TryParse(eBitrateCap.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int v) && v > 0)
            return v;
        return 25_000_000;
    }

    // Bitrate min: 0.5, 1, 2, 5 Mbps + custom
    private static readonly int[] BitrateMinPresets = new[] { 500_000, 1_000_000, 2_000_000, 5_000_000 };
    private void SelectBitrateMin(int bps)
    {
        for (int i = 0; i < BitrateMinPresets.Length; i++)
        {
            if (BitrateMinPresets[i] == bps)
            {
                pBitrateMin.SelectedIndex = i;
                eBitrateMin.IsVisible = false;
                return;
            }
        }
        pBitrateMin.SelectedIndex = BitrateMinPresets.Length;
        eBitrateMin.Text = bps.ToString(CultureInfo.InvariantCulture);
        eBitrateMin.IsVisible = true;
    }
    private int ReadBitrateMinUi()
    {
        int idx = pBitrateMin.SelectedIndex;
        if (idx >= 0 && idx < BitrateMinPresets.Length) return BitrateMinPresets[idx];
        if (int.TryParse(eBitrateMin.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int v) && v > 0)
            return v;
        return 2_000_000;
    }

    // JPEG quality presets: 10, 25, 50, 70, 85, 95 + custom
    private static readonly int[] JpegQualityPresets = new[] { 10, 25, 50, 70, 85, 95 };
    private void SelectJpegQuality(int q)
    {
        for (int i = 0; i < JpegQualityPresets.Length; i++)
        {
            if (JpegQualityPresets[i] == q)
            {
                pJpegQuality.SelectedIndex = i;
                eJpegQuality.IsVisible = false;
                return;
            }
        }
        pJpegQuality.SelectedIndex = JpegQualityPresets.Length;
        eJpegQuality.Text = q.ToString(CultureInfo.InvariantCulture);
        eJpegQuality.IsVisible = true;
    }
    private int ReadJpegQualityUi()
    {
        int idx = pJpegQuality.SelectedIndex;
        if (idx >= 0 && idx < JpegQualityPresets.Length) return JpegQualityPresets[idx];
        if (int.TryParse(eJpegQuality.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out int v) && v >= 1 && v <= 100)
            return v;
        return 25;
    }

    // Adaptive window ms: 1000, 2000, 3000, 5000, 10000
    private static readonly int[] AdaptiveWindowPresets = new[] { 1000, 2000, 3000, 5000, 10000 };
    private static int AdaptiveWindowToIndex(int ms)
    {
        for (int i = 0; i < AdaptiveWindowPresets.Length; i++)
            if (AdaptiveWindowPresets[i] == ms) return i;
        return 1;
    }
    private static int IndexToAdaptiveWindow(int idx) =>
        idx >= 0 && idx < AdaptiveWindowPresets.Length ? AdaptiveWindowPresets[idx] : 2000;

    // Adaptive bp threshold: 0.01, 0.03, 0.05, 0.10, 0.15
    private static readonly double[] AdaptiveBpPresets = new[] { 0.01, 0.03, 0.05, 0.10, 0.15 };
    private static int AdaptiveBpToIndex(double v)
    {
        for (int i = 0; i < AdaptiveBpPresets.Length; i++)
            if (Math.Abs(AdaptiveBpPresets[i] - v) < 0.001) return i;
        return 2;
    }
    private static double IndexToAdaptiveBp(int idx) =>
        idx >= 0 && idx < AdaptiveBpPresets.Length ? AdaptiveBpPresets[idx] : 0.05;

    // Adaptive clear threshold: 0, 0.005, 0.01, 0.02, 0.05
    private static readonly double[] AdaptiveClearPresets = new[] { 0.0, 0.005, 0.01, 0.02, 0.05 };
    private static int AdaptiveClearToIndex(double v)
    {
        for (int i = 0; i < AdaptiveClearPresets.Length; i++)
            if (Math.Abs(AdaptiveClearPresets[i] - v) < 0.0005) return i;
        return 2;
    }
    private static double IndexToAdaptiveClear(int idx) =>
        idx >= 0 && idx < AdaptiveClearPresets.Length ? AdaptiveClearPresets[idx] : 0.01;

    // Adaptive clear windows: 1, 2, 3, 5, 10
    private static readonly int[] AdaptiveClearWindowsPresets = new[] { 1, 2, 3, 5, 10 };
    private static int AdaptiveClearWindowsToIndex(int v)
    {
        for (int i = 0; i < AdaptiveClearWindowsPresets.Length; i++)
            if (AdaptiveClearWindowsPresets[i] == v) return i;
        return 2;
    }
    private static int IndexToAdaptiveClearWindows(int idx) =>
        idx >= 0 && idx < AdaptiveClearWindowsPresets.Length ? AdaptiveClearWindowsPresets[idx] : 3;

    // Adaptive decrease: 0.70, 0.80, 0.85, 0.90, 0.95
    private static readonly double[] AdaptiveDecreasePresets = new[] { 0.70, 0.80, 0.85, 0.90, 0.95 };
    private static int AdaptiveDecreaseToIndex(double v)
    {
        for (int i = 0; i < AdaptiveDecreasePresets.Length; i++)
            if (Math.Abs(AdaptiveDecreasePresets[i] - v) < 0.005) return i;
        return 2;
    }
    private static double IndexToAdaptiveDecrease(int idx) =>
        idx >= 0 && idx < AdaptiveDecreasePresets.Length ? AdaptiveDecreasePresets[idx] : 0.85;

    // Adaptive increase: 1.05, 1.10, 1.20, 1.30, 1.50
    private static readonly double[] AdaptiveIncreasePresets = new[] { 1.05, 1.10, 1.20, 1.30, 1.50 };
    private static int AdaptiveIncreaseToIndex(double v)
    {
        for (int i = 0; i < AdaptiveIncreasePresets.Length; i++)
            if (Math.Abs(AdaptiveIncreasePresets[i] - v) < 0.005) return i;
        return 1;
    }
    private static double IndexToAdaptiveIncrease(int idx) =>
        idx >= 0 && idx < AdaptiveIncreasePresets.Length ? AdaptiveIncreasePresets[idx] : 1.10;
}
