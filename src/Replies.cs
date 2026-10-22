using System.Globalization;
using System.Text.Json;

namespace ToolsMcp;

/// <summary>
/// What every tool says, the same way: prose for a person and the ids an agent needs next. An error starts with
/// "ERROR:" and carries its fix, and no exception escapes - an agent reads a reply, not a stack trace.
/// </summary>
internal static class Replies
{
    public const string InEncy = "In ENCY: the Tools catalogue extension lists your libraries - open it there.";
    public const string NotSignedIn = "ERROR: not signed in - run tools-mcp login";
    public const string SignInAgain = "ERROR: the sign-in has expired or was refused - run tools-mcp login";

    /// <summary>Runs a tool's body with the person's token, and turns every way it can go wrong into a reply.</summary>
    public static async Task<string> WithToken(TokenProvider tokens, Func<string, Task<string>> body)
    {
        string? token;
        try { token = await tokens.GetAccessToken(); }
        catch (Exception e) { return "ERROR: the ENCY sign-in server did not answer: " + e.Message; }
        if (token == null) return NotSignedIn;
        try { return await body(token); }
        catch (ToolsHttpException e) { return Explain(e); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return "ERROR: tools.encycam.com did not answer: " + e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "ERROR: could not write the file: " + e.Message;
        }
        catch (Exception e) { return "ERROR: " + e.Message; }
    }

    /// <summary>A refusal in words: 401 is a sign-in to renew; anything else is the server's own reason.</summary>
    public static string Explain(ToolsHttpException e)
    {
        if (e.Status == 401) return SignInAgain;
        string reason = ServerMessage(e.Body);
        return "ERROR: " + (reason.Length > 0 ? reason : "tools.encycam.com answered " + e.Status);
    }

    /// <summary>The "message" of the backend's error JSON ({"status":400,"message":"…"}), else the body itself.</summary>
    public static string ServerMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException) { }
        string trimmed = body.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }

    /// <summary>One component on one line, with its id - the handle every other tool takes.</summary>
    public static string Line(Component c)
    {
        var parts = new List<string>();
        if (c.Maker != null) parts.Add(c.Maker);
        parts.Add(c.KitName);
        parts.Add(c.Section);
        if (c.DiameterMm is { } d) parts.Add("Ø" + Mm(d) + " mm");
        if (c.LengthMm is { } l) parts.Add("L " + Mm(l) + " mm");
        parts.Add(c.Pullable ? "can go into a kit" : "cannot go into a kit");
        var files = new List<string>();
        if (c.Model) files.Add("model");
        if (c.Drawing) files.Add("drawing");
        if (c.Picture) files.Add("photo");
        if (files.Count > 0) parts.Add(string.Join("|", files));
        return $"{c.Name} [id {c.Id}] - " + string.Join(", ", parts);
    }

    /** 10, not 10.0; 7.5, not 7,5 - whatever the machine's culture. */
    public static string Mm(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
