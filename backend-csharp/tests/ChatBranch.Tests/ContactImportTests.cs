using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;
using ChatBranch.Data.Contacts;
using ChatBranch.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ChatBranch.Tests;

/// <summary>
/// Part D — contact-book import: CSV / .xlsx / .vcf (Android quoted-printable
/// 2.1, iPhone 3.0, 4.0) through one preview → mapping → commit pipeline,
/// E.164 phones, within-file merging, the three duplicate modes, row-numbered
/// errors, custom fields, the entity option bounded by scope, the caps, the
/// legacy mapping contract, and Google Contacts through a fake People API.
/// </summary>
public class ContactImportTests : IdentityApiBase, IClassFixture<IdentityFactory>
{
    public ContactImportTests(IdentityFactory factory) : base(factory) { }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName, Dictionary<string, string>? fields = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        foreach (var (k, v) in fields ?? new()) form.Add(new StringContent(v), k);
        return form;
    }

    private async Task<JsonElement> Import(HttpClient c, byte[] bytes, string fileName, bool preview,
        Dictionary<string, string>? fields = null)
    {
        var res = await c.PostAsync($"/api/contacts/import{(preview ? "?preview=1" : "")}", Upload(bytes, fileName, fields));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.IsSuccessStatusCode, body.ToString());
        return body;
    }

    private static string Target(JsonElement r, string header) =>
        r.GetProperty("columns").EnumerateArray().First(c => c.GetProperty("header").GetString() == header)
            .GetProperty("target").GetString()!;

    private Contact? ByPhone(string e164) =>
        InScope((_, db) => db.Contacts.AsNoTracking().FirstOrDefault(c => c.Phone == e164));

    // ---- CSV ---------------------------------------------------------------------

    [Fact]
    public async Task Csv_preview_suggests_the_mapping_then_the_commit_normalises_and_merges()
    {
        var admin = await Authed();
        var (a, b, d) = (NewNumber(), NewNumber(), NewNumber());
        var csv = "Full Name,Mobile,E-mail,Company,Tags\n"
                  + $"Ali Ahmad,0{a[2..5]}-{a[5..8]} {a[8..]},ALI@x.com,Acme,vip;wholesale\n"
                  + "No Reach,,,Nowhere,\n"
                  + $"Bala,+{b},bala@x.com,,\n"
                  + $"Bala Again,0{b[2..]},,BalaCo,regular\n"            // same phone → merged into Bala
                  + $"Dee,{d},not-an-email,,\n";
        var bytes = Encoding.UTF8.GetBytes(csv);

        var preview = await Import(admin, bytes, "book.csv", preview: true);
        Assert.Equal("name", Target(preview, "Full Name"));
        Assert.Equal("phone", Target(preview, "Mobile"));
        Assert.Equal("email", Target(preview, "E-mail"));
        Assert.Equal("company_name", Target(preview, "Company"));
        Assert.Equal("tags", Target(preview, "Tags"));
        Assert.Equal(5, preview.GetProperty("total").GetInt32());
        Assert.Equal(3, preview.GetProperty("created").GetInt32());
        Assert.Equal(1, preview.GetProperty("merged").GetInt32());
        var err = preview.GetProperty("errors").EnumerateArray().Single();
        Assert.Equal(3, err.GetProperty("row").GetInt32());                  // header is row 1
        Assert.Equal("no phone number or email", err.GetProperty("reason").GetString());
        Assert.Contains(preview.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("reason").GetString()!.Contains("not-an-email"));
        Assert.Null(ByPhone("+" + a));                                        // a preview writes nothing

        var done = await Import(admin, bytes, "book.csv", preview: false);
        Assert.Equal(3, done.GetProperty("created").GetInt32());
        var ali = ByPhone("+" + a)!;
        Assert.Equal("Ali Ahmad", ali.Name);
        Assert.Equal("ali@x.com", ali.Email);
        Assert.Equal(new[] { "vip", "wholesale" }, ali.Tags.OrderBy(t => t).ToArray());
        var bala = ByPhone("+" + b)!;
        Assert.Equal("Bala", bala.Name);
        Assert.Equal("BalaCo", bala.CompanyName);                              // filled from the merged row
        Assert.Contains("regular", bala.Tags);
        // identities discovered for the imported contacts (queued as part of the import)
        Assert.True(InScope((_, db) => db.ContactIdentities.Any(i => i.ContactId == ali.Id && i.Channel == "whatsapp")));
        Assert.True(InScope((_, db) => db.AuditLogs.Any(l => l.Action == "contacts.import" && l.Detail.Contains("created 3"))));
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("update_empty")]
    [InlineData("overwrite")]
    public async Task Duplicates_follow_the_chosen_mode(string mode)
    {
        var admin = await Authed();
        var phone = NewNumber();
        var id = await CreateContact(admin, "Existing Ed", "+" + phone);
        InScope((_, db) =>
        {
            db.Contacts.Single(c => c.Id == id).CompanyName = "OldCo";
            db.SaveChanges();
        });
        var csv = $"Name,Phone,Email,Company\nEdward New,0{phone[2..]},ed{phone}@x.com,NewCo\n";

        var preview = await Import(admin, Encoding.UTF8.GetBytes(csv), "dupes.csv", true, new() { ["mode"] = mode });
        Assert.Equal(1, preview.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, preview.GetProperty("created").GetInt32());
        var done = await Import(admin, Encoding.UTF8.GetBytes(csv), "dupes.csv", false, new() { ["mode"] = mode });
        var ed = InScope((_, db) => db.Contacts.AsNoTracking().Single(c => c.Id == id));
        switch (mode)
        {
            case "skip":
                Assert.Equal(1, done.GetProperty("skipped").GetInt32());
                Assert.Equal(("Existing Ed", "", "OldCo"), (ed.Name, ed.Email, ed.CompanyName));
                break;
            case "update_empty":
                Assert.Equal(1, done.GetProperty("updated").GetInt32());
                Assert.Equal(("Existing Ed", $"ed{phone}@x.com", "OldCo"), (ed.Name, ed.Email, ed.CompanyName));
                break;
            default:
                Assert.Equal(1, done.GetProperty("updated").GetInt32());
                Assert.Equal(("Edward New", $"ed{phone}@x.com", "NewCo"), (ed.Name, ed.Email, ed.CompanyName));
                break;
        }
    }

    // ---- xlsx --------------------------------------------------------------------

    /// <summary>A minimal real .xlsx: shared strings, an inline string and a NUMERIC phone cell.</summary>
    internal static byte[] Xlsx(string[] header, params object[][] rows)
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        string Col(int i) => ((char)('A' + i)).ToString();
        var strings = header.ToList();
        var sheet = new StringBuilder($"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\">");
        for (var i = 0; i < header.Length; i++) sheet.Append($"<c r=\"{Col(i)}1\" t=\"s\"><v>{i}</v></c>");
        sheet.Append("</row>");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 2}\">");
            for (var i = 0; i < rows[r].Length; i++)
            {
                var cell = $"{Col(i)}{r + 2}";
                switch (rows[r][i])
                {
                    case double or long or int:
                        sheet.Append($"<c r=\"{cell}\"><v>{Convert.ToString(rows[r][i], System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case string s when i % 2 == 1:
                        sheet.Append($"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(s)}</t></is></c>");
                        break;
                    case string s:
                        strings.Add(s);
                        sheet.Append($"<c r=\"{cell}\" t=\"s\"><v>{strings.Count - 1}</v></c>");
                        break;
                }
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");
        var sst = new StringBuilder($"<sst xmlns=\"{ns}\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
        foreach (var s in strings) sst.Append($"<si><t>{SecurityElement.Escape(s)}</t></si>");
        sst.Append("</sst>");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + content);
            }
            Entry("[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>"
                + "</Types>");
            Entry("_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Entry("xl/workbook.xml",
                $"<workbook xmlns=\"{ns}\" xmlns:r=\"{rel}\"><sheets><sheet name=\"Contacts\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Entry("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{rel}/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
            Entry("xl/worksheets/sheet1.xml", sheet.ToString());
            Entry("xl/sharedStrings.xml", sst.ToString());
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Xlsx_keeps_numeric_phones_and_maps_custom_fields()
    {
        var admin = await Authed();
        var schema = await admin.PutAsJsonAsync("/api/settings/custom-fields", new
        {
            customer = new[] { new { key = "member_tier", label = "Member Tier", kind = "select", options = new[] { "Gold", "Silver" } } },
        });
        schema.EnsureSuccessStatusCode();
        var (p1, p2) = (NewNumber(), NewNumber());
        var bytes = Xlsx(new[] { "Name", "Phone", "Member Tier", "Notes" },
            new object[] { "Xenia Lim", double.Parse(p1), "Gold", "likes calls" },
            new object[] { "Yusof", double.Parse(p2), "Silver", "" });

        var preview = await Import(admin, bytes, "Book.XLSX", preview: true);
        Assert.Equal("xlsx", preview.GetProperty("source").GetString());
        Assert.Equal("cf:member_tier", Target(preview, "Member Tier"));
        Assert.Equal(p1, preview.GetProperty("columns")[1].GetProperty("samples")[0].GetString());
        Assert.Contains(preview.GetProperty("targets").EnumerateArray(), t => t.GetProperty("value").GetString() == "cf:member_tier");

        await Import(admin, bytes, "Book.XLSX", preview: false);
        var x = ByPhone("+" + p1)!;
        Assert.Equal("Xenia Lim", x.Name);
        Assert.Equal("Gold", x.Attributes["member_tier"]?.ToString());
        Assert.Equal("likes calls", x.Notes);
        Assert.Equal("Silver", ByPhone("+" + p2)!.Attributes["member_tier"]?.ToString());
    }

    // ---- vCard -------------------------------------------------------------------

    private const string AndroidAndIphone =
        "BEGIN:VCARD\r\n"
        + "VERSION:2.1\r\n"
        + "N;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=E6=9D=8E;=E5=B0=8F=E9=BE=99;;;\r\n"
        + "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=E6=9D=8E=E5=B0=8F=E9=BE=99\r\n"
        + "TEL;HOME:03-2222 3333\r\n"
        + "TEL;CELL;PREF:+60 12-111 2222\r\n"
        + "EMAIL;HOME:li@example.com\r\n"
        + "NOTE;ENCODING=QUOTED-PRINTABLE;CHARSET=UTF-8:Line one=0D=0A=\r\n"
        + "Line two\r\n"
        + "END:VCARD\r\n"
        + "BEGIN:VCARD\r\n"
        + "VERSION:3.0\r\n"
        + "PRODID:-//Apple Inc.//iPhone OS 17.0//EN\r\n"
        + "N:Tan;Mei Ling;;;\r\n"
        + "FN:Mei Ling Tan\r\n"
        + "ORG:Kedai Runcit;\r\n"
        + "item1.TEL;type=CELL;type=VOICE;type=pref:012-555 6666\r\n"
        + "item2.TEL;type=WORK;type=VOICE:+65 6123 4567\r\n"
        + "item1.EMAIL;type=INTERNET;type=pref:mei@example.com\r\n"
        + "item1.X-ABLabel:_$!<Other>!$_\r\n"
        + "NOTE:Prefers\\, mornings\\nCall first\r\n"
        + "BDAY:1990-05-17\r\n"
        + "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQAAAQABAAD\r\n"
        + " /2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8Q\r\n"
        + "END:VCARD\r\n"
        + "BEGIN:VCARD\r\n"
        + "VERSION:4.0\r\n"
        + "FN:Ahmad Zaki\r\n"
        + "TEL;VALUE=uri;TYPE=\"cell,voice\";PREF=1:tel:+60-13-777-8888\r\n"
        + "EMAIL:zaki@example.com\r\n"
        + "BDAY:--0417\r\n"
        + "END:VCARD\r\n";

    [Fact]
    public void The_vcard_parser_reads_android_iphone_and_v4_cards()
    {
        var cards = VCardParser.Parse(AndroidAndIphone);
        Assert.Equal(3, cards.Count);
        var li = cards[0];
        Assert.Equal("李小龙", li.DisplayName);
        Assert.Equal(new[] { "+60 12-111 2222", "03-2222 3333" }, li.Phones);    // PREF first
        Assert.Equal("Line one\r\nLine two", li.Note);
        var mei = cards[1];
        Assert.Equal("Mei Ling Tan", mei.DisplayName);
        Assert.Equal("Kedai Runcit", mei.Organization);
        Assert.Equal("012-555 6666", mei.Phones[0]);
        Assert.Equal("Prefers, mornings\nCall first", mei.Note);
        Assert.Equal("1990-05-17", mei.Birthday);
        var zaki = cards[2];
        Assert.Equal("+60-13-777-8888", zaki.Phones.Single());
        Assert.Equal("--04-17", zaki.Birthday);
    }

    [Fact]
    public async Task A_vcf_file_imports_every_card_with_extra_numbers_kept()
    {
        var admin = await Authed();
        var vcf = AndroidAndIphone;
        var preview = await Import(admin, Encoding.UTF8.GetBytes(vcf), "contacts.vcf", preview: true);
        Assert.Equal("vcf", preview.GetProperty("source").GetString());
        Assert.Equal("phone", Target(preview, ImportReaders.ColPhone));
        Assert.Equal("other_phones", Target(preview, ImportReaders.ColOtherPhones));
        Assert.Equal("birthday", Target(preview, ImportReaders.ColBirthday));
        Assert.Equal(3, preview.GetProperty("total").GetInt32());

        await Import(admin, Encoding.UTF8.GetBytes(vcf), "contacts.vcf", preview: false,
            new() { ["mode"] = "overwrite" });
        var li = ByPhone("+60121112222")!;
        Assert.Equal("李小龙", li.Name);
        Assert.Equal("li@example.com", li.Email);
        Assert.Equal("+60322223333", li.Attributes["other_phones"]?.ToString());
        var mei = ByPhone("+60125556666")!;
        Assert.Equal("Kedai Runcit", mei.CompanyName);
        Assert.Equal("+6561234567", mei.Attributes["other_phones"]?.ToString());
        Assert.Equal("1990-05-17", mei.Attributes["birthday"]?.ToString());
        Assert.Equal("zaki@example.com", ByPhone("+60137778888")!.Email);
    }

    // ---- scope, options, caps, legacy contract -------------------------------------

    [Fact]
    public async Task The_entity_option_is_bounded_by_scope_and_tags_apply_to_every_row()
    {
        var admin = await Authed();
        var otherSlug = InScope((_, db) =>
        {
            var t = new Tenant { Name = "Other Co", Slug = "other-" + Guid.NewGuid().ToString("N")[..6],
                Status = "active", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Tenants.Add(t);
            db.SaveChanges();
            var e = new Entity { TenantId = t.Id, Name = "Other HQ", Slug = t.Slug + "-hq",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Entities.Add(e);
            db.SaveChanges();
            return e.Slug;
        });
        var p = NewNumber();
        var csv = Encoding.UTF8.GetBytes($"Name,Phone,Branch\nScoped Sue,+{p},nowhere-branch\n");

        var refused = await admin.PostAsync("/api/contacts/import", Upload(csv, "s.csv", new() { ["entity"] = otherSlug }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var done = await Import(admin, csv, "s.csv", false, new() { ["entity"] = "main", ["tags"] = "imported, spring-fair" });
        Assert.Contains(done.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("reason").GetString()!.Contains("nowhere-branch"));
        var sue = ByPhone("+" + p)!;
        var mainId = InScope((_, db) => db.Entities.Single(e => e.Slug == "main").Id);
        Assert.True(InScope((_, db) => db.ContactEntities.Any(l => l.ContactId == sue.Id && l.EntityId == mainId)));
        Assert.Equal(new[] { "imported", "spring-fair" }, sue.Tags.OrderBy(t => t).ToArray());

        var agent = await Authed("agent", "agent123");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync("/api/contacts/import", Upload(csv, "s.csv"))).StatusCode);
    }

    [Fact]
    public async Task Linking_an_existing_contact_to_the_entity_counts_as_an_update()
    {
        var admin = await Authed();
        var p = NewNumber();
        var id = await CreateContact(admin, "Link Only", "+" + p);
        var csv = Encoding.UTF8.GetBytes($"Name,Phone\nLink Only,+{p}\n");
        var fields = new Dictionary<string, string> { ["entity"] = "main" };

        var first = await Import(admin, csv, "l.csv", true, fields);
        Assert.Equal(1, first.GetProperty("updated").GetInt32());          // only the entity link is new
        await Import(admin, csv, "l.csv", false, fields);
        var mainId = InScope((_, db) => db.Entities.Single(e => e.Slug == "main").Id);
        Assert.True(InScope((_, db) => db.ContactEntities.Any(l => l.ContactId == id && l.EntityId == mainId)));
        var again = await Import(admin, csv, "l.csv", true, fields);
        Assert.Equal(0, again.GetProperty("updated").GetInt32());
        Assert.Equal(1, again.GetProperty("unchanged").GetInt32());
    }

    [Fact]
    public async Task The_caps_and_unsupported_files_are_refused_clearly()
    {
        var admin = await Authed();
        var big = new StringBuilder("Name,Phone\n");
        for (var i = 0; i <= ContactImporter.MaxRows; i++) big.Append("N,+6012").Append(i.ToString("0000000")).Append('\n');
        var tooMany = await admin.PostAsync("/api/contacts/import?preview=1",
            Upload(Encoding.UTF8.GetBytes(big.ToString()), "big.csv"));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("20,000", (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var huge = new byte[ContactImporter.MaxBytes + 10];
        Array.Fill(huge, (byte)'a');
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await admin.PostAsync("/api/contacts/import?preview=1", Upload(huge, "huge.csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsync("/api/contacts/import?preview=1", Upload(new byte[] { 1, 2 }, "old.xls"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsync("/api/contacts/import?preview=1", Upload(new byte[] { 1, 2 }, "photo.png"))).StatusCode);
    }

    [Fact]
    public async Task The_json_contract_and_the_legacy_field_mapping_still_work()
    {
        var admin = await Authed();
        var p = NewNumber();
        var res = await admin.PostAsJsonAsync("/api/contacts/import", new
        {
            rows = new[] { new Dictionary<string, string> { ["Nama"] = "Legacy Lia", ["Talian"] = "+" + p } },
            mapping = new Dictionary<string, string> { ["name"] = "Nama", ["phone"] = "Talian" },
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("created").GetInt32());
        Assert.Equal("Talian", body.GetProperty("mapping").GetProperty("phone").GetString());
        Assert.Equal("Legacy Lia", ByPhone("+" + p)!.Name);
    }

    /// <summary>
    /// The phone app's contact picker posts the contacts the operator ticked as
    /// JSON rows under the SAME column names a vCard is presented with
    /// (<c>frontend/src/lib/deviceContacts.ts</c> → <c>toImportRows</c>), so the
    /// mapping step suggests itself and the raw numbers the address book holds are
    /// normalised here rather than on the phone. This pins that contract — the
    /// app's rows go through the importer with no mapping and no backend of their
    /// own.
    /// </summary>
    [Fact]
    public async Task The_phone_pickers_rows_map_themselves_and_the_server_normalises_the_numbers()
    {
        var admin = await Authed();
        var (mobile, other) = (NewNumber(), NewNumber());
        var row = new Dictionary<string, string>
        {
            [ImportReaders.ColName] = "Siti Nurhaliza",
            [ImportReaders.ColFirst] = "Siti",
            [ImportReaders.ColLast] = "Nurhaliza",
            // as the phone holds it: a national number with the trunk 0 and spacing
            [ImportReaders.ColPhone] = $"0{mobile[2..5]}-{mobile[5..8]} {mobile[8..]}",
            [ImportReaders.ColOtherPhones] = "+" + other,
            [ImportReaders.ColEmail] = "SITI@example.com",
            [ImportReaders.ColOtherEmails] = "siti.work@example.com",
            [ImportReaders.ColCompany] = "Kedai Siti",
        };
        var body = new { rows = new[] { row }, source = "device" };

        var res = await admin.PostAsJsonAsync("/api/contacts/import?preview=1", body);
        var preview = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(res.IsSuccessStatusCode, preview.ToString());
        Assert.Equal("device", preview.GetProperty("source").GetString());
        Assert.Equal("name", Target(preview, ImportReaders.ColName));
        Assert.Equal("first_name", Target(preview, ImportReaders.ColFirst));
        Assert.Equal("last_name", Target(preview, ImportReaders.ColLast));
        Assert.Equal("phone", Target(preview, ImportReaders.ColPhone));
        Assert.Equal("other_phones", Target(preview, ImportReaders.ColOtherPhones));
        Assert.Equal("email", Target(preview, ImportReaders.ColEmail));
        Assert.Equal("other_emails", Target(preview, ImportReaders.ColOtherEmails));
        Assert.Equal("company_name", Target(preview, ImportReaders.ColCompany));
        Assert.Equal(1, preview.GetProperty("created").GetInt32());
        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        Assert.Null(ByPhone("+" + mobile));                                   // a preview writes nothing

        (await admin.PostAsJsonAsync("/api/contacts/import", body)).EnsureSuccessStatusCode();
        var siti = ByPhone("+" + mobile)!;                                    // E.164, with the org's country code
        Assert.Equal("Siti Nurhaliza", siti.Name);
        Assert.Equal("siti@example.com", siti.Email);
        Assert.Equal("Kedai Siti", siti.CompanyName);
        Assert.Equal("+" + other, siti.Attributes["other_phones"]?.ToString());
    }

    // ---- Google Contacts ------------------------------------------------------------

    [Fact]
    public async Task Google_contacts_need_the_callers_connection_then_go_through_the_same_pipeline()
    {
        var admin = await Authed();
        var notYet = await admin.GetAsync("/api/contacts/import/google");
        Assert.Equal(HttpStatusCode.Conflict, notYet.StatusCode);
        Assert.Contains("Connect Google first",
            (await notYet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        InScope((_, db) =>
        {
            var uid = db.Users.Single(u => u.Username == "admin").Id;
            db.Integrations.Add(new Integration
            {
                Provider = "google_contacts", OwnerUserId = uid, Status = "connected",
                Credentials = new() { ["access_token"] = "people-token", ["expires_at"] = DateTime.UtcNow.AddHours(1).ToString("o") },
                Scopes = new() { GooglePeopleClient.Scope }, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        });
        var (g1, g2) = (NewNumber(), NewNumber());
        Factory.People.People.Clear();
        Factory.People.People.Add(new GooglePerson("Gina Google", "Gina", "Google", new() { "+" + g1, "03-1234 5678" },
            new() { "gina@gmail.test" }, "Search Co", "met at expo", "1988-02-03"));
        Factory.People.People.Add(new GooglePerson("", "Hari", "Om", new() { "0" + g2[2..] }, new(), "", "", ""));

        var preview = await admin.GetFromJsonAsync<JsonElement>("/api/contacts/import/google");
        Assert.True(preview.GetProperty("connected").GetBoolean());
        Assert.Equal("google", preview.GetProperty("source").GetString());
        Assert.Equal(2, preview.GetProperty("created").GetInt32());
        Assert.Contains("people-token", Factory.People.Tokens);

        // commit = the JSON contract with the previewed rows + mapping
        var commit = await admin.PostAsJsonAsync("/api/contacts/import", new
        {
            rows = preview.GetProperty("rows"),
            column_mapping = preview.GetProperty("column_mapping"),
            source = "google",
        });
        commit.EnsureSuccessStatusCode();
        Assert.Equal(2, (await commit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32());
        var gina = ByPhone("+" + g1)!;
        Assert.Equal("Gina Google", gina.Name);
        Assert.Equal("Search Co", gina.CompanyName);
        Assert.Equal("+60312345678", gina.Attributes["other_phones"]?.ToString());
        Assert.Equal("Hari Om", ByPhone("+" + g2)!.Name);                   // first + last when no display name

        Factory.People.Error = "Google refused the request (403) — reconnect Google Contacts and allow access to your contacts.";
        try
        {
            var refused = await admin.GetAsync("/api/contacts/import/google");
            Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        }
        finally { Factory.People.Error = null; }
    }

    [Fact]
    public void A_google_people_page_parses_into_people()
    {
        using var doc = JsonDocument.Parse("""
        {"connections":[{"resourceName":"people/c1",
          "names":[{"displayName":"Pat Primary","givenName":"Pat","familyName":"Primary","metadata":{"primary":true}}],
          "phoneNumbers":[{"value":"012-000 1111"},{"value":"+60 13-000 2222","canonicalForm":"+60130002222","metadata":{"primary":true}}],
          "emailAddresses":[{"value":"pat@example.com"}],
          "organizations":[{"name":"Pat Co"}],
          "birthdays":[{"date":{"month":7,"day":9}}]}],
         "nextPageToken":null}
        """);
        var p = GooglePeopleClient.ParsePage(doc.RootElement).Single();
        Assert.Equal("Pat Primary", p.Name);
        Assert.Equal("+60130002222", p.Phones[0]);                            // primary + canonical form first
        Assert.Equal("--07-09", p.Birthday);
        Assert.Equal("Pat Co", p.Organization);
    }
}
