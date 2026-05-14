using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace VenueRaffle.Windows;

/// <summary>
/// Settings window opened from Dalamud's plugin Settings button.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool showMacroVariables;

    public ConfigWindow(Plugin plugin)
        : base("Venue Raffle Settings###VenueRaffleConfig")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 430),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose()
    {
    }

    /// <summary>Renders the window each frame while it is visible.</summary>
    public override void Draw()
    {
        if (!ImGui.BeginTabBar("VenueRaffleSettingsTabs"))
            return;

        if (ImGui.BeginTabItem("Raffle Config"))
        {
            this.DrawRaffleConfig();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Macros"))
        {
            this.DrawMacrosTab();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawRaffleConfig()
    {
        var config = this.plugin.Configuration;

        ImGui.TextUnformatted("Prize Setup");
        ImGui.Separator();

        this.DrawLongInput(
            "Base Prize Pot",
            config.BasePot,
            value =>
            {
                config.BasePot = Math.Max(0, value);
                config.Save();
            });
        ImGui.TextDisabled("Extra prize amount added before ticket sales.");

        this.DrawLongInput(
            "Price Per Ticket",
            config.PricePerTicket,
            value =>
            {
                config.PricePerTicket = Math.Max(0, value);
                config.Save();
            });

        this.DrawPercentInput(
            "Venue / Host Share (%)",
            config.VenueHostSalesPercent,
            value =>
            {
                config.VenueHostSalesPercent = ClampPercent(value);
                config.Save();
            });
        ImGui.TextDisabled("Percentage kept by the venue/host from ticket sales before calculating the prize. Default is 0%.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Ticket Limits");
        ImGui.Separator();

        this.DrawIntInput(
            "Max Tickets Per Purchase",
            config.MaxTicketsPerPurchase,
            value =>
            {
                config.MaxTicketsPerPurchase = Math.Max(0, value);
                config.Save();
            });
        ImGui.TextDisabled("Recommended: 40. Set to 0 for unlimited per purchase.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Total Ticket Cap");
        ImGui.TextDisabled("999, fixed by FFXIV /random");
        ImGui.TextDisabled("FFXIV /random cannot roll higher than 999, so this raffle is hard-capped at 999 total tickets.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Rules");
        ImGui.Separator();

        ImGui.TextWrapped("Ticket numbers are assigned automatically from the current raffle entry total.");
        ImGui.TextWrapped("Prize pot = Base Prize Pot + ticket sales after venue/host share.");
        ImGui.TextWrapped("Draw Winner uses one public FFXIV /random roll.");
        ImGui.TextWrapped("Deleting an entry rebuilds later ticket ranges so the list stays continuous.");

        ImGui.Spacing();

        if (ImGui.Button("Open Main Window"))
            this.plugin.ToggleMainUi();
    }

    /// <summary>
    /// Draws editable chat templates.
    /// Multiline macros intentionally store only chat commands, while wait fields store timing.
    /// This keeps user macros readable and avoids FFXIV rejecting rapid chat messages.
    /// </summary>
    private void DrawMacrosTab()
    {
        var config = this.plugin.Configuration;

        ImGui.TextWrapped("Edit the messages sent by the raffle buttons. Use one full chat command per line, for example /sh message or /y message.");
        ImGui.TextWrapped("Wait boxes control delay between macro lines. You do not need to add <wait.N> manually.");
        ImGui.TextDisabled("Keep variables inside {curly braces}. They are replaced automatically when the macro runs.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Quick Actions");
        ImGui.Separator();

        if (ImGui.Button("Reset to Default Macros"))
        {
            config.ResetDefaultMacros();
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove Old Wait Lines"))
        {
            config.RaffleAdMacro = RemoveManualWaitLines(config.RaffleAdMacro);
            config.WinnerDrawMacro = RemoveManualWaitLines(config.WinnerDrawMacro);
            config.WinnerResultMacro = RemoveManualWaitLines(config.WinnerResultMacro);
            config.Save();
        }

        ImGui.TextDisabled("Remove Old Wait Lines removes old <wait.N> lines. Wait boxes handle spacing now.");

        ImGui.Spacing();
        if (ImGui.Button(this.showMacroVariables ? "Hide Variables" : "Show Variables"))
            this.showMacroVariables = !this.showMacroVariables;

        if (this.showMacroVariables)
        {
            ImGui.TextWrapped("Available variables: {Pot}, {PricePerTicket}, {MaxTickets}, {TotalTickets}, {TicketSales}, {GrossTicketSales}, {PrizeTicketSales}, {VenueHostPercent}, {VenueHostShare}, {Entries}, {TicketRange}, {TargetName}, {OtherName}, {WinnerName}, {WinningTicket}");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Tell Messages");
        ImGui.Separator();

        this.DrawSingleLineMacro("Tell Tickets to Target", config.TellTargetMessageTemplate, value =>
        {
            config.TellTargetMessageTemplate = value;
            config.Save();
        });

        this.DrawSingleLineMacro("Tell Other Name Tickets", config.TellOtherNameMessageTemplate, value =>
        {
            config.TellOtherNameMessageTemplate = value;
            config.Save();
        });

        ImGui.Spacing();
        ImGui.TextUnformatted("Announcement Macro");
        ImGui.Separator();
        this.DrawMacroWaitInput("Wait Between Lines", config.RaffleAdDelaySeconds, value =>
        {
            config.RaffleAdDelaySeconds = ClampWaitSeconds(value);
            config.Save();
        });
        this.DrawMultilineMacro("Shout Raffle Ad", config.RaffleAdMacro, value =>
        {
            config.RaffleAdMacro = value;
            config.Save();
        }, 120);

        ImGui.Spacing();
        ImGui.TextUnformatted("Winner Draw Macro");
        ImGui.Separator();
        this.DrawMacroWaitInput("Wait Between Lines##WinnerDraw", config.WinnerDrawDelaySeconds, value =>
        {
            config.WinnerDrawDelaySeconds = ClampWaitSeconds(value);
            config.Save();
        });
        this.DrawMultilineMacro("Draw Winner Before /random", config.WinnerDrawMacro, value =>
        {
            config.WinnerDrawMacro = value;
            config.Save();
        }, 150);

        ImGui.Spacing();
        ImGui.TextUnformatted("Winner Announcement Macro");
        ImGui.Separator();
        this.DrawMacroWaitInput("Wait Between Lines##WinnerAnnouncement", config.WinnerResultDelaySeconds, value =>
        {
            config.WinnerResultDelaySeconds = ClampWaitSeconds(value);
            config.Save();
        });
        this.DrawMultilineMacro("Winner Announcement After /random", config.WinnerResultMacro, value =>
        {
            config.WinnerResultMacro = value;
            config.Save();
        }, 100);
    }

    private void DrawMacroWaitInput(string label, float value, Action<float> onChanged)
    {
        var mutableValue = value;
        var visibleLabel = label;
        var idSuffix = label;
        var hiddenIdIndex = label.IndexOf("##", StringComparison.Ordinal);
        if (hiddenIdIndex >= 0)
        {
            visibleLabel = label[..hiddenIdIndex];
            idSuffix = label[(hiddenIdIndex + 2)..];
        }

        ImGui.SetNextItemWidth(90);
        if (ImGui.InputFloat($"##MacroWait{idSuffix}", ref mutableValue, 0.5f, 1f, "%.1f"))
            onChanged(mutableValue);

        ImGui.SameLine();
        ImGui.TextUnformatted($"{visibleLabel} (seconds)");
    }

    private static string RemoveManualWaitLines(string? macroText)
    {
        if (string.IsNullOrWhiteSpace(macroText))
            return string.Empty;

        var lines = macroText
            .Replace("\r", string.Empty)
            .Split('\n')
            .Where(line => !System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^<wait\.\d+(?:\.\d+)?>$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        return string.Join("\n", lines);
    }

    private static float ClampWaitSeconds(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 0f;

        return Math.Clamp(value, 0f, 60f);
    }

    private static float ClampPercent(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return 0f;

        return Math.Clamp(value, 0f, 100f);
    }

    private void DrawPercentInput(string label, float value, Action<float> onChanged)
    {
        var mutableValue = value;

        ImGui.SetNextItemWidth(170);
        if (ImGui.InputFloat($"##{label}", ref mutableValue, 0.5f, 5f, "%.1f"))
            onChanged(mutableValue);

        ImGui.SameLine(0, 12);
        ImGui.TextUnformatted(label);
    }

    private void DrawSingleLineMacro(string label, string value, Action<string> onChanged)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        var text = value ?? string.Empty;
        if (ImGui.InputText($"##{label}", ref text, 512))
            onChanged(text);
    }

    private void DrawMultilineMacro(string label, string value, Action<string> onChanged, float height)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        var text = value ?? string.Empty;
        if (ImGui.InputTextMultiline($"##{label}", ref text, 8192, new Vector2(-1, height)))
            onChanged(text);
    }

    private void DrawLongInput(string label, long value, Action<long> onChanged)
    {
        var text = value.ToString("N0", CultureInfo.InvariantCulture);

        ImGui.SetNextItemWidth(180);
        if (!ImGui.InputText(label, ref text, 32))
            return;

        text = text.Trim();
        if (long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
            onChanged(parsed);
    }

    private void DrawIntInput(string label, int value, Action<int> onChanged)
    {
        var mutableValue = value;

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(label, ref mutableValue))
            onChanged(mutableValue);
    }
}
