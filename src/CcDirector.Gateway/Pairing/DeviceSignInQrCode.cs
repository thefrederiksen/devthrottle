using CcDirector.Core.Utilities;
using QRCoder;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// Renders the "scan to sign in on a new device" QR code for the Gateway's Add-a-device window
/// (issue #856). The QR carries ONLY a plain sign-in URL - never a pairing code, token, or any other
/// secret. (Issue #469 deliberately removed the old secret-embedding QR endpoints; the primary
/// device-join path is now signing into the same DevThrottle account, and the cloud issues the
/// per-device key on that sign-in.) So this helper rejects anything that is not an absolute
/// http/https URL rather than silently encoding whatever it is handed.
///
/// <see cref="QRCoder.PngByteQRCode"/> emits PNG bytes with no System.Drawing dependency, so it works
/// headless / cross-platform.
/// </summary>
public static class DeviceSignInQrCode
{
    /// <summary>
    /// Render a QR code PNG for a plain absolute http/https sign-in URL.
    /// </summary>
    /// <param name="signInUrl">The sign-in URL to encode. Must be an absolute http or https URL.</param>
    /// <param name="pixelsPerModule">The size of each QR module in pixels (controls the output resolution).</param>
    /// <returns>The QR code as PNG bytes.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="signInUrl"/> is null/blank, not an absolute URI, or not http/https -
    /// the QR must only ever carry a plain sign-in URL, so a bad value fails loudly here rather than
    /// being encoded.
    /// </exception>
    public static byte[] RenderPng(string signInUrl, int pixelsPerModule = 6)
    {
        if (string.IsNullOrWhiteSpace(signInUrl))
            throw new ArgumentException("A sign-in URL is required to render the QR code.", nameof(signInUrl));

        if (!Uri.TryCreate(signInUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"The sign-in QR code only carries a plain http/https sign-in URL; got '{signInUrl}'.",
                nameof(signInUrl));
        }

        FileLog.Write($"[DeviceSignInQrCode] RenderPng: encoding sign-in URL host={uri.Host}, ppm={pixelsPerModule}");

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(signInUrl, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);

        FileLog.Write($"[DeviceSignInQrCode] RenderPng: rendered {png.Length} PNG byte(s)");
        return png;
    }
}
