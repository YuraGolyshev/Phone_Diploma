namespace PhoneCamera.Controls;

public record CameraConfig(int Width, int Height, double MinFps, double MaxFps)
{
    public string DisplayName
    {
        get
        {
            string res = $"{Width}×{Height}";
            string fps = MinFps == MaxFps
                ? $"{MaxFps:F0} fps"
                : $"{MinFps:F0}–{MaxFps:F0} fps";
            return $"{res}   {fps}";
        }
    }

    public static CameraConfig? FindDefault(IReadOnlyList<CameraConfig> configs)
    {
        // 1. Точно 1920×1080, постоянные 30 fps
        var c = configs.FirstOrDefault(x =>
            x.Width == 1920 && x.Height == 1080 && x.MinFps == 30 && x.MaxFps == 30);
        if (c != null) return c;

        // 2. 1920×1080, максимум ровно 30
        c = configs.FirstOrDefault(x =>
            x.Width == 1920 && x.Height == 1080 && x.MaxFps == 30);
        if (c != null) return c;

        // 3. 1920×1080, максимум >= 30
        c = configs.FirstOrDefault(x =>
            x.Width == 1920 && x.Height == 1080 && x.MaxFps >= 30);
        if (c != null) return c;

        // 4. Любое 1920×1080
        c = configs.FirstOrDefault(x => x.Width == 1920 && x.Height == 1080);
        if (c != null) return c;

        // 5. Лучшее из доступного (список уже отсортирован)
        return configs.FirstOrDefault();
    }
}
