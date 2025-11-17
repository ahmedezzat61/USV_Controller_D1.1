using System;
using System.Windows;
using System.Windows.Media;

namespace USV_Controller_V1._1
{
    // Partial class - mapping and steering helpers (moved from MainWindow)
    public partial class MainWindow
    {
        private static double Map(double value, double inMin, double inMax, double outMin, double outMax)
        {
            if (Math.Abs(inMax - inMin) < double.Epsilon) return outMin;
            return (value - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
        }

        // ✅ REMOVED DUPLICATE - Use the one in PacketBuilder.cs instead
        // This version had a deadzone parameter, but camera control doesn't need it
        // Keep only ONE version in PacketBuilder.cs with optional deadzone parameter

        private double MapSteeringValue(int feedback3)
        {
            double angle;
            if (feedback3 <= 230)
                angle = -27 + (feedback3 - 230) * ((-30 - (-27)) / (230.0 - 200));
            else if (feedback3 <= 310)
                angle = -27 + (feedback3 - 230) * ((-20 - (-27)) / (310.0 - 230));
            else if (feedback3 <= 540)
                angle = -20 + (feedback3 - 310) * ((0 - (-20)) / (540.0 - 310));
            else if (feedback3 <= 770)
                angle = 0 + (feedback3 - 540) * ((20 - 0) / (770.0 - 540));
            else if (feedback3 <= 840)
                angle = 20 + (feedback3 - 770) * ((26 - 20) / (840.0 - 770));
            else
                angle = 26 + (feedback3 - 840) * ((30 - 26) / (870.0 - 840));

            return Math.Round(angle, 1);
        }
    }
}