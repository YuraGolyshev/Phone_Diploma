using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace PhoneCamera
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ScreenOrientation = ScreenOrientation.FullSensor, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        // Высота системной status bar в dp — выставляется в OnCreate, читается из
        // MainPage для отрисовки backdrop'а правильного размера. Дефолт 28 dp —
        // среднее значение для большинства Android-устройств без notch'а.
        public static int StatusBarHeightDp { get; private set; } = 28;

        // Слушатель WindowInsets, который «съедает» все insets. Без этого
        // MAUI PageHandler добавил бы Padding под статус-бар, и шторки сместились
        // бы. У нас они сами поднимаются под status bar; цвет под прозрачной
        // системной шторкой даёт BoxView-backdrop в MainPage.
        private class ConsumeAllInsetsListener : Java.Lang.Object, Android.Views.View.IOnApplyWindowInsetsListener
        {
            public WindowInsets? OnApplyWindowInsets(Android.Views.View v, WindowInsets insets)
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                    return WindowInsets.Consumed;
#pragma warning disable CS0618
                return insets.ConsumeSystemWindowInsets()?.ConsumeStableInsets();
#pragma warning restore CS0618
            }
        }
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Запрашиваем размер status bar у системы. Используется в MainPage
            // для отрисовки backdrop'а под прозрачной системной шторкой.
            try
            {
                int resId = Resources!.GetIdentifier("status_bar_height", "dimen", "android");
                if (resId > 0)
                {
                    int px = Resources.GetDimensionPixelSize(resId);
                    float density = Resources.DisplayMetrics?.Density ?? 1f;
                    StatusBarHeightDp = (int)System.Math.Round(px / density);
                }
            }
            catch { /* остаётся дефолт 28 dp */ }

            // Не давать экрану засыпать пока приложение на переднем плане.
            // Флаг автоматически снимается, когда окно теряет фокус (другая активити,
            // системный диалог) — не нужно ручного wake-lock'а и разрешений.
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            if (Window != null)
            {
                // ── Edge-to-edge: контент рисуется ПОД системными барами ───────
                // Без этого по краям экрана видна тёмно-серая полоса в зоне
                // status bar / navigation bar / выреза камеры (особенно заметно
                // на устройствах с punch-hole/notch в landscape).
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    // Android 11+ — современный API.
                    Window.SetDecorFitsSystemWindows(false);
                }
                else
                {
                    // Android < 11: эквивалент через DecorView flags.
                    // LayoutStable + LayoutFullscreen говорят системе, что мы хотим
                    // рисовать под status bar (но сам status bar остаётся видимым).
#pragma warning disable CS0618 // SystemUiVisibility deprecated since API 30
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)
                        ((int)SystemUiFlags.LayoutStable | (int)SystemUiFlags.LayoutFullscreen);
#pragma warning restore CS0618
                }

                // ── Цвета системных баров ──────────────────────────────────────
                // #CC000000 — тот же полупрозрачный чёрный, что у наших шторок
                // разрешения / FPS / формата. Status bar визуально сливается с UI.
                var barColor = Android.Graphics.Color.Argb(0xCC, 0, 0, 0);
                Window.SetStatusBarColor(barColor);
                Window.SetNavigationBarColor(barColor);

                // ── Cutout/notch: разрешить контенту рисоваться в зоне выреза ──
                // ShortEdges — content идёт под cutout, когда тот на короткой
                // стороне устройства (типичный случай — punch-hole/notch сверху
                // в portrait → попадает на левый край в landscape).
                // Always (API 30+) — в любой ориентации, без ограничений.
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    Window.Attributes!.LayoutInDisplayCutoutMode =
                        LayoutInDisplayCutoutMode.Always;
                }
                else if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    Window.Attributes!.LayoutInDisplayCutoutMode =
                        LayoutInDisplayCutoutMode.ShortEdges;
                }

                // Финальный шаг: перехватываем WindowInsets dispatch и съедаем все
                // insets. MAUI больше не добавит safe-area padding, content идёт
                // от края до края. Цвет под прозрачной системной шторкой даёт
                // BoxView-backdrop в MainPage (см. там).
                Window.DecorView.SetOnApplyWindowInsetsListener(new ConsumeAllInsetsListener());
            }
        }
    }
}
