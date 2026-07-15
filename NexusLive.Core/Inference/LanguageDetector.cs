using System;
using System.Collections.Generic;

namespace NexusLive.Core.Inference
{
    public static class LanguageDetector
    {
        private static readonly HashSet<string> SpanishWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "el", "la", "los", "las", "un", "una", "y", "en", "que", "de", "es", "esta", "con", "para", "como", "si", "no", "o", "pero", "este", "del", "al"
        };

        private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "in", "that", "of", "is", "this", "with", "for", "as", "if", "not", "or", "but", "it", "to", "on", "are", "we", "you"
        };

        public static string Detect(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "es";

            // If text contains multiple lines, try to find the last transcript line
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string targetText = text;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.StartsWith('[') && line.Contains(']'))
                {
                    targetText = line;
                    break;
                }
            }

            int spanishCount = 0;
            int englishCount = 0;

            var tokens = targetText.Split(new[] { ' ', '.', ',', '!', '?', ';', ':', '[', ']', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (SpanishWords.Contains(token)) spanishCount++;
                if (EnglishWords.Contains(token)) englishCount++;
            }

            return spanishCount >= englishCount ? "es" : "en";
        }
    }
}
