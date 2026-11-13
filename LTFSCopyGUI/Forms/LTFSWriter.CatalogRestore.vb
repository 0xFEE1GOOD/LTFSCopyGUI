Imports System.Text

' Restore requests from LTFSCatalog.exe. The catalog never touches the drive: it drops a request file into
' %ProgramData%\LTFSCopyGUI\CatalogRestore, and this window offers to run it once it is idle with the matching
' tape's index loaded. The restore itself is the same tape-order extraction as the Extract menu items.
' Kept in its own file so upstream changes to LTFSWriter.vb merge cleanly.
Partial Class LTFSWriter
    Public Shared ReadOnly CatalogRequestFolder As String =
        IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LTFSCopyGUI", "CatalogRestore")

    Private WithEvents CatalogRequestTimer As Global.System.Windows.Forms.Timer
    Private _catalogBusy As Boolean
    Private _catalogSeenIndex As ltfsindex
    Private ReadOnly _catalogSeen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

    Private NotInheritable Class CatalogRequest
        Public Barcode As String = ""
        Public Serial As String = ""
        Public Volume As Guid
        Public Generation As Long = -1
        Public Destination As String = ""
        Public KeepPaths As Boolean = True
        Public ReadOnly Items As New List(Of KeyValuePair(Of Boolean, String)) ' (is folder, tape path joined with "/")

        Public ReadOnly Property Label As String
            Get
                Return If(Serial = "", Barcode, $"{Barcode} (serial {Serial})")
            End Get
        End Property

        Public Shared Function Load(path As String) As CatalogRequest
            Dim lines As String() = IO.File.ReadAllLines(path, New UTF8Encoding(False))
            If lines.Length = 0 OrElse lines(0).Trim() <> "LTFSCatalogRestore 1" Then Return Nothing
            Dim r As New CatalogRequest()
            For i As Integer = 1 To lines.Length - 1
                Dim eq As Integer = lines(i).IndexOf("="c)
                If eq <= 0 Then Continue For
                Dim value As String = Unescape(lines(i).Substring(eq + 1))
                Select Case lines(i).Substring(0, eq)
                    Case "barcode" : r.Barcode = value
                    Case "serial" : r.Serial = value
                    Case "volume" : Guid.TryParse(value, r.Volume)
                    Case "generation" : Long.TryParse(value, r.Generation)
                    Case "dest" : r.Destination = value
                    Case "keeppaths" : r.KeepPaths = value <> "0"
                    Case "d" : If value.Trim("/"c) <> "" Then r.Items.Add(New KeyValuePair(Of Boolean, String)(True, value.Trim("/"c)))
                    Case "f" : If value.Trim("/"c) <> "" Then r.Items.Add(New KeyValuePair(Of Boolean, String)(False, value.Trim("/"c)))
                End Select
            Next
            If r.Volume = Guid.Empty OrElse Not IO.Path.IsPathRooted(r.Destination) OrElse r.Items.Count = 0 Then Return Nothing
            Return r
        End Function

        ' The catalog escapes only "%", CR and LF; every "%" starts exactly one escape, so plain replaces are exact.
        Private Shared Function Unescape(s As String) As String
            If s.IndexOf("%"c) < 0 Then Return s
            Return s.Replace("%0A", vbLf).Replace("%0D", vbCr).Replace("%25", "%")
        End Function
    End Class

    Private NotInheritable Class CatalogItem
        Public Parts As String()
        Public Ancestors As ltfsindex.directory() ' tape folders above the item (root excluded), for "keep paths"
        Public File As ltfsindex.file
        Public Directory As ltfsindex.directory
    End Class

    Private Sub CatalogRestore_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        If OfflineMode Then Return
        CatalogRequestTimer = New Global.System.Windows.Forms.Timer With {.Interval = 3000}
        CatalogRequestTimer.Start()
    End Sub

    Private Sub CatalogRestore_Closed(sender As Object, e As FormClosedEventArgs) Handles MyBase.FormClosed
        If CatalogRequestTimer Is Nothing Then Return
        CatalogRequestTimer.Stop()
        CatalogRequestTimer.Dispose()
        CatalogRequestTimer = Nothing
    End Sub

    <Runtime.InteropServices.DllImport("user32.dll", EntryPoint:="IsWindowEnabled")>
    Private Shared Function CatalogIsWindowEnabled(hWnd As IntPtr) As Boolean
    End Function

    Private Sub CatalogRequestTimer_Tick(sender As Object, e As EventArgs) Handles CatalogRequestTimer.Tick
        If _catalogBusy OrElse IsDisposed OrElse Not CanOperateTape() Then Return
        If Not IsHandleCreated OrElse Not CatalogIsWindowEnabled(Handle) Then Return ' a modal dialog of this window is open
        If driveHandle = IntPtr.Zero OrElse driveHandle = New IntPtr(-1) Then Return
        Dim index As ltfsindex = schema
        If index Is Nothing OrElse index._directory Is Nothing OrElse index._directory.Count = 0 OrElse index._directory(0) Is Nothing Then Return
        Dim files As String()
        Try
            If Not IO.Directory.Exists(CatalogRequestFolder) Then Return
            files = IO.Directory.GetFiles(CatalogRequestFolder, "*.req")
        Catch
            Return
        End Try
        If files.Length = 0 Then Return
        If _catalogSeenIndex IsNot index Then
            _catalogSeen.Clear() ' another index was loaded: look at every request again
            _catalogSeenIndex = index
        End If
        Array.Sort(files, StringComparer.OrdinalIgnoreCase) ' names start with a timestamp: oldest first
        _catalogBusy = True
        Try
            For Each path As String In files
                If _catalogSeen.Contains(path) Then Continue For
                Dim req As CatalogRequest
                Try
                    req = CatalogRequest.Load(path)
                Catch
                    Continue For ' locked or vanished: try again next tick
                End Try
                _catalogSeen.Add(path)
                If req Is Nothing Then
                    PrintMsg($"LTFSCatalog: ignored unreadable restore request {IO.Path.GetFileName(path)}", IsWarn:=True)
                ElseIf req.Volume <> index.volumeuuid Then
                    PrintMsg($"LTFSCatalog restore request waiting for tape {req.Label}: {req.Items.Count} item(s) to {req.Destination}")
                ElseIf OfferCatalogRequest(path, req, index) Then
                    Exit For ' a restore is running; the rest wait
                End If
            Next
        Finally
            _catalogBusy = False
        End Try
    End Sub

    ' Returns True when a restore was started.
    Private Function OfferCatalogRequest(path As String, req As CatalogRequest, index As ltfsindex) As Boolean
        Dim items As New List(Of CatalogItem)
        Dim missing As New List(Of String)
        ResolveCatalogItems(req, index._directory(0), items, missing)
        Dim files As Long = 0, bytes As Long = 0, folders As Long = 0, folderFiles As Long = 0
        For Each it As CatalogItem In items
            If it.Directory IsNot Nothing Then
                folders += 1
                folderFiles += it.Directory.TotalFiles
            Else
                files += 1
                bytes += Math.Max(0L, it.File.length)
            End If
        Next
        Dim sb As New StringBuilder()
        sb.AppendLine($"LTFSCatalog asks to restore from this tape ({req.Label}):")
        sb.AppendLine()
        If files > 0 Then sb.AppendLine($"  {files:N0} file(s), {IOManager.FormatSize(bytes)}")
        If folders > 0 Then sb.AppendLine($"  {folders:N0} folder(s) with {folderFiles:N0} file(s)")
        sb.AppendLine($"  to {req.Destination}" & If(req.KeepPaths, " (tape folder paths kept)", ""))
        If missing.Count > 0 Then
            sb.AppendLine()
            sb.AppendLine($"{missing.Count:N0} item(s) are not in the loaded index and will be skipped, e.g. {missing(0)}")
        End If
        If req.Generation >= 0 AndAlso CULng(req.Generation) <> index.generationnumber Then
            sb.AppendLine()
            sb.AppendLine($"The catalog was built from index generation {req.Generation}; the tape is at {index.generationnumber}.")
        End If
        Dim answer As DialogResult
        Using owner As New Form With {.TopMost = True}
            If items.Count = 0 Then
                MessageBox.Show(owner, $"LTFSCatalog asked to restore {req.Items.Count:N0} item(s) from {req.Label}, but none of them is in the loaded index. The request is removed.",
                                "LTFSCatalog restore", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                TryDeleteCatalogRequest(path)
                Return False
            End If
            sb.AppendLine()
            sb.Append("Yes = restore now, No = discard the request, Cancel = ask again after the next index load.")
            answer = MessageBox.Show(owner, sb.ToString(), "LTFSCatalog restore", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question)
        End Using
        Select Case answer
            Case DialogResult.Yes
                If Not CanOperateTape() Then Return False ' something else started while the question was open
                TryDeleteCatalogRequest(path)
                For Each m As String In missing
                    PrintMsg($"LTFSCatalog restore: not in the loaded index, skipped: {m}", LogOnly:=True, IsWarn:=True)
                Next
                StartCatalogRestore(req, items, missing.Count)
                Return True
            Case DialogResult.No
                TryDeleteCatalogRequest(path)
                PrintMsg($"LTFSCatalog restore request discarded ({req.Items.Count} item(s) to {req.Destination})")
        End Select
        Return False
    End Function

    Private Sub TryDeleteCatalogRequest(path As String)
        Try
            IO.File.Delete(path)
        Catch ex As Exception
            PrintMsg($"LTFSCatalog: could not remove request {path}: {ex.Message}", LogOnly:=True, IsWarn:=True)
        End Try
    End Sub

    ' Name -> child map of one folder. LTFS names are case-sensitive. LTFSCopyGUI keeps percent-encoded names as
    ' written while the catalog stores them decoded, so a name containing "%" is also filed under its decoded form.
    Private Shared Function CatalogNameMap(Of T As Class)(children As IEnumerable(Of T), getName As Func(Of T, String)) As Dictionary(Of String, T)
        Dim map As New Dictionary(Of String, T)(StringComparer.Ordinal)
        For Each child As T In children
            If child Is Nothing Then Continue For
            Dim name As String = getName(child)
            If name Is Nothing Then Continue For
            If Not map.ContainsKey(name) Then map(name) = child
            If name.IndexOf("%"c) >= 0 Then
                Try
                    Dim decoded As String = Uri.UnescapeDataString(name)
                    If Not map.ContainsKey(decoded) Then map(decoded) = child
                Catch
                End Try
            End If
        Next
        Return map
    End Function

    Private Shared Sub ResolveCatalogItems(req As CatalogRequest, root As ltfsindex.directory, items As List(Of CatalogItem), missing As List(Of String))
        Dim dirs As New Dictionary(Of String, ltfsindex.directory)(StringComparer.Ordinal) From {{"", root}}
        Dim dirMaps As New Dictionary(Of String, Dictionary(Of String, ltfsindex.directory))(StringComparer.Ordinal)
        Dim fileMaps As New Dictionary(Of String, Dictionary(Of String, ltfsindex.file))(StringComparer.Ordinal)
        Dim childDir As Func(Of String, ltfsindex.directory, String, ltfsindex.directory) =
            Function(parentKey As String, parentDir As ltfsindex.directory, name As String) As ltfsindex.directory
                Dim map As Dictionary(Of String, ltfsindex.directory) = Nothing
                If Not dirMaps.TryGetValue(parentKey, map) Then
                    map = CatalogNameMap(parentDir.EnumerateLazyDirectories(), Function(x) x.name)
                    dirMaps(parentKey) = map
                End If
                Dim found As ltfsindex.directory = Nothing
                map.TryGetValue(name, found)
                Return found
            End Function
        Dim wantedFolders As New HashSet(Of String)(StringComparer.Ordinal)
        For Each kv As KeyValuePair(Of Boolean, String) In req.Items
            If kv.Key Then wantedFolders.Add(kv.Value)
        Next
        Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
        For Each kv As KeyValuePair(Of Boolean, String) In req.Items
            If Not seen.Add(If(kv.Key, "d|", "f|") & kv.Value) Then Continue For
            Dim parts As String() = kv.Value.Split("/"c)
            Dim parentPath As String = ""
            Dim insideWanted As Boolean = False
            For i As Integer = 0 To parts.Length - 2
                parentPath = If(i = 0, parts(0), parentPath & "/" & parts(i))
                If wantedFolders.Contains(parentPath) Then insideWanted = True : Exit For
            Next
            If insideWanted Then Continue For ' a requested folder above it brings it along

            Dim ancestors(parts.Length - 2) As ltfsindex.directory
            Dim parent As ltfsindex.directory = root
            parentPath = ""
            For i As Integer = 0 To parts.Length - 2
                Dim upKey As String = parentPath
                parentPath = If(i = 0, parts(0), parentPath & "/" & parts(i))
                Dim d As ltfsindex.directory = Nothing
                If Not dirs.TryGetValue(parentPath, d) Then
                    d = If(parent Is Nothing, Nothing, childDir(upKey, parent, parts(i)))
                    dirs(parentPath) = d
                End If
                ancestors(i) = d
                parent = d
            Next
            Dim it As New CatalogItem With {.Parts = parts, .Ancestors = ancestors}
            Dim last As String = parts(parts.Length - 1)
            If parent IsNot Nothing Then
                If kv.Key Then
                    Dim d As ltfsindex.directory = Nothing
                    If Not dirs.TryGetValue(kv.Value, d) Then
                        d = childDir(parentPath, parent, last)
                        dirs(kv.Value) = d
                    End If
                    it.Directory = d
                Else
                    Dim map As Dictionary(Of String, ltfsindex.file) = Nothing
                    If Not fileMaps.TryGetValue(parentPath, map) Then
                        map = CatalogNameMap(parent.EnumerateLazyFiles(), Function(x) x.name)
                        fileMaps(parentPath) = map
                    End If
                    map.TryGetValue(last, it.File)
                End If
            End If
            If it.File Is Nothing AndAlso it.Directory Is Nothing Then
                missing.Add("/" & kv.Value)
            Else
                items.Add(it)
            End If
        Next
    End Sub

    Private Sub StartCatalogRestore(req As CatalogRequest, items As List(Of CatalogItem), skipped As Integer)
        Dim outputRoot As String = req.Destination
        Dim th As New Threading.Thread(
            Sub()
                Dim succeeded As Boolean = False
                Dim reserved As Boolean = False
                Try
                    StopFlag = False
                    CurrentFilesProcessed = 0
                    CurrentBytesProcessed = 0
                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    StartTime = Now
                    SetStatusLight(LWStatus.Busy)
                    PrintMsg($"LTFSCatalog restore: {items.Count} item(s) from {req.Label} to {outputRoot}")
                    PrintMsg(My.Resources.ResText_PrepFile)
                    IO.Directory.CreateDirectory(outputRoot)
                    Dim baseDir As String = If(outputRoot.StartsWith("\\"), outputRoot, $"\\?\{outputRoot}")
                    Dim requests As New List(Of ExtractionFileRequest)
                    Dim directoryRequests As New List(Of ExtractionDirectoryRequest)
                    Dim knownDirs As New HashSet(Of String)(StringComparer.Ordinal)
                    Dim addDir As Action(Of String, ltfsindex.directory) =
                        Sub(dirPath As String, dir As ltfsindex.directory)
                            If knownDirs.Add(dirPath) Then directoryRequests.Add(New ExtractionDirectoryRequest With {.DirectoryPath = dirPath, .Directory = dir})
                        End Sub
                    For Each it As CatalogItem In items
                        If StopFlag Then Exit For
                        Dim parentDir As String = baseDir
                        If req.KeepPaths Then
                            For i As Integer = 0 To it.Parts.Length - 2
                                parentDir = IO.Path.Combine(parentDir, it.Parts(i))
                                addDir(parentDir, it.Ancestors(i))
                            Next
                        End If
                        Dim target As String = IO.Path.Combine(parentDir, it.Parts(it.Parts.Length - 1))
                        If it.Directory IsNot Nothing Then
                            addDir(target, it.Directory)
                            TraverseRestoreFiles(it.Directory, New IO.DirectoryInfo(target),
                                Sub(fr As FileRecord)
                                    If StopFlag Then Return
                                    requests.Add(New ExtractionFileRequest With {.FileName = fr.SourcePath, .FileIndex = fr.File})
                                End Sub,
                                addDir, False)
                        Else
                            it.File.TempObj = Nothing
                            requests.Add(New ExtractionFileRequest With {.FileName = target, .FileIndex = it.File})
                        End If
                    Next
                    If Not StopFlag Then
                        Dim total As ULong = 0
                        For Each r As ExtractionFileRequest In requests
                            total += CULng(Math.Max(0L, r.FileIndex.length))
                        Next
                        UnwrittenSizeOverrideValue = total
                        UnwrittenCountOverrideValue = CULng(requests.Count)
                        PrintMsg(My.Resources.ResText_RestFile)
                        TapeUtils.ReserveUnit(driveHandle)
                        reserved = True
                        TapeUtils.PreventMediaRemoval(driveHandle)
                        RestorePosition = New TapeUtils.PositionData(driveHandle)
                        RestoreFilesInTapeOrder(requests, outputRoot, directoryRequests)
                    End If
                    If StopFlag Then
                        PrintMsg(My.Resources.ResText_OpCancelled)
                        SetStatusLight(LWStatus.Idle)
                    Else
                        succeeded = True
                        PrintMsg(My.Resources.ResText_RestFin)
                        SetStatusLight(LWStatus.Succ)
                    End If
                Catch ex As Exception
                    PrintMsg($"{My.Resources.ResText_RestoreErr}{ex}", IsWarn:=True)
                    SetStatusLight(LWStatus.Err)
                Finally
                    If reserved Then
                        SyncLock OperationLock
                            SyncLock TapeUtils.GetSCSIOperationLock(driveHandle)
                                Try
                                    TapeUtils.AllowMediumRemoval(driveHandle)
                                Catch
                                End Try
                                Try
                                    TapeUtils.ReleaseUnit(driveHandle)
                                Catch
                                End Try
                            End SyncLock
                        End SyncLock
                    End If
                    Dim cancelled As Boolean = StopFlag
                    StopFlag = False
                    UnwrittenSizeOverrideValue = 0
                    UnwrittenCountOverrideValue = 0
                    LockGUI(False)
                    Try
                        Dim note As String = If(skipped > 0, $"{vbCrLf}{skipped:N0} requested item(s) were not on this tape and were skipped.", "")
                        If succeeded AndAlso Not cancelled Then
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RestFin}{vbCrLf}{outputRoot}{note}", "LTFSCatalog restore"))
                        ElseIf Not cancelled Then
                            Invoke(Sub() MessageBox.Show(New Form With {.TopMost = True}, $"{My.Resources.ResText_RestoreErr}{vbCrLf}See the log for details.", "LTFSCatalog restore", MessageBoxButtons.OK, MessageBoxIcon.Error))
                        End If
                    Catch
                    End Try
                End Try
            End Sub)
        LockGUI()
        th.Start()
    End Sub
End Class
