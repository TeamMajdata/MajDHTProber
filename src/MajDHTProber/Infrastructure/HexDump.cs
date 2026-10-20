using System;
using System.Globalization;
using System.Text;

#nullable enable

namespace MajDHTProber.Infrastructure
{
    /// <summary>Formats binary data as a classic offset, hexadecimal and ASCII dump.</summary>
    public static class HexDump
    {
        /// <summary>The number of bytes per line.</summary>
        public const int BytesPerLine = 16;

        /// <summary>Formats up to a maximum number of bytes.</summary>
        /// <param name="data">The data.</param>
        /// <param name="maximumBytes">The maximum number of bytes shown; a note reports the omitted remainder.</param>
        /// <returns>The dump text, one line per sixteen bytes.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The maximum is negative.</exception>
        public static string Format(ReadOnlySpan<byte> data, int maximumBytes = 4096)
        {
            if (maximumBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            }
            var shown = Math.Min(data.Length, maximumBytes);
            var builder = new StringBuilder(((shown / BytesPerLine) + 2) * 80);
            for (var offset = 0; offset < shown; offset += BytesPerLine)
            {
                var count = Math.Min(BytesPerLine, shown - offset);
                builder.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");
                for (var index = 0; index < BytesPerLine; index++)
                {
                    if (index < count)
                    {
                        builder.Append(data[offset + index].ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
                    }
                    else
                    {
                        builder.Append("   ");
                    }
                    if (index == 7)
                    {
                        builder.Append(' ');
                    }
                }
                builder.Append(" |");
                for (var index = 0; index < count; index++)
                {
                    var value = data[offset + index];
                    builder.Append(value >= 0x20 && value < 0x7f ? (char)value : '.');
                }
                builder.Append('|').Append('\n');
            }
            if (data.Length > shown)
            {
                builder.Append("… 其余 ").Append((data.Length - shown).ToString(CultureInfo.InvariantCulture)).Append(" 字节未显示\n");
            }
            return builder.ToString();
        }
    }
}
