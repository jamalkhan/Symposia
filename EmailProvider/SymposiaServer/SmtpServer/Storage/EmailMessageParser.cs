using System.Globalization;
using System.Text;

namespace NativeSmtpReceiver;

public static class EmailMessageParser
{
    public static ParsedMailboxMessage Parse(IReadOnlyList<string> dataLines)
    {
        var (headers, bodyLines) = SplitHeadersAndBody(dataLines);
        var headerLookup = headers
            .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        var body = ParseBodyPart(headers, bodyLines);

        return new ParsedMailboxMessage(
            GetHeaderValue(headerLookup, "From"),
            GetHeaderValue(headerLookup, "To"),
            GetHeaderValue(headerLookup, "Subject"),
            headers,
            body.PlainTextBody,
            body.HtmlBody);
    }

    private static (IReadOnlyList<ParsedEmailHeader> Headers, IReadOnlyList<string> BodyLines) SplitHeadersAndBody(IReadOnlyList<string> dataLines)
    {
        var rawHeaderLines = new List<string>();
        var bodyLines = new List<string>();
        var inHeaders = true;

        foreach (var line in dataLines)
        {
            if (inHeaders)
            {
                if (line.Length == 0)
                {
                    inHeaders = false;
                    continue;
                }

                rawHeaderLines.Add(line);
            }
            else
            {
                bodyLines.Add(line);
            }
        }

        return (ParseHeaders(rawHeaderLines), bodyLines);
    }

    private static IReadOnlyList<ParsedEmailHeader> ParseHeaders(IReadOnlyList<string> rawHeaderLines)
    {
        var headers = new List<ParsedEmailHeader>();
        string? currentName = null;
        var currentValue = new StringBuilder();

        foreach (var line in rawHeaderLines)
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && currentName is not null)
            {
                if (currentValue.Length > 0)
                {
                    currentValue.Append(' ');
                }

                currentValue.Append(line.Trim());
                continue;
            }

            if (currentName is not null)
            {
                headers.Add(new ParsedEmailHeader(currentName, currentValue.ToString()));
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                currentName = null;
                currentValue.Clear();
                continue;
            }

            currentName = line[..separatorIndex].Trim();
            currentValue.Clear();
            currentValue.Append(line[(separatorIndex + 1)..].Trim());
        }

        if (currentName is not null)
        {
            headers.Add(new ParsedEmailHeader(currentName, currentValue.ToString()));
        }

        return headers;
    }

    private static ParsedBodyPart ParseBodyPart(IReadOnlyList<ParsedEmailHeader> headers, IReadOnlyList<string> bodyLines)
    {
        var headerLookup = headers
            .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        var contentType = GetHeaderValue(headerLookup, "Content-Type") ?? "text/plain";
        var transferEncoding = GetHeaderValue(headerLookup, "Content-Transfer-Encoding");

        if (TryGetBoundary(contentType, out var boundary))
        {
            return ParseMultipartBody(bodyLines, boundary);
        }

        var decodedBody = DecodeBody(bodyLines, transferEncoding, contentType);
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedBodyPart(null, decodedBody);
        }

        return new ParsedBodyPart(decodedBody, null);
    }

    private static ParsedBodyPart ParseMultipartBody(IReadOnlyList<string> bodyLines, string boundary)
    {
        var plainTextBody = default(string);
        var htmlBody = default(string);
        var boundaryMarker = $"--{boundary}";
        var closingBoundaryMarker = $"--{boundary}--";
        List<string>? currentPartLines = null;

        foreach (var line in bodyLines)
        {
            if (string.Equals(line, boundaryMarker, StringComparison.Ordinal))
            {
                if (currentPartLines is not null)
                {
                    MergePart(ParsePartLines(currentPartLines), ref plainTextBody, ref htmlBody);
                }

                currentPartLines = new List<string>();
                continue;
            }

            if (string.Equals(line, closingBoundaryMarker, StringComparison.Ordinal))
            {
                if (currentPartLines is not null)
                {
                    MergePart(ParsePartLines(currentPartLines), ref plainTextBody, ref htmlBody);
                }

                break;
            }

            currentPartLines?.Add(line);
        }

        return new ParsedBodyPart(plainTextBody, htmlBody);
    }

    private static ParsedBodyPart ParsePartLines(IReadOnlyList<string> partLines)
    {
        var (headers, bodyLines) = SplitHeadersAndBody(partLines);
        return ParseBodyPart(headers, bodyLines);
    }

    private static void MergePart(ParsedBodyPart part, ref string? plainTextBody, ref string? htmlBody)
    {
        if (plainTextBody is null && !string.IsNullOrWhiteSpace(part.PlainTextBody))
        {
            plainTextBody = part.PlainTextBody;
        }

        if (htmlBody is null && !string.IsNullOrWhiteSpace(part.HtmlBody))
        {
            htmlBody = part.HtmlBody;
        }
    }

    private static string DecodeBody(IReadOnlyList<string> bodyLines, string? transferEncoding, string? contentType)
    {
        var bodyText = string.Join("\r\n", bodyLines);

        if (string.Equals(transferEncoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(string.Concat(bodyLines));
                return GetEncoding(contentType).GetString(bytes);
            }
            catch
            {
                return bodyText;
            }
        }

        if (string.Equals(transferEncoding, "quoted-printable", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeQuotedPrintable(bodyLines, GetEncoding(contentType));
        }

        return bodyText;
    }

    private static string DecodeQuotedPrintable(IReadOnlyList<string> bodyLines, Encoding encoding)
    {
        var buffer = new List<byte>();

        for (var lineIndex = 0; lineIndex < bodyLines.Count; lineIndex++)
        {
            var line = bodyLines[lineIndex];
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '=')
                {
                    if (i == line.Length - 1)
                    {
                        break;
                    }

                    if (i + 2 < line.Length &&
                        byte.TryParse(line.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    {
                        buffer.Add(value);
                        i += 2;
                        continue;
                    }
                }

                buffer.Add((byte)line[i]);
            }

            if (!line.EndsWith("=", StringComparison.Ordinal) && lineIndex < bodyLines.Count - 1)
            {
                buffer.Add((byte)'\r');
                buffer.Add((byte)'\n');
            }
        }

        return encoding.GetString(buffer.ToArray());
    }

    private static Encoding GetEncoding(string? contentType)
    {
        const string marker = "charset=";
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var charsetIndex = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (charsetIndex >= 0)
            {
                var charset = contentType[(charsetIndex + marker.Length)..]
                    .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0]
                    .Trim('"');

                try
                {
                    return Encoding.GetEncoding(charset);
                }
                catch
                {
                    // Fall back to UTF-8 below.
                }
            }
        }

        return Encoding.UTF8;
    }

    private static bool TryGetBoundary(string contentType, out string boundary)
    {
        const string marker = "boundary=";
        var boundaryIndex = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (boundaryIndex < 0)
        {
            boundary = string.Empty;
            return false;
        }

        boundary = contentType[(boundaryIndex + marker.Length)..]
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim('"');
        return !string.IsNullOrWhiteSpace(boundary);
    }

    private static string? GetHeaderValue(IReadOnlyDictionary<string, string> headers, string headerName)
    {
        return headers.TryGetValue(headerName, out var value) ? value : null;
    }

    private sealed record ParsedBodyPart(string? PlainTextBody, string? HtmlBody);
}
