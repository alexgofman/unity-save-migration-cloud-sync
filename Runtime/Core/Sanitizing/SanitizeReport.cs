using System.Collections.Generic;
using System.Globalization;

namespace SaveSync
{
    /// <summary>
    /// One value the sanitizer changed.
    /// </summary>
    public readonly struct SanitizeCorrection
    {
        public SanitizeCorrection(string field, string from, string to)
        {
            Field = field;
            From = from;
            To = to;
        }

        public string Field { get; }

        public string From { get; }

        public string To { get; }

        public override string ToString()
        {
            return Field + ": " + From + " -> " + To;
        }
    }

    /// <summary>
    /// The corrections a sanitizer made to one state. The core has no logger; the host
    /// decides what to do with the list.
    /// </summary>
    public sealed class SanitizeReport
    {
        private readonly List<SanitizeCorrection> _corrections = new List<SanitizeCorrection>();

        public IReadOnlyList<SanitizeCorrection> Corrections => _corrections;

        public bool HasCorrections => _corrections.Count > 0;

        /// <summary>Records a correction the sanitizer made by hand.</summary>
        public void Corrected(string field, string from, string to)
        {
            _corrections.Add(new SanitizeCorrection(field, from, to));
        }

        /// <summary>
        /// Returns <paramref name="value"/> limited to [<paramref name="min"/>,
        /// <paramref name="max"/>], recording a correction when it had to change.
        /// </summary>
        public int Clamp(string field, int value, int min, int max)
        {
            int clamped = value < min ? min : (value > max ? max : value);
            if (clamped != value)
            {
                Corrected(field, value.ToString(CultureInfo.InvariantCulture), clamped.ToString(CultureInfo.InvariantCulture));
            }

            return clamped;
        }

        /// <inheritdoc cref="Clamp(string,int,int,int)"/>
        public long Clamp(string field, long value, long min, long max)
        {
            long clamped = value < min ? min : (value > max ? max : value);
            if (clamped != value)
            {
                Corrected(field, value.ToString(CultureInfo.InvariantCulture), clamped.ToString(CultureInfo.InvariantCulture));
            }

            return clamped;
        }
    }
}
