using Illux.ImageTool.Options;
using Illux.ImageTool.Watch;

namespace Illux.ImageTool.Settings;

/// <summary>The "Resolution rules" section of <see cref="MainWindow"/>; the logic is in <see cref="RulesEditor"/>.</summary>
public partial class MainWindow
{
    private void WireRules()
    {
        RuleList.SelectionChanged += (_, _) => UpdateRuleButtons();
        RuleList.DoubleTapped += async (_, _) => await EditRuleAsync();
        RuleAddButton.Click += async (_, _) => await AddRuleAsync();
        RuleEditButton.Click += async (_, _) => await EditRuleAsync();
        RuleRemoveButton.Click += async (_, _) => await RemoveRuleAsync();
        RuleUpButton.Click += async (_, _) => await MoveRuleAsync(up: true);
        RuleDownButton.Click += async (_, _) => await MoveRuleAsync(up: false);
        RuleResetButton.Click += async (_, _) => await ResetRulesAsync();
    }

    private void ShowRules(int select = -1)
    {
        var rules = _config.ResolutionRules;
        RuleList.ItemsSource = rules.Select(RuleRow.From).ToList();
        RuleList.SelectedIndex = select >= 0 && select < rules.Count ? select : -1;
        RuleAddButton.IsEnabled = RuleResetButton.IsEnabled = _configUsable && _mayChange;
        UpdateRuleButtons();
    }

    private void UpdateRuleButtons()
    {
        var rules = _config.ResolutionRules;
        var i = RuleList.SelectedIndex;
        var selected = _configUsable && _mayChange && i >= 0 && i < rules.Count;
        var plain = selected && rules[i].Test != SizeTest.All; // "All other images" cannot be removed or moved
        RuleEditButton.IsEnabled = selected;
        RuleRemoveButton.IsEnabled = plain;
        RuleUpButton.IsEnabled = plain && i > 0;
        RuleDownButton.IsEnabled = plain && i + 1 < rules.Count && rules[i + 1].Test != SizeTest.All;
    }

    private ResolutionRule? SelectedRule()
    {
        var i = RuleList.SelectedIndex;
        return _configUsable && _mayChange && i >= 0 && i < _config.ResolutionRules.Count ? _config.ResolutionRules[i] : null;
    }

    private async Task AddRuleAsync()
    {
        if (!_configUsable || !_mayChange) return;
        var result = await new RuleWindow(null).ShowDialog<ResolutionRule?>(this);
        if (result is null) return;
        await ModifyRulesAsync(editor => editor.Add(result) ? IndexOf(editor, result) : null, keep: -1);
    }

    private async Task EditRuleAsync()
    {
        if (SelectedRule() is not { } original) return;
        var at = RuleList.SelectedIndex;
        var result = await new RuleWindow(original).ShowDialog<ResolutionRule?>(this);
        if (result is null) return;
        await ModifyRulesAsync(editor => editor.Replace(IndexOf(editor, original), result) ? IndexOf(editor, result) : null, keep: at);
    }

    private async Task RemoveRuleAsync()
    {
        if (SelectedRule() is not { } original) return;
        if (!await MessageWindow.AskAsync(this, $"Remove the rule \"{original.Name}\"?")) return;
        await ModifyRulesAsync(editor => editor.Remove(IndexOf(editor, original)) ? -1 : null, keep: RuleList.SelectedIndex);
    }

    private async Task MoveRuleAsync(bool up)
    {
        if (SelectedRule() is not { } original) return;
        await ModifyRulesAsync(editor =>
        {
            var at = IndexOf(editor, original);
            var moved = up ? editor.MoveUp(at) : editor.MoveDown(at);
            return moved >= 0 ? moved : null;
        }, keep: RuleList.SelectedIndex);
    }

    private async Task ResetRulesAsync()
    {
        if (!_configUsable || !_mayChange) return;
        if (!await MessageWindow.AskAsync(this, "Replace all rules with the Illux defaults?")) return;
        await ModifyRulesAsync(editor =>
        {
            editor.Reset();
            return -1;
        }, keep: -1);
    }

    private static int IndexOf(RulesEditor editor, ResolutionRule rule)
    {
        for (var i = 0; i < editor.Rules.Count; i++)
            if (editor.Rules[i] == rule) return i;
        return -1;
    }

    /// <summary>
    /// Re-reads the file first so a change made elsewhere meanwhile is not lost, applies the change, refuses to save a
    /// rule list that <see cref="ResolutionRules.Validate"/> rejects, then saves the way the folders are saved.
    /// </summary>
    private async Task ModifyRulesAsync(Func<RulesEditor, int?> change, int keep)
    {
        var select = keep; // the rule to select after the reload: the changed rule, or the old selection when nothing changed
        try
        {
            var config = ConfigStore.Load();
            var editor = new RulesEditor(config.ResolutionRules);
            if (change(editor) is not { } index)
                await ShowAsync("That rule was changed or removed somewhere else. The list was refreshed.");
            else if (editor.Validate() is { Count: > 0 } problems)
                await ShowAsync("The rules were not saved.\n" + string.Join("\n", problems));
            else
            {
                config.ResolutionRules = [.. editor.Rules];
                SettingsPlatform.SaveConfig(config);
                select = index;
            }
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            await ShowAsync("Could not save the settings.\n" + ex.Message);
        }
        await ReloadFoldersAsync();
        if (select >= 0) RuleList.SelectedIndex = Math.Min(select, _config.ResolutionRules.Count - 1);
    }
}
