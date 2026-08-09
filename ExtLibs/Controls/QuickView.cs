using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace MissionPlanner.Controls
{
    public partial class QuickView : SkiaSharp.Views.Desktop.SKControl
    {
        [System.ComponentModel.Browsable(true)]
        public string desc
        {
            get { return _desc; } set { if (_desc == value) return; _desc = value; Invalidate(); }
        }

        double _number = -9999;

        [System.ComponentModel.Browsable(true)]
        public double number
        {
            get { return _number; }
            set
            {
                lock (this)
                {
                    if (_number.Equals(value))
                        return;
                    _number = value;
                    Invalidate();
                }
            }
        }

        string _numberformat = "0.00";
        private string _desc = "";
        private Color _numbercolor;

        [System.ComponentModel.Browsable(true)]
        public string numberformat
        {
            get
            {
                return _numberformat;
            }
            set
            {
                if (_numberformat.Equals(value))
                    return;
                _numberformat = value;
                this.Invalidate();
            }
        }

        [System.ComponentModel.Browsable(true)]
        public Color numberColor { get { return _numbercolor; } set { if (_numbercolor == value) return; _numbercolor = value; Invalidate(); } }

        //We use this property as a backup store for the numberColor, so it is possible to change numberColor temporary.
        public Color numberColorBackup { get; set; }

        public QuickView()
        {
            InitializeComponent();

            PaintSurface+= OnPaintSurface;
        }

        private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e2)
        {
            var canvas = e2.Surface.Canvas;
            int w = e2.Info.Width;
            int h = e2.Info.Height;
            canvas.Clear(SKColor.Parse("#111517"));

            if (w <= 2 || h <= 2)
                return;

            SKRect card = new SKRect(1, 1, w - 1, h - 1);
            using (var cardPaint = new SKPaint { Color = SKColor.Parse("#171C1F"), Style = SKPaintStyle.Fill, IsAntialias = true })
            using (var borderPaint = new SKPaint { Color = SKColor.Parse("#354147"), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true })
            {
                canvas.DrawRoundRect(card, 6, 6, cardPaint);
                canvas.DrawRoundRect(card, 6, 6, borderPaint);
            }

            Color accentColor = numberColor.IsEmpty ? Color.FromArgb(62, 190, 235) : numberColor;
            using (var accentPaint = new SKPaint
            {
                Color = new SKColor(accentColor.R, accentColor.G, accentColor.B, 210),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            })
            {
                canvas.DrawRoundRect(new SKRect(1, 12, 4, Math.Max(13, h - 12)), 2, 2, accentPaint);
            }

            float labelSize = Math.Max(10f, Math.Min(15f, h * 0.12f));
            using (var labelTypeface = SKTypeface.FromFamilyName("Segoe UI Variable Text", SKTypefaceStyle.Normal))
            using (var labelPaint = new SKPaint
            {
                Color = SKColor.Parse("#9FACB3"),
                TextSize = labelSize,
                IsAntialias = true,
                Typeface = labelTypeface
            })
            {
                string label = desc ?? string.Empty;
                float labelWidth = labelPaint.MeasureText(label);
                float labelBaseline = Math.Max(labelSize + 7, h * 0.22f);
                canvas.DrawText(label, Math.Max(9, (w - labelWidth) / 2f), labelBaseline, labelPaint);
            }

            string value = number.ToString(numberformat);
            float valueSize = Math.Max(18f, h * 0.44f);
            using (var valueTypeface = SKTypeface.FromFamilyName("Segoe UI Variable Text", SKTypefaceStyle.Bold))
            using (var valuePaint = new SKPaint
            {
                Color = new SKColor(accentColor.R, accentColor.G, accentColor.B, accentColor.A),
                TextSize = valueSize,
                IsAntialias = true,
                Typeface = valueTypeface
            })
            {
                float availableWidth = Math.Max(1, w - 24);
                float measured = valuePaint.MeasureText(value);
                if (measured > availableWidth)
                {
                    valuePaint.TextSize = Math.Max(12f, valuePaint.TextSize * (availableWidth / measured));
                    measured = valuePaint.MeasureText(value);
                }

                SKFontMetrics metrics = valuePaint.FontMetrics;
                float contentTop = Math.Max(labelSize + 10, h * 0.25f);
                float baseline = contentTop + ((h - contentTop - (metrics.Descent + metrics.Ascent)) / 2f);
                canvas.DrawText(value, (w - measured) / 2f, baseline, valuePaint);
            }
        }

        public override void Refresh()
        {
            if (this.Visible)
                base.Refresh();
        }

        protected override void WndProc(ref Message m) // seems to crash here on linux... so try ignore it
        {
            try
            {
                base.WndProc(ref m);
            }
            catch { }
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            if (this.Visible && this.ThisReallyVisible())
                base.OnInvalidated(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            this.Invalidate();
        }
    }
}
