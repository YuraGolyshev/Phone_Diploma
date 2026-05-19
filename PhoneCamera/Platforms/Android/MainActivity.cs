using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace PhoneCamera
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ScreenOrientation = ScreenOrientation.FullSensor, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Не давать экрану засыпать пока приложение на переднем плане.
            // Флаг автоматически снимается, когда окно теряет фокус (другая активити,
            // системный диалог) — не нужно ручного wake-lock'а и разрешений.
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
    }
}
