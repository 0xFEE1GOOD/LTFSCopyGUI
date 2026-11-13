Imports System.Globalization
Imports System.IO
Imports System.Text

''' <summary>Formatting, normalisation and logging shared by the catalog.</summary>
Friend Module Util

    Public ReadOnly AppDir As String = AppDomain.CurrentDomain.BaseDirectory

    ''' <summary>Same output as LTFSCopyGUI's IOManager.FormatSize (binary units).</summary>
    Public Function FormatSize(byteCount As Long) As String
        Dim sym As String = If(byteCount >= 0, "", "-")
        Dim b As Double = Math.Abs(CDbl(byteCount))
        If b < 1024 Then Return sym & CLng(b).ToString(CultureInfo.InvariantCulture) & " Bytes"
        If b < 1024.0 ^ 2 Then Return sym & (b / 1024).ToString("F2", CultureInfo.InvariantCulture) & " KiB"
        If b < 1024.0 ^ 3 Then Return sym & (b / 1024.0 ^ 2).ToString("F2", CultureInfo.InvariantCulture) & " MiB"
        If b < 1024.0 ^ 4 Then Return sym & (b / 1024.0 ^ 3).ToString("F2", CultureInfo.InvariantCulture) & " GiB"
        Return sym & (b / 1024.0 ^ 4).ToString("F2", CultureInfo.InvariantCulture) & " TiB"
    End Function

    Public Function FormatCount(n As Long) As String
        Return n.ToString("N0", CultureInfo.InvariantCulture)
    End Function

    Private ReadOnly Epoch As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    ''' <summary>LTFS time ("2024-07-15T10:20:30.123456789Z", UTC) to Unix seconds; Nothing when absent or malformed.</summary>
    Public Function ParseLtfsTime(s As String) As Long?
        If String.IsNullOrEmpty(s) OrElse s.Length < 19 Then Return Nothing
        Dim dt As DateTime
        If DateTime.TryParseExact(s.Substring(0, 19), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, dt) Then
            Return CLng(Math.Floor((dt - Epoch).TotalSeconds))
        End If
        Return Nothing
    End Function

    Public Function ToUnix(utc As DateTime) As Long
        Return CLng(Math.Floor((utc.ToUniversalTime() - Epoch).TotalSeconds))
    End Function

    Public Function FromUnix(sec As Long) As DateTime
        Return Epoch.AddSeconds(sec)
    End Function

    ''' <summary>Local-time display used in every list and panel.</summary>
    Public Function FormatTime(sec As Object) As String
        If sec Is Nothing OrElse TypeOf sec Is DBNull Then Return ""
        Return FromUnix(Convert.ToInt64(sec, CultureInfo.InvariantCulture)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
    End Function

    ''' <summary>
    ''' The text the search index holds for a name, and the form a query term is compared in:
    ''' compatibility-decomposed, accents removed, lower case by invariant (Unicode) rules, and the
    ''' Russian ё folded to е, so "Ёлка", "ёлка" and "елка" all match one another.
    ''' </summary>
    Public Function SearchKey(s As String) As String
        If String.IsNullOrEmpty(s) Then Return ""
        Dim d As String = s.Normalize(NormalizationForm.FormKD)
        Dim sb As New StringBuilder(d.Length)
        For Each ch As Char In d
            Dim cat As UnicodeCategory = CharUnicodeInfo.GetUnicodeCategory(ch)
            If cat = UnicodeCategory.NonSpacingMark OrElse cat = UnicodeCategory.SpacingCombiningMark OrElse cat = UnicodeCategory.EnclosingMark Then Continue For
            sb.Append(ch)
        Next
        Dim k As String = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant()
        Return k.Replace("ё"c, "е"c)
    End Function

    ''' <summary>Lower-case extension without the dot, or Nothing.</summary>
    Public Function ExtensionOf(name As String) As String
        Dim i As Integer = name.LastIndexOf("."c)
        If i <= 0 OrElse i = name.Length - 1 Then Return Nothing
        Dim e As String = name.Substring(i + 1)
        If e.Length > 16 OrElse e.IndexOf(" "c) >= 0 Then Return Nothing
        Return e.ToLowerInvariant()
    End Function

    Private ReadOnly ArchiveExts As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "tar", "zip", "7z", "rar", "gz", "tgz", "bz2", "xz", "zst", "iso", "cab"}

    Public Function IsArchiveName(name As String) As Boolean
        Dim e As String = ExtensionOf(name)
        Return e IsNot Nothing AndAlso ArchiveExts.Contains(e)
    End Function

    ' ---- log -----------------------------------------------------------------------------------
    Private ReadOnly LogLock As New Object()
    Public Property LogPath As String = Path.Combine(AppDir, "catalog", "LTFSCatalog.log")

    Public Sub Log(message As String)
        Try
            SyncLock LogLock
                Dim dir As String = Path.GetDirectoryName(LogPath)
                If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                Dim fi As New FileInfo(LogPath)
                If fi.Exists AndAlso fi.Length > 4 * 1024 * 1024 Then
                    File.Copy(LogPath, LogPath & ".old", True)
                    File.Delete(LogPath)
                End If
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & "  " & message & Environment.NewLine, Encoding.UTF8)
            End SyncLock
        Catch
            ' logging must never take the catalog down
        End Try
    End Sub

    Public Sub LogError(context As String, ex As Exception)
        Log(context & ": " & ex.ToString())
    End Sub

End Module
