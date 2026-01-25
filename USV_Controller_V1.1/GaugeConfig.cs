using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace USV_Controller_V1._1
{
    // Partial class: kept methods related to RPM gauge configuration in a separate file.
    public partial class MainWindow
    {
        private void SetupRpmChart(PieChart chart)
        {
            chart.RenderTransform = new RotateTransform(+180);
            UpdateGaugeTheme(ThemeManager.IsDarkTheme);
            chart.IsHitTestVisible = false;
        }

        private void UpdateGaugeTheme(bool isDark)
        {
            var zones = isDark 
                ? new (double min, double max, SKColor color)[] // Night Mode (Original)
                {
                    (0,    999,  SKColor.Parse("#1A5A7A")),   // Dark teal
                    (1000, 1499, SKColor.Parse("#2A6A4A")),   // Dark green
                    (1500, 2499, SKColor.Parse("#3A7A5A")),   // Medium green
                    (2500, 2999, SKColor.Parse("#4A8A6A")),   // Light green
                    (3000, 3499, SKColor.Parse("#00B4CC")),   // Teal accent
                    (3500, 4000, SKColor.Parse("#00D4FF")),   // Cyan accent
                }
                : new (double min, double max, SKColor color)[] // Day Mode (User Requested)
                {
                    (0,    999,  SKColor.Parse("#FFFF00")),   // Yellow
                    (1000, 1499, SKColor.Parse("#FFA500")),   // Orange
                    (1500, 2499, SKColor.Parse("#FFFF00")),   // Yellow
                    (2500, 2999, SKColor.Parse("#9ACD32")),   // YellowGreen
                    (3000, 3499, SKColor.Parse("#008000")),   // Green
                    (3500, 4000, SKColor.Parse("#32CD32")),   // LimeGreen
                };

            var degrees = new List<double>();
            double totalDeg = 0.0;

            foreach (var z in zones)
            {
                double span = z.max - z.min + 1;
                double deg = span / 4000.0 * 180.0;
                degrees.Add(deg);
                totalDeg += deg;
            }

            double correction = 180.0 - totalDeg;
            if (Math.Abs(correction) > 1e-6)
                degrees[degrees.Count - 1] += correction;

            var series = new List<ISeries>();
            for (int i = 0; i < zones.Length; i++)
            {
                series.Add(new PieSeries<double>
                {
                    Values = new double[] { degrees[i] },
                    Fill = new SolidColorPaint(zones[i].color),
                    InnerRadius = 70,
                    HoverPushout = 0
                });
            }

            // Transparent filler for the bottom half
            series.Add(new PieSeries<double>
            {
                Values = new double[] { 180.0 },
                Fill = new SolidColorPaint(SKColors.Transparent),
                InnerRadius = 70
            });

            // Update both charts
            if (RpmPieLeft != null) RpmPieLeft.Series = series.ToArray();
            if (RpmPieRight != null) RpmPieRight.Series = series.ToArray();
        }

        private void IntializedRpm(double rpm)
        {
            rpm = Math.Clamp(rpm, 0, 100);
            double angle = -90 + (rpm / 100.0 * 180.0);
            GaugeNeedleLeft.RenderTransform = new RotateTransform(angle, 125, 125);
            SpeedValueTextLeft.Text = $"{rpm:F0} %";
        }

        private void FeedbackRpm(double rpm)
        {
            rpm = Math.Clamp(rpm, 0, 100);
            double angle = -90 + (rpm / 100.0 * 180.0);
            GaugeNeedleRight.RenderTransform = new RotateTransform(angle, 125, 125);
            SpeedValueTextRight.Text = $"{rpm:F0} %";
        }
    }
}