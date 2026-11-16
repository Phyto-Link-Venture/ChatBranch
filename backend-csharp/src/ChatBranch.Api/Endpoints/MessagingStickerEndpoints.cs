using System.Security.Claims;
using System.Text.Json;
using ChatBranch.Api.Auth;
using ChatBranch.Data;
using ChatBranch.Data.Audit;
using ChatBranch.Data.Channels;
using ChatBranch.Data.Sidecars;
using ChatBranch.Data.Stores;
using ChatBranch.Domain;
using ChannelIds = ChatBranch.Domain.Channels;

namespace ChatBranch.Api.Endpoints;

/// <summary>
/// <c>POST /api/messaging/send-sticker</c> — send a WhatsApp STICKER.
///
/// Receiving one has always worked (the gateway opens animated Lottie wrappers too and
/// the inbox renders a sticker small with no bubble); sending one did not, because a
/// sticker is not just a picture. WhatsApp shows one only for a 512×512 WebP under its
/// own size ceiling, with an EXIF block naming the pack — push an arbitrary image
/// through as a <c>stickerMessage</c> and it renders wrong or is dropped. So the
/// shaping happens here, before the gateway: <see cref="StickerImage"/> scales and pads
/// the picture onto a transparent square, walks the quality down until it fits, and
/// stamps the pack.
///
/// The file comes from the same four references <c>POST /api/messaging/send-file</c>
/// takes — <c>media_url</c>, <c>source_message_id</c>, <c>file_id</c> (the media
/// library) or <c>file_base64</c> — read by that route's own loader, so the two cannot
/// drift. An animated GIF or a short clip becomes an animated sticker; an animated
/// WebP is passed through untouched, because ffmpeg cannot decode one to resize it.
///
/// QR-paired numbers only, and the refusal says which kind of "no" it is: the Cloud API
/// could carry a sticker and simply is not wired for it here, while the BSPs publish no
/// sticker send at all. Every other guard a manual send applies still applies — scope,
/// opt-out, the cold-outbound guard, the 24-hour window — and <c>status</c> is the
/// truth, so <c>sandbox</c> is never relabelled <c>sent</c>.
/// </summary>
public static class MessagingStickerEndpoints
{
    /// <summary>What a sticker's pack is called when the caller names nothing.</summary>
    public const string DefaultPack = "ChatBranch";

    public static void MapMessagingStickerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/messaging/send-sticker", async (JsonElement body, HttpContext ctx, AppDbContext db,
            ThreadStore store, ScopingService sc, OutboundSender sender, IWhatsAppCloudClient wa,
            MediaStore media, VoiceAgentBridge bridge, IRemoteMediaFetcher fetcher, ClaimsPrincipal me) =>
        {
            var channel = (MessagingSendFileEndpoints.Str(body, "channel") ?? "").Trim().ToLowerInvariant();
            var key = (MessagingSendFileEndpoints.Str(body, "key") ?? "").Trim();
            if (channel.Length == 0 || key.Length == 0)
                return Problem(422, "no_target", "Name the conversation with channel and key.");
            // only WhatsApp has stickers ChatBranch can send; say so before fetching anything
            if (channel != ChannelIds.WhatsApp)
                return Problem(422, "unsupported_channel",
                    $"Stickers can only be sent on WhatsApp — this conversation is on {channel}.");

            var force = MessagingSendFileEndpoints.Flag(body, "force");
            var pack = Trim(MessagingSendFileEndpoints.Str(body, "pack"), DefaultPack);
            var author = Trim(MessagingSendFileEndpoints.Str(body, "author"), "");
            var emojis = Emojis(body);

            // ---- the conversation -------------------------------------------
            var opened = await ThreadOpen.FindOrOpenAsync(db, store, sc, me, channel, GroupChats.WhatsAppKey(key),
                Pk(body, "thread_id"), MessagingSendFileEndpoints.Str(body, "company"));
            if (!opened.Ok)
                return opened.Pick == ThreadResolver.Outcome.Ambiguous
                    ? Problem(409, "company_required",
                        "This customer has a conversation in more than one of your companies — "
                        + "name one with thread_id or company.")
                    : Problem(403, "out_of_scope", "That conversation is outside your scope.");
            var thread = opened.Thread!;
            if (!ThreadAccessFilter.InScope(db, sc, me, thread))
                return Problem(403, "out_of_scope", "That conversation is outside your scope.");

            if (thread.OptedOut && !force)
                return Problem(409, "opted_out",
                    $"This contact sent STOP on {thread.Channel}. A manual exception needs force.");

            // ---- the picture -------------------------------------------------
            var loaded = await MessagingSendFileEndpoints.ReadFileAsync(body, db, sc, me, media, bridge, fetcher,
                StickerImage.MaxSourceBytes, ctx.RequestAborted);
            if (loaded.Error is { } problem) return problem;
            var source = loaded.File!;
            if (!StickerImage.CanMake(source.Mime))
                return Problem(422, "not_a_picture",
                    $"That file type ({source.Mime}) cannot be made into a sticker — "
                    + "send a picture, a GIF or a short clip.");

            // ---- route before converting, so a refusal costs no ffmpeg run ----
            var routing = new Routing(db, sc);
            var route = routing.ForThread(thread, RouteNeed.Conversation, apply: true);
            if (!route.Ok)
                return Problem(409, "no_account",
                    route.Error ?? "No WhatsApp number is available to send this conversation.");
            var account = route.Account;
            if (account != null && MessagingSendFileEndpoints.ColdBlocked(db, sc, thread, account) && !force)
                return Problem(409, "cold_sticker",
                    "This number has never received a message from this contact. Cold outbound from a personal "
                    + "number carries ban risk — have the contact message first.");
            if (account != null && wa.IsConfigured(account)
                && !MessagingSendFileEndpoints.WindowOpen(db, thread, account))
                return Problem(409, "window_closed",
                    "WhatsApp's 24-hour window is closed for this conversation — only a template can reopen it.");

            // ---- shape it ----------------------------------------------------
            var scratch = Path.Combine(Path.GetTempPath(), "cb-sticker-in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            StickerResult made;
            try
            {
                // a plain name under OUR temp dir, carrying the real extension so ffmpeg
                // picks the right demuxer — never the caller's file name on disk
                var input = Path.Combine(scratch, "in" + MediaStore.ExtFor(source.Mime, source.FileName));
                await File.WriteAllBytesAsync(input, source.Content, ctx.RequestAborted);
                made = await StickerImage.MakeAsync(input, source.Mime, pack,
                    author.Length > 0 ? author : BusinessName(sc, thread.EntityId), emojis, ctx.RequestAborted);
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch { /* temp */ }
            }
            if (!made.Ok) return Problem(422, "sticker_refused", made.Error);

            // ---- send --------------------------------------------------------
            var rel = await media.SaveAsync(ChannelIds.WhatsApp, thread.Key, made.Webp!, "webp");
            var (msg, error, conflict) = sender.SendSticker(thread, made.Webp!, rel, MessageSource.Agent,
                account?.Id);
            if (msg == null) return Problem(conflict ? 409 : 422, "send_failed", error);

            if (MessagingTransportEndpoints.IsHumanSender(me))
                AiControlsEndpoints.HumanReplied(db, thread, msg, me);

            AuditTrail.Add(db, "message.send_sticker", $"{thread.Channel}:{thread.Key}",
                $"{(made.Animated ? "animated" : "still")} sticker, {made.Webp!.Length} bytes, "
                + $"from {loaded.From} → {msg.Status}");
            await db.SaveChangesAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                channel = thread.Channel,
                key = thread.Key,
                thread_id = thread.Id,
                ok = msg.Status != "failed",
                // the truth, as everywhere: "sandbox" means nothing left the building
                status = msg.Status,
                message_id = msg.Id,
                external_id = msg.ExternalId,
                error = msg.Error,
                sticker = new
                {
                    animated = made.Animated,
                    size = made.Webp!.Length,
                    width = StickerImage.Size,
                    height = StickerImage.Size,
                    pack,
                    source = loaded.From,
                },
                url = $"/chats?thread={thread.Channel}:{thread.Key}",
            });
        }).RequireAuthorization(AuthPolicies.Authenticated).RequireCapability("chats.send");
    }

    private static IResult Problem(int status, string code, string detail) =>
        MessagingSendFileEndpoints.Problem(status, code, detail);

    private static string Trim(string? value, string fallback)
    {
        var v = (value ?? "").Trim();
        return v.Length > 0 ? v : fallback;
    }

    private static int? Pk(JsonElement body, string key) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>The emoji a viewer can find this sticker by (WhatsApp keeps up to three).</summary>
    private static List<string> Emojis(JsonElement body)
    {
        var list = new List<string>();
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("emojis", out var raw)) return list;
        if (raw.ValueKind == JsonValueKind.String)
        {
            var one = (raw.GetString() ?? "").Trim();
            if (one.Length > 0) list.Add(one);
            return list;
        }
        if (raw.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in raw.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            var v = (e.GetString() ?? "").Trim();
            if (v.Length > 0) list.Add(v);
            if (list.Count == 3) break;
        }
        return list;
    }

    /// <summary>The pack's publisher when the caller names none: the business's own name.</summary>
    private static string BusinessName(ScopingService sc, int? entityId)
    {
        try
        {
            var name = (sc.SettingValue("business_name", entityId) ?? "").Trim();
            return name.Length > 0 ? name : DefaultPack;
        }
        catch { return DefaultPack; }
    }
}
