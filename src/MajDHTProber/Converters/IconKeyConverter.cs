using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

#nullable enable

namespace MajDHTProber.Converters
{
    /// <summary>Looks up an icon geometry by its resource key, so view models can name icons without referencing UI types.</summary>
    public sealed class IconKeyConverter : IValueConverter
    {
        /// <summary>Gets the shared instance.</summary>
        public static IconKeyConverter Instance { get; } = new IconKeyConverter();

        /// <summary>Converts a resource key into the geometry registered under it.</summary>
        /// <param name="value">The resource key.</param>
        /// <param name="targetType">The ignored target type.</param>
        /// <param name="parameter">The ignored parameter.</param>
        /// <param name="culture">The ignored culture.</param>
        /// <returns>The geometry, or null when the key is unknown.</returns>
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string key && Application.Current != null && Application.Current.TryFindResource(key, out var resource))
            {
                return resource as Geometry;
            }
            return null;
        }

        /// <summary>Conversion back is not supported.</summary>
        /// <param name="value">The ignored value.</param>
        /// <param name="targetType">The ignored target type.</param>
        /// <param name="parameter">The ignored parameter.</param>
        /// <param name="culture">The ignored culture.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="NotSupportedException">Always.</exception>
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
