using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChatBranch.Data;
using ChatBranch.Data.Channels;
using ChatBranch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatBranch.Tests;

public class MediaLibraryFactory : ApiFactory
{
    public FakeMediaFetcher Fetcher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddScoped<IRemoteMediaFetcher>(_ => Fetcher));
    }
}

/// <summary>
/// The media library — a file uploaded ONCE and sent by its id. The third way to send
/// a file, next to a link and a message ChatBranch already holds, and the one that
/// covers a photo or a scan that exists only on somebody's phone.
/// </summary>
public class MediaLibraryTests : IClassFixture<MediaLibraryFactory>
{
    private readonly MediaLibraryFactory _f;
    public MediaLibraryTests(MediaLibraryFactory f) => _f = f;

    private T Db<T>(Func<AppDbContext, T> fn)
    {
        using var scope = _f.Services.CreateScope();
        return fn(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Number() => "6017" + Random.Shared.Next(1000000, 9999999);

    /// <summary>Upload a file the way the dashboard does (multipart).</summary>
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, string name, byte[] bytes,
        string mime = "application/pdf")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", name);
        return await c.PostAsync("/api/media/library", form);
    }

    [Fact]
    public async Task A_file_is_uploaded_once_and_then_sent_by_its_id()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 the invoice nobody has online");

        var uploaded = await Ok(await UploadAsync(admin, "invoice-nov.pdf", bytes));
        var fileId = uploaded.GetProperty("file_id").GetString()!;
        Assert.StartsWith("f_", fileId);
        Assert.Equal("invoice-nov.pdf", uploaded.GetProperty("name").GetString());
        Assert.Equal("document", uploaded.GetProperty("kind").GetString());
        Assert.Equal(bytes.Length, uploaded.GetProperty("size").GetInt32());
        // kept for a month unless someone deletes it
        Assert.True(uploaded.GetProperty("expires_at").GetDateTime() > DateTime.UtcNow.AddDays(29));

        // sending it costs the id, not the file
        var to = Number();
        var sent = await Ok(await admin.PostAsJsonAsync("/api/messaging/send-file", new
        {
            channel = "whatsapp", key = to, file_id = fileId, caption = "Your invoice",
        }));
        Assert.Equal("library", sent.GetProperty("file").GetProperty("source").GetString());
        Assert.Equal("invoice-nov.pdf", sent.GetProperty("file").GetProperty("name").GetString());
        Assert.Equal(1, sent.GetProperty("sent").GetInt32());

        var msg = Db(db =>
        {
            var t = db.Threads.Single(x => x.Channel == "whatsapp" && x.Key == to);
            return db.Messages.Single(m => m.ThreadId == t.Id);
        });
        Assert.Equal("document", msg.MediaType);
        Assert.Equal("Your invoice", msg.Text);

        // and the library notes that it went out
        var listed = await Ok(await admin.GetAsync("/api/media/library"));
        var row = Assert.Single(listed.GetProperty("files").EnumerateArray()
            .Where(f => f.GetProperty("file_id").GetString() == fileId));
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("last_sent_at").ValueKind);
    }

    [Fact]
    public async Task One_upload_goes_to_several_conversations()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var fileId = (await Ok(await UploadAsync(admin, "photo.jpg", new byte[2048], "image/jpeg")))
            .GetProperty("file_id").GetString()!;
        var wa = Number();
        var mail = $"lib-{Guid.NewGuid():N}"[..14] + "@example.com";

        var answer = await Ok(await admin.PostAsJsonAsync("/api/messaging/send-file", new
        {
            targets = new object[]
            {
                new { channel = "whatsapp", key = wa },
                new { channel = "email", key = mail, subject = "Photo" },
            },
            file_id = fileId,
        }));

        Assert.Equal(2, answer.GetProperty("sent").GetInt32());
        Assert.Equal("image", answer.GetProperty("file").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task The_library_is_searchable_by_name()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var tag = Guid.NewGuid().ToString("N")[..8];
        await Ok(await UploadAsync(admin, $"quote-{tag}.pdf", Encoding.UTF8.GetBytes("%PDF quote")));
        await Ok(await UploadAsync(admin, $"receipt-{tag}.pdf", Encoding.UTF8.GetBytes("%PDF receipt")));

        var hits = await Ok(await admin.GetAsync($"/api/media/library?q=quote-{tag}"));
        var row = Assert.Single(hits.GetProperty("files").EnumerateArray());
        Assert.Equal($"quote-{tag}.pdf", row.GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_file_can_be_fetched_back___and_deleted()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 fetch me back");
        var fileId = (await Ok(await UploadAsync(admin, "fetch.pdf", bytes))).GetProperty("file_id").GetString()!;

        var download = await admin.GetAsync($"/api/media/library/{fileId}/file");
        download.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        (await admin.DeleteAsync($"/api/media/library/{fileId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/media/library/{fileId}/file")).StatusCode);
        // and it can no longer be sent
        var sent = await admin.PostAsJsonAsync("/api/messaging/send-file", new
        {
            channel = "whatsapp", key = Number(), file_id = fileId,
        });
        Assert.Equal(HttpStatusCode.NotFound, sent.StatusCode);
        Assert.Contains("no_such_file", await sent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_upload_link_takes_the_bytes_and_can_only_be_used_once()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var link = await Ok(await admin.PostAsJsonAsync("/api/media/library/upload-url",
            new { file_name = "scan.pdf" }));
        var fileId = link.GetProperty("file_id").GetString()!;
        var url = link.GetProperty("upload_url").GetString()!;
        Assert.Equal("PUT", link.GetProperty("method").GetString());
        Assert.True(link.GetProperty("expires_at").GetDateTime() > DateTime.UtcNow);
        var path = new Uri(url).PathAndQuery;

        // the link is the credential: no bearer token at all
        var anonymous = _f.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 uploaded by link");
        var put = await anonymous.PutAsync(path, new ByteArrayContent(bytes));
        var stored = await Ok(put);
        Assert.Equal(fileId, stored.GetProperty("file_id").GetString());
        Assert.Equal("scan.pdf", stored.GetProperty("name").GetString());

        // once
        var again = await anonymous.PutAsync(path, new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // and it is a normal library file afterwards
        var sent = await Ok(await admin.PostAsJsonAsync("/api/messaging/send-file", new
        {
            channel = "whatsapp", key = Number(), file_id = fileId,
        }));
        Assert.Equal(1, sent.GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task A_tampered_or_invented_upload_link_is_refused()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var url = (await Ok(await admin.PostAsJsonAsync("/api/media/library/upload-url",
            new { file_name = "real.pdf" }))).GetProperty("upload_url").GetString()!;
        var path = new Uri(url).PathAndQuery;
        var anonymous = _f.CreateClient();

        // change one character of the signature and it is worthless
        var tampered = path[..^1] + (path[^1] == 'a' ? 'b' : 'a');
        Assert.Equal(HttpStatusCode.Forbidden,
            (await anonymous.PutAsync(tampered, new ByteArrayContent(new byte[8]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await anonymous.PutAsync("/api/media/library/u/not-a-token", new ByteArrayContent(new byte[8]))).StatusCode);
    }

    [Fact]
    public async Task An_upload_link_needs_a_file_name_with_an_extension()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        foreach (var name in new[] { "", "noextension" })
        {
            var res = await admin.PostAsJsonAsync("/api/media/library/upload-url", new { file_name = name });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.Contains("file_name", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task An_expired_upload_is_swept_away_with_its_bytes()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var fileId = (await Ok(await UploadAsync(admin, "old.pdf", Encoding.UTF8.GetBytes("%PDF old"))))
            .GetProperty("file_id").GetString()!;
        var relPath = Db(db => db.MediaLibrary.Single(m => m.FileId == fileId).Path);
        var onDisk = Db(db => _f.Services.GetRequiredService<MediaStore>().ResolveLibrary(relPath));
        Assert.NotNull(onDisk);

        // its keep-by date passes
        Db(db =>
        {
            db.MediaLibrary.Single(m => m.FileId == fileId).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            return db.SaveChanges();
        });

        // the next listing is also the sweep
        await Ok(await admin.GetAsync("/api/media/library"));

        Assert.False(Db(db => db.MediaLibrary.Any(m => m.FileId == fileId)));
        Assert.False(File.Exists(onDisk!));
    }

    [Fact]
    public async Task Another_outlets_upload_is_not_mine_to_send()
    {
        // an agent bound to one entity; the file belongs to another
        var sfx = Guid.NewGuid().ToString("N")[..8];
        var (username, otherEntity) = Db(db =>
        {
            var tenant = db.Entities.AsNoTracking().First();
            var now = DateTime.UtcNow;
            var mine = new Entity
            {
                Name = $"Lib mine {sfx}", Slug = $"lib-mine-{sfx}", TenantId = tenant.TenantId,
                CreatedAt = now, UpdatedAt = now,
            };
            var theirs = new Entity
            {
                Name = $"Lib theirs {sfx}", Slug = $"lib-theirs-{sfx}", TenantId = tenant.TenantId,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Entities.AddRange(mine, theirs);
            db.SaveChanges();
            var user = new AppUser
            {
                Username = $"libagent{sfx}", Password = DjangoPasswordHasher.Hash("agent123"), IsActive = true,
            };
            db.Users.Add(user);
            db.SaveChanges();
            var profile = new Profile { UserId = user.Id, Role = Roles.Agent, TenantId = tenant.TenantId };
            profile.Entities.Add(mine);
            db.Profiles.Add(profile);
            db.SaveChanges();
            return (user.Username, theirs.Id);
        });

        var admin = await Platform.As(_f, "admin", "admin123");
        var fileId = (await Ok(await UploadAsync(admin, "theirs.pdf", Encoding.UTF8.GetBytes("%PDF theirs"))))
            .GetProperty("file_id").GetString()!;
        Db(db =>
        {
            db.MediaLibrary.Single(m => m.FileId == fileId).EntityId = otherEntity;
            return db.SaveChanges();
        });

        var agent = await Platform.As(_f, username, "agent123");
        var listed = await Ok(await agent.GetAsync("/api/media/library"));
        Assert.DoesNotContain(listed.GetProperty("files").EnumerateArray(),
            f => f.GetProperty("file_id").GetString() == fileId);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/media/library/{fileId}/file")).StatusCode);
        var sent = await agent.PostAsJsonAsync("/api/messaging/send-file", new
        {
            channel = "whatsapp", key = Number(), file_id = fileId,
        });
        Assert.Equal(HttpStatusCode.NotFound, sent.StatusCode);
    }

    [Fact]
    public async Task A_read_only_key_can_look_but_not_upload_or_delete()
    {
        var readKey = Platform.MintKey(_f, "admin", "chats.view");
        var reader = _f.CreateClient();
        reader.DefaultRequestHeaders.Add("X-Api-Key", readKey);

        await Ok(await reader.GetAsync("/api/media/library"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await UploadAsync(reader, "nope.pdf", Encoding.UTF8.GetBytes("%PDF"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync("/api/media/library/upload-url", new { file_name = "nope.pdf" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.DeleteAsync("/api/media/library/f_whatever")).StatusCode);
    }

    [Fact]
    public async Task An_upload_bigger_than_any_channel_allows_is_refused()
    {
        var admin = await Platform.As(_f, "admin", "admin123");
        var res = await UploadAsync(admin, "huge.bin", new byte[21 * 1024 * 1024], "application/octet-stream");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }
}
