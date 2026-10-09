using QRCoder;

namespace ChatBranch.Data.Contacts;

/// <summary>
/// A QR code for an invite link as a self-contained SVG (QRCoder, MIT): black
/// modules on white with the quiet zone, sized by viewBox so the page scales it.
/// Only ever fed our own URLs; null when encoding fails.
/// </summary>
public static class QrSvg
{
    public static string? Render(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1200) return null;
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data);
            return svg.GetGraphic(4, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
        }
        catch
        {
            return null;
        }
    }
}
