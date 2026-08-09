using Microsoft.VisualStudio.TestTools.UnitTesting;
using MissionPlanner.Utilities;
using System;
using System.Drawing;

namespace MissionPlanner.Utilities.Tests
{
    [TestClass]
    public class ModernUiTests
    {
        [TestMethod]
        public void BlendClampsAndInterpolatesChannels()
        {
            Color from = Color.FromArgb(10, 20, 30, 40);
            Color to = Color.FromArgb(110, 120, 130, 140);

            Assert.AreEqual(from, ModernUi.Blend(from, to, -1F));
            Assert.AreEqual(to, ModernUi.Blend(from, to, 2F));
            Assert.AreEqual(Color.FromArgb(60, 70, 80, 90), ModernUi.Blend(from, to, 0.5F));
        }

        [TestMethod]
        public void CorePaletteMaintainsOperationalContrast()
        {
            Assert.IsTrue(ContrastRatio(ModernUi.TextPrimary, ModernUi.Canvas) >= 7.0);
            Assert.IsTrue(ContrastRatio(ModernUi.TextSecondary, ModernUi.Surface) >= 4.5);
            Assert.IsTrue(ContrastRatio(ModernUi.AccentBright, ModernUi.Surface) >= 3.0);
        }

        [TestMethod]
        public void NamedNavigationIconProducesVisiblePixels()
        {
            using (var icon = ModernUi.CreateNamedIcon("MenuFlightData", 20))
            {
                Assert.AreEqual(20, icon.Width);
                Assert.AreEqual(20, icon.Height);

                bool hasVisiblePixel = false;
                for (int y = 0; y < icon.Height && !hasVisiblePixel; y++)
                {
                    for (int x = 0; x < icon.Width; x++)
                    {
                        if (icon.GetPixel(x, y).A > 0)
                        {
                            hasVisiblePixel = true;
                            break;
                        }
                    }
                }

                Assert.IsTrue(hasVisiblePixel);
            }
        }

        private static double ContrastRatio(Color foreground, Color background)
        {
            double lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
            double darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            return (0.2126 * Linearize(color.R)) +
                   (0.7152 * Linearize(color.G)) +
                   (0.0722 * Linearize(color.B));
        }

        private static double Linearize(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
