using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using VenueRaffle.Models;

namespace VenueRaffle.Windows;

/// <summary>
/// Main Venue Raffle ticket control panel.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private int ticketsToAdd = 1;
    private int ticketToFind = 1;
    private int saleIndexPendingDelete = -1;
    private string findResult = string.Empty;
    private string exportStatus = string.Empty;
    private string lastExportFilePath = string.Empty;
    private string lastExportDirectory = string.Empty;
    private string namedTicketsPlayerName = string.Empty;
    private System.Numerics.Vector2 popupCenter;
    private bool hasPopupCenter;

    private const string ClearStatisticsPopupName = "Clear Raffle Entries?";
    private const string UndoLastSalePopupName = "Undo Last Sale?";
    private const string DeleteEntryWindowName = "Delete Raffle Entry?###VenueRaffleDeleteEntryConfirm";

    public MainWindow(Plugin plugin)
        : base("Venue Raffle###VenueRaffleMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(820, 390),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue)
        };

        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
            Click = _ => this.plugin.ToggleConfigUi(),
            Priority = 10,
        });
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!this.plugin.LicenseService.IsLicensed)
        {
            this.DrawLicenseRequired();
            return;
        }

        this.CapturePopupCenterFromMainWindow();

        if (!ImGui.BeginTabBar("VenueRaffleTabs"))
            return;

        if (ImGui.BeginTabItem("Main"))
        {
            this.DrawMainTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Statistics"))
        {
            this.DrawStatisticsTab();
            ImGui.EndTabItem();
        }


        ImGui.EndTabBar();
    }


    private void DrawLicenseRequired()
    {
        ImGui.TextUnformatted("Venue Raffle");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("VenueRaffle is locked because no valid install-bound license is installed for this computer.");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Install ID: {this.plugin.Configuration.InstallId}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Install ID"))
            ImGui.SetClipboardText(this.plugin.Configuration.InstallId);
        ImGui.Spacing();
        if (ImGui.Button("Open License Settings", new System.Numerics.Vector2(180, 0)))
            this.plugin.ToggleConfigUi();
    }

    private void DrawMainTab()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        this.DrawQuickSetupRow(config);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawRaffleSummary(session);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        this.DrawMessageSection(session);
    }

    private void DrawStatisticsTab()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        ImGui.TextUnformatted("Ticket Summary");
        ImGui.Separator();

        if (ImGui.BeginTable("TicketStatisticsSummaryTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthFixed, 145);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 180);

            this.DrawSummaryRow("Entries", $"{config.SalesLedger.Count:N0}");
            this.DrawSummaryRow("Tickets", $"{session.TotalTickets:N0} / {Services.RaffleSession.MaxTotalTickets:N0}");
            this.DrawSummaryRow("Remaining", $"{session.RemainingTickets:N0}");
            this.DrawSummaryRow("Ticket Sales", $"{session.GrossTicketSalesTotalGil:N0} gil");
            if (session.VenueHostSalesPercent > 0f || session.VenueHostShareGil > 0)
            {
                this.DrawSummaryRow("Venue / Host Share", $"{session.VenueHostShareGil:N0} gil ({session.VenueHostSalesPercent:0.#}%)");
                this.DrawSummaryRow("Sales To Prize", $"{session.PrizeTicketSalesGil:N0} gil");
            }

            this.DrawSummaryRow("Total Pot", $"{session.TotalPot:N0} gil");

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Find Ticket");
        this.DrawFindTicketRow(session);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Entry Actions");

        if (ImGui.Button("Undo Last Sale", new System.Numerics.Vector2(140, 0)))
            ImGui.OpenPopup(UndoLastSalePopupName);

        ImGui.SameLine();

        if (this.DrawActionButton("Clear Raffle Entries", new System.Numerics.Vector2(160, 0), ButtonTone.Danger))
            ImGui.OpenPopup(ClearStatisticsPopupName);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Export");

        if (ImGui.Button("Export CSV", new System.Numerics.Vector2(110, 0)))
            this.ExportSalesLedgerToCsv();

        ImGui.SameLine();

        if (ImGui.Button("Open Export Folder", new System.Numerics.Vector2(160, 0)))
            this.OpenExportFolder();

        ImGui.SameLine();

        if (ImGui.Button("Copy Export Path", new System.Numerics.Vector2(145, 0)))
            this.CopyExportPath();

        this.DrawUndoLastSaleConfirmationPopup(session);
        this.DrawClearStatisticsConfirmationPopup(session);

        if (!string.IsNullOrWhiteSpace(this.exportStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(this.exportStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Raffle Entries");

        // Let the ledger fill the remaining vertical space in the Statistics tab.
        // This makes the table grow/shrink naturally when the plugin window is resized.
        var ledgerTableHeight = Math.Max(180.0f, ImGui.GetContentRegionAvail().Y);

        if (ImGui.BeginTable(
                "TicketLedgerTable",
                7,
                ImGuiTableFlags.Borders |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.Resizable |
                ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(0, ledgerTableHeight)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Tickets", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Range", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("Tell", ImGuiTableColumnFlags.WidthFixed, 95);
            ImGui.TableSetupColumn("Delete", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            for (var i = 0; i < config.SalesLedger.Count; i++)
            {
                var sale = config.SalesLedger[i];

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted((i + 1).ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(sale.PlayerName);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(sale.Tickets.ToString(CultureInfo.InvariantCulture));

                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(sale.TicketRange);

                ImGui.TableSetColumnIndex(4);
                ImGui.TextUnformatted($"{sale.Gil:N0}");

                ImGui.TableSetColumnIndex(5);
                if (this.DrawActionButton($"Tell Target##TellSale{i}", new System.Numerics.Vector2(86, 0), ButtonTone.Info))
                    this.plugin.TellTicketSaleToCurrentTarget(sale);

                ImGui.TableSetColumnIndex(6);
                if (this.DrawActionButton($"Delete##DeleteSale{i}", new System.Numerics.Vector2(58, 0), ButtonTone.Danger))
                {
                    this.saleIndexPendingDelete = i;
                }
            }

            ImGui.EndTable();
        }

        this.DrawFloatingDeleteEntryConfirmation(session);
    }

    private void DrawFloatingDeleteEntryConfirmation(Services.RaffleSession session)
    {
        if (this.saleIndexPendingDelete < 0)
            return;

        if (this.saleIndexPendingDelete >= this.plugin.Configuration.SalesLedger.Count)
        {
            this.saleIndexPendingDelete = -1;
            return;
        }

        var sale = this.plugin.Configuration.SalesLedger[this.saleIndexPendingDelete];
        var isOpen = true;

        this.CenterNextPopupInMainWindow();
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(390, 0), ImGuiCond.Always);

        if (!ImGui.Begin(DeleteEntryWindowName, ref isOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped($"Delete {sale.PlayerName}, tickets {sale.TicketRange}, cost {sale.Gil:N0} gil?");
        ImGui.TextDisabled("Later ticket ranges will be rebuilt automatically.");
        ImGui.Spacing();

        if (this.DrawActionButton("Yes, Delete Entry", new System.Numerics.Vector2(170, 0), ButtonTone.Danger))
        {
            session.RemoveSaleAt(this.saleIndexPendingDelete);
            this.saleIndexPendingDelete = -1;
            this.findResult = string.Empty;
            this.exportStatus = string.Empty;
            ImGui.End();
            return;
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new System.Numerics.Vector2(110, 0)))
            this.saleIndexPendingDelete = -1;

        ImGui.End();

        if (!isOpen)
            this.saleIndexPendingDelete = -1;
    }

    private void DrawFindTicketRow(Services.RaffleSession session)
    {
        ImGui.SetNextItemWidth(90);
        ImGui.InputInt("##FindTicket", ref this.ticketToFind);

        if (this.ticketToFind < 1)
            this.ticketToFind = 1;

        ImGui.SameLine();

        if (ImGui.Button("Find", new System.Numerics.Vector2(70, 0)))
        {
            var sale = session.FindTicket(this.ticketToFind);
            this.findResult = sale is null
                ? $"Ticket {this.ticketToFind:N0} was not found."
                : $"Ticket {this.ticketToFind:N0} belongs to {sale.PlayerName}, range {sale.TicketRange}.";
        }

        if (!string.IsNullOrWhiteSpace(this.findResult))
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(this.findResult);
        }
    }


    private void CapturePopupCenterFromMainWindow()
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        this.popupCenter = new System.Numerics.Vector2(
            windowPos.X + (windowSize.X * 0.5f),
            windowPos.Y + (windowSize.Y * 0.5f));
        this.hasPopupCenter = true;
    }

    private void CenterNextPopupInMainWindow()
    {
        if (!this.hasPopupCenter)
            this.CapturePopupCenterFromMainWindow();

        ImGui.SetNextWindowPos(
            this.popupCenter,
            ImGuiCond.Always,
            new System.Numerics.Vector2(0.5f, 0.5f));
    }

    private void DrawUndoLastSaleConfirmationPopup(Services.RaffleSession session)
    {
        var popupOpen = true;

        this.CenterNextPopupInMainWindow();

        if (!ImGui.BeginPopupModal(UndoLastSalePopupName, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (this.plugin.Configuration.SalesLedger.Count == 0)
        {
            ImGui.TextWrapped("There are no ticket sales to undo.");
            ImGui.Spacing();

            if (ImGui.Button("OK", new System.Numerics.Vector2(100, 0)))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
            return;
        }

        var lastSale = this.plugin.Configuration.SalesLedger[^1];
        ImGui.TextWrapped($"Undo the last sale for {lastSale.PlayerName}, tickets {lastSale.TicketRange}, cost {lastSale.Gil:N0} gil?");
        ImGui.Spacing();

        if (ImGui.Button("Yes, Undo Last Sale", new System.Numerics.Vector2(170, 0)))
        {
            session.RemoveLastSale();
            this.findResult = string.Empty;
            this.exportStatus = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new System.Numerics.Vector2(100, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawClearStatisticsConfirmationPopup(Services.RaffleSession session)
    {
        var popupOpen = true;

        this.CenterNextPopupInMainWindow();

        if (!ImGui.BeginPopupModal(ClearStatisticsPopupName, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("This will permanently clear the saved raffle entries.");

        ImGui.Spacing();

        if (ImGui.Button("Yes, Clear Raffle Entries", new System.Numerics.Vector2(190, 0)))
        {
            session.ClearSalesLedger();
            this.exportStatus = string.Empty;
            this.findResult = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new System.Numerics.Vector2(100, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawQuickSetupRow(Configuration config)
    {
        ImGui.TextUnformatted("Target");

        var quickRowAvailable = ImGui.GetContentRegionAvail().X;
        var targetInputWidth = Math.Max(360.0f, quickRowAvailable - 240.0f);
        ImGui.SetNextItemWidth(targetInputWidth);

        var targetName = config.TargetPlayerName;
        if (ImGui.InputText("##TargetPlayer", ref targetName, 128))
        {
            config.TargetPlayerName = targetName;
            config.Save();
        }

        ImGui.SameLine();

        if (ImGui.Button("Use Current Target", new System.Numerics.Vector2(150, 0)))
            this.plugin.SetTargetFromCurrentGameTarget();

        ImGui.SameLine();

        if (this.DrawActionButton("Clear", new System.Numerics.Vector2(70, 0), ButtonTone.Neutral))
        {
            config.TargetPlayerName = string.Empty;
            config.Save();
        }

        ImGui.Spacing();

        ImGui.TextUnformatted($"Tickets ({this.plugin.RaffleSession.RemainingTickets:N0} remaining, max {Services.RaffleSession.MaxTotalTickets:N0})");

        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("##Tickets", ref this.ticketsToAdd);

        if (this.ticketsToAdd < 1)
            this.ticketsToAdd = 1;

        ImGui.SameLine();

        if (this.DrawActionButton("Add Tickets", new System.Numerics.Vector2(180, 0), ButtonTone.Success))
            this.plugin.AddTicketsForConfiguredTarget(this.ticketsToAdd);
    }

    private void DrawRaffleSummary(Services.RaffleSession session)
    {
        var config = this.plugin.Configuration;

        ImGui.TextUnformatted("Raffle Summary");
        ImGui.Separator();

        if (ImGui.BeginTable("RaffleSummaryTable", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthFixed, 145);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 180);

            this.DrawSummaryRow("Entries", $"{session.EntryCount:N0}");
            this.DrawSummaryRow("Tickets", $"{session.TotalTickets:N0} / {Services.RaffleSession.MaxTotalTickets:N0}");
            this.DrawSummaryRow("Remaining", $"{session.RemainingTickets:N0}");
            this.DrawSummaryRow("Ticket Sales", $"{session.GrossTicketSalesTotalGil:N0} gil");
            if (session.VenueHostSalesPercent > 0f || session.VenueHostShareGil > 0)
            {
                this.DrawSummaryRow("Venue / Host Share", $"{session.VenueHostShareGil:N0} gil ({session.VenueHostSalesPercent:0.#}%)");
                this.DrawSummaryRow("Sales To Prize", $"{session.PrizeTicketSalesGil:N0} gil");
            }

            this.DrawSummaryRow("Base Pot", $"{config.BasePot:N0} gil");
            this.DrawSummaryRow("Total Pot", $"{session.TotalPot:N0} gil");

            ImGui.EndTable();
        }

        if (session.LastSale is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextUnformatted("Last Sale");
            ImGui.TextWrapped($"{session.LastSale.PlayerName} received tickets {session.LastSale.TicketRange}. Cost: {session.LastSale.Gil:N0} gil.");
        }

    }

    private void DrawSummaryRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value);
    }

    private void DrawMessageSection(Services.RaffleSession session)
    {
        ImGui.TextUnformatted("Messages");
        ImGui.Separator();
        ImGui.TextDisabled("Ticket tells are sent to your current target with /tell <t>.");

        if (ImGui.Button("Tell Tickets to Target", new System.Numerics.Vector2(180, 0)))
            this.plugin.TellLastBuyerTickets();

        ImGui.Spacing();
        ImGui.TextUnformatted("Other Recipient");

        var otherRecipientButtonWidth = 180.0f;
        var otherRecipientInputWidth = 420.0f;
        ImGui.SetNextItemWidth(otherRecipientInputWidth);
        ImGui.InputText("##NamedTicketsPlayer", ref this.namedTicketsPlayerName, 128);

        ImGui.SameLine();

        if (ImGui.Button("Tell Other Name Tickets", new System.Numerics.Vector2(otherRecipientButtonWidth, 0)))
            this.plugin.TellLastBuyerTicketsForNamedPerson(this.namedTicketsPlayerName);

        ImGui.TextDisabled("Name shown in the ticket message, for partner/friend purchases.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Announcements");

        if (this.DrawActionButton("Shout Raffle Ad", new System.Numerics.Vector2(180, 0), ButtonTone.Info))
            this.plugin.ShoutRaffleAnnouncement();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Winner");
        ImGui.TextDisabled("Draws with /random using the current ticket total. Max tickets: 999.");

        if (this.DrawActionButton("Draw Winner", new System.Numerics.Vector2(180, 0), ButtonTone.Primary))
            this.plugin.QueueWinnerDraw();
    }

    private enum ButtonTone
    {
        Neutral,
        Success,
        Info,
        Primary,
        Danger,
    }

    private bool DrawActionButton(string label, System.Numerics.Vector2 size, ButtonTone tone)
    {
        this.PushButtonTone(tone);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private bool DrawSmallActionButton(string label, ButtonTone tone)
    {
        this.PushButtonTone(tone);
        var clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private void PushButtonTone(ButtonTone tone)
    {
        var (normal, hovered, active) = tone switch
        {
            ButtonTone.Success => (
                new System.Numerics.Vector4(0.12f, 0.38f, 0.20f, 1f),
                new System.Numerics.Vector4(0.16f, 0.48f, 0.26f, 1f),
                new System.Numerics.Vector4(0.10f, 0.30f, 0.16f, 1f)),
            ButtonTone.Info => (
                new System.Numerics.Vector4(0.12f, 0.26f, 0.42f, 1f),
                new System.Numerics.Vector4(0.16f, 0.34f, 0.54f, 1f),
                new System.Numerics.Vector4(0.10f, 0.21f, 0.34f, 1f)),
            ButtonTone.Primary => (
                new System.Numerics.Vector4(0.45f, 0.25f, 0.08f, 1f),
                new System.Numerics.Vector4(0.58f, 0.32f, 0.10f, 1f),
                new System.Numerics.Vector4(0.36f, 0.20f, 0.06f, 1f)),
            ButtonTone.Danger => (
                new System.Numerics.Vector4(0.42f, 0.12f, 0.12f, 1f),
                new System.Numerics.Vector4(0.55f, 0.16f, 0.16f, 1f),
                new System.Numerics.Vector4(0.32f, 0.09f, 0.09f, 1f)),
            _ => (
                new System.Numerics.Vector4(0.30f, 0.30f, 0.30f, 1f),
                new System.Numerics.Vector4(0.38f, 0.38f, 0.38f, 1f),
                new System.Numerics.Vector4(0.24f, 0.24f, 0.24f, 1f)),
        };

        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
    }

    /// <summary>
    /// Exports the current raffle entries to a CSV file under Documents\VenueRaffle.
    /// The export includes raw sales plus summary totals so venue staff can reconcile gil later.
    /// </summary>
    private void ExportSalesLedgerToCsv()
    {
        var config = this.plugin.Configuration;
        var session = this.plugin.RaffleSession;

        if (config.SalesLedger.Count == 0)
        {
            this.exportStatus = "Nothing to export. Raffle entries are empty.";
            return;
        }

        try
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (string.IsNullOrWhiteSpace(documentsPath))
            {
                this.exportStatus = "CSV export failed: Documents folder could not be found.";
                return;
            }

            var exportDirectory = Path.Combine(documentsPath, "VenueRaffle");
            Directory.CreateDirectory(exportDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var filePath = Path.Combine(exportDirectory, $"VenueRaffle-sales-{timestamp}.csv");

            var csv = new StringBuilder();

            csv.AppendLine("Row,CreatedAtLocal,Player,Tickets,TicketNumbers,Gil");

            for (var i = 0; i < config.SalesLedger.Count; i++)
            {
                var sale = config.SalesLedger[i];

                csv.AppendCsvField((i + 1).ToString(CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.AppendCsvField(sale.CreatedAtLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.AppendCsvField(sale.PlayerName);
                csv.Append(',');
                csv.AppendCsvField(sale.Tickets.ToString(CultureInfo.InvariantCulture));
                csv.Append(',');
                csv.AppendCsvField(sale.TicketRange);
                csv.Append(',');
                csv.AppendCsvField(sale.Gil.ToString(CultureInfo.InvariantCulture));
                csv.AppendLine();
            }

            csv.AppendCsvField(string.Empty);
            csv.Append(',');
            csv.AppendCsvField(string.Empty);
            csv.Append(',');
            csv.AppendCsvField("TOTAL");
            csv.Append(',');
            csv.AppendCsvField(session.TotalTickets.ToString(CultureInfo.InvariantCulture));
            csv.Append(',');
            csv.AppendCsvField(string.Empty);
            csv.Append(',');
            csv.AppendCsvField(session.GrossTicketSalesTotalGil.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();

            csv.AppendLine();
            csv.AppendLine("Summary,Value");
            csv.AppendCsvField("VenueHostSalesPercent");
            csv.Append(',');
            csv.AppendCsvField(session.VenueHostSalesPercent.ToString("0.#", CultureInfo.InvariantCulture));
            csv.AppendLine();
            csv.AppendCsvField("VenueHostShareGil");
            csv.Append(',');
            csv.AppendCsvField(session.VenueHostShareGil.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();
            csv.AppendCsvField("PrizeTicketSalesGil");
            csv.Append(',');
            csv.AppendCsvField(session.PrizeTicketSalesGil.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();
            csv.AppendCsvField("BasePotGil");
            csv.Append(',');
            csv.AppendCsvField(config.BasePot.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();
            csv.AppendCsvField("TotalPotGil");
            csv.Append(',');
            csv.AppendCsvField(session.TotalPot.ToString(CultureInfo.InvariantCulture));
            csv.AppendLine();

            File.WriteAllText(filePath, csv.ToString(), Encoding.UTF8);

            this.lastExportDirectory = exportDirectory;
            this.lastExportFilePath = filePath;
            this.exportStatus = $"CSV exported to: {filePath}";
        }
        catch (Exception ex)
        {
            this.exportStatus = $"CSV export failed: {ex.Message}";
        }
    }

    private void OpenExportFolder()
    {
        try
        {
            var directory = this.GetExportDirectory();

            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });

            this.exportStatus = $"Opened export folder: {directory}";
        }
        catch (Exception ex)
        {
            this.exportStatus = $"Could not open export folder: {ex.Message}";
        }
    }

    private void CopyExportPath()
    {
        var path = !string.IsNullOrWhiteSpace(this.lastExportFilePath)
            ? this.lastExportFilePath
            : this.GetExportDirectory();

        ImGui.SetClipboardText(path);
        this.exportStatus = $"Copied export path: {path}";
    }

    private string GetExportDirectory()
    {
        if (!string.IsNullOrWhiteSpace(this.lastExportDirectory))
            return this.lastExportDirectory;

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (string.IsNullOrWhiteSpace(documentsPath))
            throw new InvalidOperationException("Documents folder could not be found.");

        return Path.Combine(documentsPath, "VenueRaffle");
    }

}

internal static class CsvExtensions
{
    /// <summary>
    /// Appends one RFC4180-style CSV field, quoting only when required.
    /// </summary>
    public static void AppendCsvField(this StringBuilder builder, string value)
    {
        value ??= string.Empty;

        var mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');

        if (!mustQuote)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        builder.Append(value.Replace("\"", "\"\""));
        builder.Append('"');
    }
}
