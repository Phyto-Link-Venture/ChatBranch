namespace ChatBranch.Api.TestLab;

/// <summary>Every Test lab scenario, in category order.</summary>
public static class LabCatalog
{
    public static readonly IReadOnlyList<LabScenario> All = Build();

    private static IReadOnlyList<LabScenario> Build()
    {
        var all = AiScenarios.All().Concat(ConversationScenarios.All()).Concat(OperationScenarios.All())
            .Concat(DataScenarios.All()).ToList();
        var order = LabCategories.All.Select(c => c.Id).ToList();
        var dup = all.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"Duplicate Test lab scenario id '{dup.Key}'.");
        var stray = all.FirstOrDefault(s => !order.Contains(s.Category));
        if (stray != null) throw new InvalidOperationException($"Scenario '{stray.Id}' has an unknown category.");
        return all.OrderBy(s => order.IndexOf(s.Category)).ToList();
    }

    public static LabScenario? Find(string id) => All.FirstOrDefault(s => s.Id == id);
}
