using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

#nullable enable

namespace MajDHTProber.Controls
{
    /// <summary>
    /// Draws a stroked line icon authored on a 24 × 24 grid, scaled to the control size and colored with the inherited
    /// foreground, so icons follow button and theme colors.
    /// </summary>
    public sealed class LineIcon : Control
    {
        /// <summary>The size of the grid icons are authored on.</summary>
        public const double GridSize = 24;

        /// <summary>Identifies the <see cref="Data"/> property.</summary>
        public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

        /// <summary>Identifies the <see cref="Foreground"/> property.</summary>
        public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<LineIcon>();

        /// <summary>Identifies the <see cref="StrokeThickness"/> property.</summary>
        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.8);

        /// <summary>Registers the properties that affect rendering.</summary>
        static LineIcon()
        {
            AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        }

        /// <summary>Gets or sets the icon geometry on the 24 × 24 grid.</summary>
        public Geometry? Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        /// <summary>Gets or sets the stroke brush; inherited from the surrounding text foreground by default.</summary>
        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        /// <summary>Gets or sets the stroke thickness on the 24 × 24 grid.</summary>
        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        /// <summary>Draws the icon centered in the control bounds.</summary>
        /// <param name="context">The drawing context.</param>
        public override void Render(DrawingContext context)
        {
            var data = Data;
            var brush = Foreground;
            var size = Math.Min(Bounds.Width, Bounds.Height);
            if (data == null || brush == null || size <= 0)
            {
                return;
            }
            var scale = size / GridSize;
            var offset = Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
            var pen = new Pen(brush, StrokeThickness, null, PenLineCap.Round, PenLineJoin.Round);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * offset))
            {
                context.DrawGeometry(null, pen, data);
            }
        }
    }
}
