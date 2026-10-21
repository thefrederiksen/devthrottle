using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE BUILT-IN MAIL DESK FACTORY'S DEFINITION TEMPLATE (Mail Desk mission, design 5.3): the folder
/// <c>MailDesk/Template/</c>, compiled into the Gateway binary as embedded resources the way the built-in skills are,
/// and read back here as the same map of path to text a person's import sends.
///
/// The folder holds <c>factory.yaml</c>, the Mail Desk seat <c>seats/mail-desk.yaml</c> (its brief inline, as the seat
/// format requires), the screening check <c>checks/screen.py</c>, and the Boss seat <c>seats/boss.yaml</c>: every
/// factory has a Boss (issue #4343). The Boss is the one the owner's own Mail Desk was given by hand on 21 October 2026,
/// byte for byte, so the template's version equals that definition's and the offer finds it already held. Nothing in it names a mailbox, an address or a
/// provider: the channels are Mail Desk data on the Gateway (design 5.3, decisions D19 and D20), so adding a mailbox is
/// a command and never a new template.
///
/// THE DEFAULT MACHINE IS A LITERAL. The definition format requires <c>defaults.machine</c> and a publish never fills
/// it in, so the template names <c>devlinux</c> (mission question 2). On an account with no Director on that machine
/// the check answers a notice, not an error, and the owner changes the machine in the draft before publishing it.
///
/// THE TEMPLATE VERSION is <see cref="VersionOf"/> the files: a hash of every path and its exact text. It is what the
/// offer (<see cref="MailDeskTemplateOffer"/>) compares, so "already offered this template" and "a newer template"
/// are one comparison, and it does not depend on a version number someone has to remember to raise. The files are
/// checked out with line feeds on every machine (<c>.gitattributes</c>), so one template has one version whichever
/// machine built the binary.
/// </summary>
public static class MailDeskTemplate
{
    /// <summary>The factory the template defines. <see cref="MailDeskStart.Is"/> reads it.</summary>
    public const string Factory = "mail-desk";

    /// <summary>The template's Mail Desk seat: the one its screening check starts.</summary>
    public const string Seat = "mail-desk";

    /// <summary>The screening check the seat's start runs. <see cref="MailDeskStart.Is"/> reads it.</summary>
    public const string CheckPath = "checks/screen.py";

    /// <summary>The template's Boss seat (issue #4343).</summary>
    public const string BossSeat = "boss";

    /// <summary>Every file the template holds, by its path in the definition, in ordinal order.</summary>
    public static readonly IReadOnlyList<string> ExpectedPaths = new[]
    {
        CheckPath,
        "factory.yaml",
        $"seats/{BossSeat}.yaml",
        $"seats/{Seat}.yaml",
    };

    /// <summary>What an embedded template file's resource name begins with; the rest is its path in the definition.</summary>
    internal const string ResourcePrefix = "CcDirector.Gateway.MailDesk.Template/";

    /// <summary>A strict reading: a template file that is not UTF-8 is a build defect, never quietly repaired.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Loaded> Template = new(Load);

    private sealed record Loaded(IReadOnlyDictionary<string, string> Files, string Version);

    /// <summary>The template's files, path to exact text, as embedded in this binary.</summary>
    public static IReadOnlyDictionary<string, string> Files => Template.Value.Files;

    /// <summary>The template version of this binary's template: <see cref="VersionOf"/> its files.</summary>
    public static string Version => Template.Value.Version;

    /// <summary>The first twelve characters of a template version, for a note or a log line a person reads.</summary>
    public static string Short(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Length <= 12 ? version : version[..12];
    }

    /// <summary>
    /// The template version of a set of definition files: SHA-256 (lower-case hex) over every path and the hash of its
    /// text as UTF-8, in ordinal path order. The same files always give the same version, and any change to a path or
    /// a single character gives another. A stored definition version is compared with the template by this.
    /// </summary>
    public static string VersionOf(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var canonical = JsonSerializer.Serialize(files
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new { path = f.Key, sha256 = Sha256Hex(Encoding.UTF8.GetBytes(f.Value)) })
            .ToArray());
        return Sha256Hex(Encoding.UTF8.GetBytes(canonical));
    }

    private static Loaded Load()
    {
        FileLog.Write("[MailDeskTemplate] Load: reading the embedded Mail Desk template");
        try
        {
            var assembly = typeof(MailDeskTemplate).Assembly;
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                // MSBuild writes the folder part with the building machine's separator; a definition path uses '/'.
                var path = name[ResourcePrefix.Length..].Replace('\\', '/');
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"The embedded Mail Desk template file '{name}' is listed but cannot be read.");
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files[path] = StrictUtf8.GetString(buffer.ToArray());
            }
            if (files.Count == 0)
                throw new InvalidOperationException(
                    "The Gateway binary holds no Mail Desk template: MailDesk/Template/ was not embedded. That is a build defect.");

            var version = VersionOf(files);
            FileLog.Write($"[MailDeskTemplate] Load: {files.Count} files ({string.Join(", ", files.Keys)}), version {Short(version)}");
            return new Loaded(files, version);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[MailDeskTemplate] Load FAILED: {ex.Message}");
            throw;
        }
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
