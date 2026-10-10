using ChatBranch.Api.Endpoints;
using ChatBranch.Data;

namespace ChatBranch.Api.Auth;

/// <summary>
/// Publishes a GET's multi-entity filter (<c>?entity=a,b</c> or repeated
/// <c>entity=</c>) as <see cref="EntitySelection"/>, cut down to the entities the
/// caller may see (the switcher's own list, <c>AdminEndpoints.SwitcherEntitiesAsync</c>)
/// so a ticked slug can never widen anyone's scope. Writes never carry a selection:
/// a setting or a new row belongs to ONE level.
/// </summary>
public static class EntitySelectionMiddleware
{
    public static IApplicationBuilder UseEntitySelection(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            EntitySelection.Set(null);
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                && ctx.User.Identity?.IsAuthenticated == true
                && ctx.Request.Query.TryGetValue("entity", out var raw))
            {
                var slugs = raw.SelectMany(v => (v ?? "").Split(',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(s => s is not ("all" or "__all__")).Distinct().ToList();
                if (slugs.Count > 1)
                {
                    var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
                    var visible = (await AdminEndpoints.SwitcherEntitiesAsync(db, ctx.User))
                        .ToDictionary(e => e.Slug, e => e.Id);
                    EntitySelection.Set(slugs.Where(visible.ContainsKey).Select(s => visible[s]).ToList());
                }
            }
            await next(ctx);
        });
}
