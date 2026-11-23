using System.Text;
using HarScope.Core.Models;
using HarScope.Core.Parsing;

namespace HarScope.Core.Querying;

public enum BodySide { Request, Response }

/// <summary>
/// A decoded body. <see cref="Text"/> is set only for textual content (Base64 text is decoded);
/// binary or missing content carries a <see cref="Reason"/> instead.
/// </summary>
public sealed record BodyContent(
    bool Available,
    string? Text,
    string? MimeType,
    string? SourceEncoding = null,
    bool Binary = false,
    long? SizeBytes = null,
    string? Reason = null,
    IReadOnlyList<FormParam>? Params = null)
{
    public bool LooksLikeJson =>
        Text is not null &&
        ((MimeType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false) ||
         Text.AsSpan().TrimStart() is [ '{' or '[', ..]);
}

/// <summary>Single place that knows how HAR stores request/response bodies.</summary>
public static class BodyReader
{
    private const int MaxCommentChars = 300;
    private const int MaxParams = 200;

    public static BodyContent Read(HarEntry entry, BodySide side, int maxParamValueChars) =>
        side == BodySide.Request ? Request(entry, maxParamValueChars) : Response(entry);

    public static BodyContent Request(HarEntry entry, int maxParamValueChars)
    {
        var request = Json.Object(entry.Raw, "request");
        var post = request is { } r ? Json.Object(r, "postData") : null;
        if (post is not { } pd)
            return new BodyContent(false, null, null, Reason: "Request has no postData.");

        var mime = Json.String(pd, "mimeType");
        var text = Json.String(pd, "text");

        IReadOnlyList<FormParam>? formParams = null;
        var paramList = Json.Array(pd, "params").ToList();
        if (paramList.Count > 0)
        {
            formParams = paramList.Take(MaxParams).Select(p =>
            {
                var value = Json.String(p, "value");
                if (value is not null && value.Length > maxParamValueChars) value = value[..maxParamValueChars] + "…";
                return new FormParam(Json.String(p, "name") ?? "", value, Json.String(p, "fileName"), Json.String(p, "contentType"));
            }).ToList();
        }

        if (string.IsNullOrEmpty(text))
        {
            return formParams is null
                ? new BodyContent(false, null, mime, Reason: "postData.text is empty or absent.")
                : new BodyContent(true, null, mime, Params: formParams);
        }

        return new BodyContent(true, text, mime, Params: formParams);
    }

    public static BodyContent Response(HarEntry entry)
    {
        var response = Json.Object(entry.Raw, "response");
        var content = response is { } r ? Json.Object(r, "content") : null;
        if (content is not { } c)
            return new BodyContent(false, null, null,
                Reason: entry.Status == 0 ? "No response was recorded." : "Response has no content object.");

        var mime = Json.String(c, "mimeType");
        var text = Json.String(c, "text");
        var size = entry.ResponseBodySize;
        if (string.IsNullOrEmpty(text))
        {
            var reason = entry.Status == 0 ? "No response was recorded (request failed or was blocked)."
                : size is > 0 ? "Response body was not captured in the HAR (content.text is absent; size is known)."
                : "Response body is empty or was not captured.";
            if (Json.String(c, "comment") is { Length: > 0 } comment)
                reason += $" Exporter comment: {(comment.Length > MaxCommentChars ? comment[..MaxCommentChars] + "…" : comment)}";
            return new BodyContent(false, null, mime, SizeBytes: size, Reason: reason);
        }

        if (!entry.ResponseBase64)
            return IsTextual(mime) || mime is null
                ? new BodyContent(true, text, mime, SizeBytes: size)
                : new BodyContent(true, null, mime, Binary: true, SizeBytes: size, Reason: "Non-text content is not returned.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return new BodyContent(false, null, mime, "base64", Reason: "content.text is marked base64 but is not valid Base64.");
        }

        if (!IsTextual(mime) && (mime is not null || !LooksLikeUtf8Text(bytes)))
            return new BodyContent(true, null, mime, "base64", Binary: true, SizeBytes: bytes.Length,
                Reason: "Binary content is not returned.");

        return new BodyContent(true, Encoding.UTF8.GetString(bytes), mime, "base64", SizeBytes: bytes.Length);
    }

    public static bool IsTextual(string? mime)
    {
        if (mime is null) return false;
        var m = mime.ToLowerInvariant();
        return m.StartsWith("text/") || m.Contains("json") || m.Contains("xml") || m.Contains("javascript") ||
               m.Contains("ecmascript") || m.Contains("x-www-form-urlencoded") || m.Contains("graphql") ||
               m.Contains("yaml") || m.Contains("csv");
    }

    private static bool LooksLikeUtf8Text(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
