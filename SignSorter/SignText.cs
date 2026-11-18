using System;
using System.Text.RegularExpressions;

namespace SignSorter
{
    internal enum Arrow { None, Left, Right, Up, Down }

    /// <summary>Cleans raw sign text and pulls off a leading/trailing direction arrow.</summary>
    internal static class SignText
    {
        // Only real TextMeshPro tags are stripped, so "< Wood" keeps its arrow.
        private static readonly Regex RichText = new Regex(
            @"<\/?(?:b|i|u|s|color|size|sup|sub|mark|font|font-weight|material|align|alpha|cspace|indent|line-indent|line-height|lowercase|uppercase|smallcaps|allcaps|nobr|noparse|pos|space|style|voffset|width|sprite|link|gradient|rotate|br|margin|margin-left|margin-right|mspace|page|zwsp|nbsp|strikethrough|underline|cr)(?:[=\s][^<>]*)?\/?>|<#[0-9a-fA-F]{3,8}>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private struct Token
        {
            public readonly string Text;
            public readonly Arrow Arrow;
            public Token(string text, Arrow arrow) { Text = text; Arrow = arrow; }
        }

        // Longest first so "<-" wins over "<".
        private static readonly Token[] Prefixes =
        {
            new Token("<--", Arrow.Left), new Token("<-", Arrow.Left), new Token("<", Arrow.Left), new Token("←", Arrow.Left),
            new Token("-->", Arrow.Right), new Token("->", Arrow.Right), new Token(">", Arrow.Right), new Token("→", Arrow.Right),
            new Token("^", Arrow.Up), new Token("↑", Arrow.Up),
            new Token("↓", Arrow.Down),
        };

        private static readonly Token[] Suffixes =
        {
            new Token("-->", Arrow.Right), new Token("->", Arrow.Right), new Token(">", Arrow.Right), new Token("→", Arrow.Right),
            new Token("<--", Arrow.Left), new Token("<-", Arrow.Left), new Token("<", Arrow.Left), new Token("←", Arrow.Left),
            new Token("^", Arrow.Up), new Token("↑", Arrow.Up),
            new Token("↓", Arrow.Down),
        };

        public static string StripRichText(string text) =>
            string.IsNullOrEmpty(text) ? "" : RichText.Replace(text, " ");

        /// <summary>
        /// Splits "&lt; Wood, Stone" into (Left, "Wood, Stone"). An arrow at the start wins over
        /// one at the end; both are removed from the text. "v" counts as down only when it
        /// stands alone ("v Wood", "Wood v").
        /// </summary>
        public static Arrow ExtractArrow(string text, out string body)
        {
            body = (StripRichText(text) ?? "").Trim();
            Arrow arrow = Arrow.None;

            Arrow start = TakePrefix(ref body);
            Arrow end = TakeSuffix(ref body);
            if (start != Arrow.None) arrow = start;
            else if (end != Arrow.None) arrow = end;

            body = body.Trim();
            return arrow;
        }

        private static Arrow TakePrefix(ref string s)
        {
            foreach (Token t in Prefixes)
            {
                if (s.StartsWith(t.Text, StringComparison.Ordinal))
                {
                    s = s.Substring(t.Text.Length).TrimStart();
                    return t.Arrow;
                }
            }
            if (s.Length >= 2 && (s[0] == 'v' || s[0] == 'V') && char.IsWhiteSpace(s[1]))
            {
                s = s.Substring(2).TrimStart();
                return Arrow.Down;
            }
            if (s == "v" || s == "V")
            {
                s = "";
                return Arrow.Down;
            }
            return Arrow.None;
        }

        private static Arrow TakeSuffix(ref string s)
        {
            foreach (Token t in Suffixes)
            {
                if (s.EndsWith(t.Text, StringComparison.Ordinal))
                {
                    s = s.Substring(0, s.Length - t.Text.Length).TrimEnd();
                    return t.Arrow;
                }
            }
            int n = s.Length;
            if (n >= 2 && (s[n - 1] == 'v' || s[n - 1] == 'V') && char.IsWhiteSpace(s[n - 2]))
            {
                s = s.Substring(0, n - 1).TrimEnd();
                return Arrow.Down;
            }
            return Arrow.None;
        }
    }
}
