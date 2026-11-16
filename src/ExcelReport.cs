using ClosedXML.Excel;

namespace BackupChecker;

/// <summary>Un foglio per server, con le stesse colonne del censimento.</summary>
public static class ExcelReport
{
    private static readonly XLColor Verde = XLColor.FromHtml("#C6EFCE");
    private static readonly XLColor Giallo = XLColor.FromHtml("#FFEB9C");
    private static readonly XLColor Rosso = XLColor.FromHtml("#FFC7CE");
    private static readonly XLColor Intestazione = XLColor.FromHtml("#1F4E78");

    private static readonly string[] Colonne =
        { "Istanza", "Database", "Stato", "Recovery", "Data ultimo backup", "Orario avvio", "Orario fine", "Esito" };

    public static void Write(string path, IReadOnlyList<ServerReport> reports)
    {
        using var wb = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reports)
            WriteServer(wb.Worksheets.Add(SheetName(r, used)), r);
        wb.SaveAs(path);
    }

    private static void WriteServer(IXLWorksheet ws, ServerReport r)
    {
        for (int c = 0; c < Colonne.Length; c++) ws.Cell(1, c + 1).Value = Colonne[c];
        var head = ws.Range(1, 1, 1, Colonne.Length);
        head.Style.Font.Bold = true;
        head.Style.Font.FontColor = XLColor.White;
        head.Style.Fill.BackgroundColor = Intestazione;

        if (!r.Ok)
        {
            ws.Cell(2, 1).Value = r.Istanza ?? r.DisplayName ?? r.Server;
            ws.Cell(2, 8).Value = "KO - SERVER NON RAGGIUNGIBILE: " + (r.Errore ?? "");
            ws.Cell(2, 8).Style.Fill.BackgroundColor = Rosso;
            Finish(ws, 2);
            return;
        }

        string istanza = r.Istanza ?? r.DisplayName ?? r.Server;
        int row = 2;
        foreach (var d in r.Databases)
        {
            ws.Cell(row, 1).Value = istanza;
            ws.Cell(row, 2).Value = d.Database;
            ws.Cell(row, 3).Value = d.Stato;
            ws.Cell(row, 4).Value = d.Recovery;
            if (d.InizioBackup is not null)
            {
                ws.Cell(row, 5).Value = d.InizioBackup.Value.Date;
                ws.Cell(row, 5).Style.DateFormat.Format = "dd/MM/yyyy";
            }
            ws.Cell(row, 6).Value = d.OrarioJob ?? "N/D";
            if (d.FineBackup is not null) ws.Cell(row, 7).Value = d.FineBackup.Value.ToString("HH:mm");
            ws.Cell(row, 8).Value = d.Esito;
            ws.Cell(row, 8).Style.Fill.BackgroundColor =
                !d.IsOk ? Rosso : d.Esito.Contains("NESSUN JOB") ? Giallo : Verde;
            row++;
        }

        if (r.Databases.Count == 0)
        {
            ws.Cell(row, 1).Value = istanza;
            ws.Cell(row, 2).Value = "Nessun database utente";
            row++;
        }

        Finish(ws, row - 1);
    }

    private static void Finish(IXLWorksheet ws, int lastRow)
    {
        var table = ws.Range(1, 1, Math.Max(1, lastRow), Colonne.Length);
        table.SetAutoFilter();
        table.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        table.Style.Border.InsideBorderColor = XLColor.FromHtml("#BFBFBF");
        table.Style.Border.OutsideBorderColor = XLColor.FromHtml("#BFBFBF");
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, Colonne.Length).AdjustToContents(1, Math.Max(1, lastRow));
        for (int c = 1; c <= Colonne.Length; c++)
            if (ws.Column(c).Width > 80) ws.Column(c).Width = 80;
    }

    /// <summary>Nome foglio valido per Excel: max 31 caratteri, senza []:*?/\ e univoco.</summary>
    public static string SheetName(ServerReport r, HashSet<string> used)
    {
        string baseName = r.DisplayName ?? r.Istanza ?? r.Server;
        var clean = new string(baseName.Select(ch => "[]:*?/\\".Contains(ch) ? '_' : ch).ToArray()).Trim().Trim('\'');
        if (clean.Length == 0) clean = "Server";
        if (clean.Length > 31) clean = clean[..31];

        string name = clean;
        int n = 2;
        while (!used.Add(name))
        {
            string suffix = $" ({n++})";
            name = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        return name;
    }
}
