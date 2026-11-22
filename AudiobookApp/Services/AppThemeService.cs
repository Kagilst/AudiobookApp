using System;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace AudiobookApp.Services
{
    /// <summary>
    /// App-wide light/dark theme. Null override follows Windows settings.
    /// </summary>
    public static class AppThemeService
    {
        private const string SettingKey = "AppColorTheme";

        public static event Action? ThemeChanged;

        /// <summary>Null means follow Windows light/dark.</summary>
        public static bool? Override { get; private set; }

        public static void Initialize(Window window)
        {
            Override = ReadOverride();
            Apply(window);
        }

        public static void Toggle(Window window)
        {
            var nextDark = !IsEffectivelyDark(window);
            Override = nextDark;
            SaveOverride(nextDark);
            Apply(window);
            ThemeChanged?.Invoke();
        }

        public static void Apply(Window window)
        {
            if (window.Content is not FrameworkElement root)
                return;

            root.RequestedTheme = Override switch
            {
                true => ElementTheme.Dark,
                false => ElementTheme.Light,
                _ => ElementTheme.Default
            };
        }

        public static string ToggleButtonLabel(Window? window = null) =>
            IsEffectivelyDark(window) ? "Light mode" : "Dark mode";

        public static bool IsEffectivelyDark(Window? window = null)
        {
            if (Override is bool forced)
                return forced;

            if (window?.Content is FrameworkElement root
                && root.ActualTheme == ElementTheme.Dark)
            {
                return true;
            }

            try
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            catch
            {
                return false;
            }
        }

        private static bool? ReadOverride()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(SettingKey, out var value) && value is bool dark)
                    return dark;
            }
            catch
            {
                // Follow system.
            }

            return null;
        }

        private static void SaveOverride(bool isDark)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = isDark;
            }
            catch
            {
                // Ignore persistence failures.
            }
        }
    }
}
