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
        { "Istanza", "Database", "Stato", "Recovery", "Data ultimo backup", "Orario avvio", "Orario fine",
          "File backup", "Dimensione file (MB)", "Verifica file", "Copia Wasabi", "Esito" };
    private const int ColEsito = 12;

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
            ws.Cell(2, ColEsito).Value = "KO - SERVER NON RAGGIUNGIBILE: " + (r.Errore ?? "");
            ws.Cell(2, ColEsito).Style.Fill.BackgroundColor = Rosso;
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
            if (d.File.Count > 0)
            {
                ws.Cell(row, 8).Value = string.Join("\n", d.File.Select(f => f.Path));
                ws.Cell(row, 8).Style.Alignment.WrapText = d.File.Count > 1;
            }
            if (d.VerificaFile?.Byte is long b)
            {
                ws.Cell(row, 9).Value = Math.Round(b / 1048576d, 1);
                ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.0";
            }
            if (d.VerificaFile is not null)
            {
                ws.Cell(row, 10).Value = d.VerificaFile.Stato switch
                {
                    StatoFile.Ok => "OK - presente",
                    StatoFile.NonTrovato => "NON TROVATO - " + d.VerificaFile.Dettaglio,
                    StatoFile.Incompleto => "INCOMPLETO - " + d.VerificaFile.Dettaglio,
                    _ => "NON VERIFICABILE - " + d.VerificaFile.Dettaglio,
                };
                ws.Cell(row, 10).Style.Fill.BackgroundColor = d.VerificaFile.Stato == StatoFile.Ok ? Verde : Rosso;
            }
            else if (d.FineBackup is not null) ws.Cell(row, 10).Value = "non controllato";

            if (d.VerificaCloud is not null)
            {
                ws.Cell(row, 11).Value = d.VerificaCloud.Stato switch
                {
                    StatoFile.Ok => "OK - " + d.VerificaCloud.Dettaglio,
                    StatoFile.NonTrovato => "NON TROVATA - " + d.VerificaCloud.Dettaglio,
                    StatoFile.Incompleto => "INCOMPLETA - " + d.VerificaCloud.Dettaglio,
                    _ => "NON VERIFICABILE - " + d.VerificaCloud.Dettaglio,
                };
                ws.Cell(row, 11).Style.Fill.BackgroundColor = d.VerificaCloud.Stato == StatoFile.Ok ? Verde : Rosso;
            }
            else if (d.FineBackup is not null) ws.Cell(row, 11).Value = r.NotaCloud ?? "-";

            ws.Cell(row, ColEsito).Value = d.Esito;
            ws.Cell(row, ColEsito).Style.Fill.BackgroundColor =
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
