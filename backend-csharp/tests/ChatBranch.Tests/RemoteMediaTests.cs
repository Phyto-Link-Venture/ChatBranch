using System.Net;
using ChatBranch.Data.Channels;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// The rules behind <c>media_url</c>, as pure functions: which addresses the server may
/// fetch, what a Google Drive link means, and what the file ends up called. These are
/// the decisions that keep "the server downloads a link for you" from becoming "the
/// server reads its own network for you", so they are tested without a network at all.
/// </summary>
public class RemoteMediaTests
{
    private static IPAddress[] Ips(params string[] addresses) =>
        addresses.Select(IPAddress.Parse).ToArray();

    [Theory]
    // the loopback and the cloud metadata address are the two that matter most
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]      // carrier NAT
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("::1")]
    [InlineData("fe80::1")]         // link-local
    [InlineData("fd00::1")]         // unique-local
    [InlineData("::ffff:10.0.0.1")] // IPv4 in an IPv6 coat
    public void A_private_or_local_address_is_not_public(string ip) =>
        Assert.True(RemoteMediaRules.IsPrivate(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("142.250.185.78")]
    [InlineData("172.15.0.1")]      // just below the private block
    [InlineData("172.32.0.1")]      // just above it
    [InlineData("192.167.0.1")]
    [InlineData("100.63.0.1")]
    [InlineData("2606:4700::1111")]
    public void A_public_address_is_public(string ip) =>
        Assert.False(RemoteMediaRules.IsPrivate(IPAddress.Parse(ip)), ip);

    [Fact]
    public void Only_https_is_fetched()
    {
        Assert.Equal(RemoteMediaError.NotHttps,
            RemoteMediaRules.Refuse(new Uri("http://files.example.com/a.jpg"), Ips("1.1.1.1")));
        Assert.Null(RemoteMediaRules.Refuse(new Uri("https://files.example.com/a.jpg"), Ips("1.1.1.1")));
    }

    [Fact]
    public void A_name_that_resolves_inside_is_refused___however_public_it_looks()
    {
        // the SSRF case that a scheme check alone would miss
        Assert.Equal(RemoteMediaError.Blocked,
            RemoteMediaRules.Refuse(new Uri("https://totally-public.example.com/a.jpg"), Ips("10.0.0.7")));
        // one private answer among several is enough to refuse
        Assert.Equal(RemoteMediaError.Blocked,
            RemoteMediaRules.Refuse(new Uri("https://split.example.com/a.jpg"), Ips("1.1.1.1", "127.0.0.1")));
    }

    [Theory]
    [InlineData("https://localhost/a.jpg")]
    [InlineData("https://intranet/a.jpg")]          // no dot: never a public site
    [InlineData("https://files.localhost/a.jpg")]
    [InlineData("https://api.internal/a.jpg")]
    [InlineData("https://nas.local/a.jpg")]
    public void An_internal_name_is_refused_without_even_resolving(string url) =>
        Assert.Equal(RemoteMediaError.Blocked,
            RemoteMediaRules.Refuse(new Uri(url), Ips("1.1.1.1")));

    [Fact]
    public void A_host_that_resolves_to_nothing_is_a_not_found()
    {
        Assert.Equal(RemoteMediaError.NotFound,
            RemoteMediaRules.Refuse(new Uri("https://nope.example.com/a.jpg"), Array.Empty<IPAddress>()));
    }

    [Theory]
    // the three shapes people actually paste
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOp/view?usp=sharing", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/uc?export=download&id=1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://docs.google.com/file/d/1AbCdEfGhIjKlMnOp/edit", "1AbCdEfGhIjKlMnOp")]
    public void A_drive_share_link_gives_up_its_file_id(string url, string expected) =>
        Assert.Equal(expected, RemoteMediaRules.DriveFileId(new Uri(url)));

    [Theory]
    [InlineData("https://files.example.com/file/d/1AbCdEfGhIjKlMnOp/view")]  // not Google
    [InlineData("https://drive.google.com/drive/my-drive")]                  // a folder, not a file
    [InlineData("https://drive.google.com/open?id=short")]                    // not an id
    public void Anything_else_is_not_a_drive_file(string url) =>
        Assert.Equal("", RemoteMediaRules.DriveFileId(new Uri(url)));

    [Fact]
    public void A_drive_file_id_becomes_a_direct_download() =>
        Assert.Equal("https://drive.google.com/uc?export=download&id=ABC1234567",
            RemoteMediaRules.DriveDirectUrl("ABC1234567"));

    [Fact]
    public void The_file_name_comes_from_the_request___then_the_header___then_the_url()
    {
        var url = new Uri("https://files.example.com/a/b/invoice-nov.pdf?v=2");
        Assert.Equal("mine.pdf",
            RemoteMediaRules.FileName("mine.pdf", "attachment; filename=\"header.pdf\"", url, "application/pdf"));
        Assert.Equal("header.pdf",
            RemoteMediaRules.FileName(null, "attachment; filename=\"header.pdf\"", url, "application/pdf"));
        Assert.Equal("invoice-nov.pdf", RemoteMediaRules.FileName(null, null, url, "application/pdf"));
    }

    [Fact]
    public void A_name_with_no_extension_gets_one_from_the_type()
    {
        // the extension is what decides the kind on every channel, so there is always one
        var name = RemoteMediaRules.FileName(null, null,
            new Uri("https://files.example.com/download"), "image/jpeg");
        Assert.Equal(".jpg", Path.GetExtension(name));

        var fromNothing = RemoteMediaRules.FileName(null, null,
            new Uri("https://files.example.com/"), "application/pdf");
        Assert.Equal(".pdf", Path.GetExtension(fromNothing));
    }

    [Fact]
    public void A_name_cannot_escape_its_folder()
    {
        var name = RemoteMediaRules.FileName("../../etc/passwd", null,
            new Uri("https://files.example.com/x"), "text/plain");
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("..", name);
    }

    [Fact]
    public void Every_refusal_says_what_to_do_about_it()
    {
        foreach (var error in Enum.GetValues<RemoteMediaError>().Where(e => e != RemoteMediaError.None))
        {
            var words = RemoteMediaRules.Explain(error, "https://files.example.com/a.jpg", 16 * 1024 * 1024);
            Assert.False(string.IsNullOrWhiteSpace(words), error.ToString());
            Assert.DoesNotContain(error.ToString(), words);   // never the enum name at a person
        }
        // the size is named in megabytes, not bytes
        Assert.Contains("16 MB",
            RemoteMediaRules.Explain(RemoteMediaError.TooLarge, "https://x.example.com/a", 16 * 1024 * 1024));
        // and the no-access words point at the two ways out
        var noAccess = RemoteMediaRules.Explain(RemoteMediaError.NoAccess, "https://drive.google.com/x", 1);
        Assert.Contains("anyone who has the link", noAccess);
        Assert.Contains("Google", noAccess);
    }
}
