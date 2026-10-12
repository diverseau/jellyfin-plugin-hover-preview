using System;
using System.Net;

namespace Jellyfin.Plugin.HoverPreview.Injection;

/// <summary>Adds the Hover Preview stylesheet and script tags to jellyfin-web's index.html.</summary>
public static class IndexHtmlInjector
{
    /// <summary>Marker attribute that makes injection idempotent (both injection paths may see the same page).</summary>
    public const string Marker = "data-hover-preview";

    /// <summary>Returns <paramref name="html"/> with the client files linked, or unchanged if already present.</summary>
    /// <param name="html">The original page.</param>
    /// <param name="baseUrl">Jellyfin's configured base URL ("" or "/jellyfin").</param>
    /// <param name="cacheToken">Version token appended to the URLs to bust caches on upgrade.</param>
    /// <returns>The page with tags added.</returns>
    public static string Inject(string html, string? baseUrl, string cacheToken)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var root = NormalizeBaseUrl(baseUrl);
        var token = WebUtility.UrlEncode(cacheToken);
        var css = $"<link rel=\"stylesheet\" {Marker} href=\"{root}/HoverPreview/client.css?v={token}\" />";
        var js = $"<script {Marker} defer src=\"{root}/HoverPreview/client.js?v={token}\"></script>";

        html = InsertBefore(html, "</head>", css);
        return InsertBefore(html, "</body>", js);
    }

    /// <summary>"jellyfin/" or "/jellyfin" -> "/jellyfin"; empty -> "". Rejects anything that isn't a plain path.</summary>
    internal static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '~'))
            {
                return string.Empty;
            }
        }

        return "/" + trimmed;
    }

    private static string InsertBefore(string html, string closingTag, string content)
    {
        var index = html.LastIndexOf(closingTag, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? html + content : html.Insert(index, content);
    }
}
