using System;
using System.Collections.Generic;

namespace AudiobookApp.Services
{
    /// <summary>
    /// Splits chapter plain text into speakable chunks for TTS.
    /// </summary>
    public static class EpubTextChunker
    {
        public const int DefaultMaxLength = 420;

        public static List<(int Offset, string Text)> SplitIntoChunks(
            string text,
            int startOffset,
            int maxLength = DefaultMaxLength)
        {
            var result = new List<(int, string)>();
            if (string.IsNullOrEmpty(text))
                return result;

            var i = Math.Clamp(startOffset, 0, text.Length);
            var maxLen = Math.Max(40, maxLength);

            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i >= text.Length)
                    break;

                var end = Math.Min(text.Length, i + maxLen);
                if (end < text.Length)
                {
                    var slice = text.AsSpan(i, end - i);
                    var lastBreak = LastSentenceBreak(slice);
                    if (lastBreak > maxLen / 3)
                        end = i + lastBreak + 1;
                }

                var chunk = text[i..end].Trim();
                if (chunk.Length > 0)
                    result.Add((i, chunk));
                i = end;
            }

            return result;
        }

        public static double ClampVolume(double volume) => Math.Clamp(volume, 0.0, 1.0);

        public static double ClampSpeakingRate(double rate) => Math.Clamp(rate, 0.5, 6.0);

        private static int LastSentenceBreak(ReadOnlySpan<char> slice)
        {
            for (var i = slice.Length - 1; i >= 0; i--)
            {
                var c = slice[i];
                if (c is '.' or '!' or '?' or ';' or '\n')
                    return i;
            }

            for (var i = slice.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(slice[i]))
                    return i;
            }

            return slice.Length - 1;
        }
    }
}
