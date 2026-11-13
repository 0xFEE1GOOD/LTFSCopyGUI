Imports System.Globalization
Imports System.IO
Imports System.Text

''' <summary>Hands a restore to LTFSCopyGUI, which owns the drive: one request file per tape in
''' %ProgramData%\LTFSCopyGUI\CatalogRestore. LTFSCopyGUI asks to run it once that tape's index is loaded
''' (LTFSCopyGUI\Forms\LTFSWriter.CatalogRestore.vb reads it). The catalog itself never touches the drive.
''' Format: UTF-8 lines, "LTFSCatalogRestore 1" then key=value; d=/f= are folder/file tape paths joined with "/"
''' (LTFS names cannot contain "/"); values escape "%", CR and LF as %25, %0D, %0A.</summary>
Friend Module RestoreRequest

    Public ReadOnly Folder As String =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LTFSCopyGUI", "CatalogRestore")

    ''' <summary>Writes one request atomically (temp file, then rename) and returns its path.</summary>
    Public Function Write(tape As TapeInfo, items As List(Of KeyValuePair(Of Boolean, String())), destination As String, keepPaths As Boolean,
                          Optional toFolder As String = Nothing) As String
        Dim dir As String = If(toFolder, Folder)
        Directory.CreateDirectory(dir)
        Dim sb As New StringBuilder()
        sb.Append("LTFSCatalogRestore 1").Append(ControlChars.Lf)
        Add(sb, "created", DateTime.Now.ToString("s", CultureInfo.InvariantCulture))
        Add(sb, "barcode", tape.Barcode)
        Add(sb, "serial", tape.Serial)
        Add(sb, "volume", tape.VolumeUuid)
        Add(sb, "generation", tape.Generation.ToString(CultureInfo.InvariantCulture))
        Add(sb, "dest", destination)
        Add(sb, "keeppaths", If(keepPaths, "1", "0"))
        For Each it As KeyValuePair(Of Boolean, String()) In items
            Add(sb, If(it.Key, "d", "f"), "/" & String.Join("/", it.Value))
        Next
        Dim stem As String = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) & "_" & SafeName(tape.Barcode)
        Dim final As String = Path.Combine(dir, stem & ".req")
        Dim n As Integer = 1
        While File.Exists(final)
            final = Path.Combine(dir, stem & "_" & n.ToString(CultureInfo.InvariantCulture) & ".req")
            n += 1
        End While
        Dim temp As String = Path.ChangeExtension(final, ".tmp")
        File.WriteAllText(temp, sb.ToString(), New UTF8Encoding(False))
        File.Move(temp, final)
        Log("restore request " & final & ": " & items.Count.ToString(CultureInfo.InvariantCulture) & " item(s) from " & tape.Barcode & " to " & destination)
        Return final
    End Function

    ''' <summary>Requests LTFSCopyGUI has not taken yet.</summary>
    Public Function PendingCount() As Integer
        Try
            Return If(Directory.Exists(Folder), Directory.GetFiles(Folder, "*.req").Length, 0)
        Catch ex As IOException
            Return 0
        Catch ex As UnauthorizedAccessException
            Return 0
        End Try
    End Function

    Private Sub Add(sb As StringBuilder, key As String, value As String)
        sb.Append(key).Append("="c).Append(If(value, "").Replace("%", "%25").Replace(ControlChars.Cr, "%0D").Replace(ControlChars.Lf, "%0A")).Append(ControlChars.Lf)
    End Sub

    Private Function SafeName(s As String) As String
        Dim sb As New StringBuilder()
        For Each ch As Char In If(s, "")
            sb.Append(If(Char.IsLetterOrDigit(ch) OrElse ch = "-"c, ch, "_"c))
        Next
        Return If(sb.Length = 0, "tape", sb.ToString())
    End Function

End Module
