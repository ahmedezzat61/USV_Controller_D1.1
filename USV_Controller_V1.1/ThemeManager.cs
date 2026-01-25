using System;
using System.Windows;

namespace USV_Controller_V1._1
{
    public static class ThemeManager
    {
        private static bool _isDarkTheme = true;
        private static ResourceDictionary? _currentTheme;
        
        // Event to notify components when theme changes (for non-XAML updates like Chart colors)
        public static event Action? ThemeChanged;
        
        public static bool IsDarkTheme => _isDarkTheme;
        
        public static void Initialize()
        {
            // Load dark theme by default
            ApplyTheme(true);
        }
        
        public static void ToggleTheme()
        {
            ApplyTheme(!_isDarkTheme);
        }
        
        public static void ApplyTheme(bool dark)
        {
            _isDarkTheme = dark;
            
            var app = Application.Current;
            if (app == null) return;
            
            // Remove current theme if exists
            if (_currentTheme != null && app.Resources.MergedDictionaries.Contains(_currentTheme))
            {
                app.Resources.MergedDictionaries.Remove(_currentTheme);
            }
            
            // Load new theme
            // Load new theme using relative URI which is often more reliable for local files
            string themePath = dark ? "ThemeDark.xaml" : "ThemeLight.xaml";
            
            _currentTheme = new ResourceDictionary
            {
                Source = new Uri(themePath, UriKind.Relative)
            };
            
            // Clear existing themes to ensure no conflicts and correct priority
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(_currentTheme);
            
            // Notify subscribers
            ThemeChanged?.Invoke();
        }
    }
}
