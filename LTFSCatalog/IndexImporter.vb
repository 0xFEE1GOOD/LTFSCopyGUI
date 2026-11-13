Imports System.Data.Common
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Xml

''' <summary>One saved index file (LTFSIndex_Autosave_*.schema[.zst]) and what its cartridge report says.</summary>
Friend NotInheritable Class IndexFileInfo
    Public Path As String = ""
    Public Barcode As String = ""
    Public Serial As String = ""
    Public Generation As Long
    Public Stamp As DateTime
    Public StampKey As String = ""
    Public IsZst As Boolean
    Public FileSize As Long
    Public MTime As Long
    Public Format As String = ""
    Public Vendor As String = ""
    Public Capacity As Long
    ' from the index header (PeekHeader)
    Public VolumeUuid As String = ""
    Public HeaderGeneration As Long
    Public UpdateTime As Long?
    Public IsBlank As Boolean

    Public ReadOnly Property Label As String
        Get
            Return If(String.IsNullOrEmpty(Serial), Barcode, Barcode & " [" & Serial & "]")
        End Get
    End Property
End Class

''' <summary>What a refresh found: tapes to ask about, tapes to re-import, reformatted blank tapes to ask about.</summary>
Friend NotInheritable Class ScanPlan
    Public NewTapes As New List(Of IndexFileInfo)
    Public Updates As New List(Of Tuple(Of TapeInfo, IndexFileInfo))
    Public ReformattedBlank As New List(Of Tuple(Of TapeInfo, IndexFileInfo))
    Public Errors As New List(Of String)
End Class

Friend Module IndexScanner

    Private ReadOnly NameRx As New Regex("^LTFSIndex_Autosave_(?<id>.+)_GEN(?<gen>\d+)_P(?<p>[A-Za-z0-9]+)_B(?<blk>\d+)_(?<ts>\d{8}_\d{6})(?:\.(?<frac>\d+))?\.schema(?<zst>\.zst)?$",
                                         RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)

    ''' <summary>Newest saved index for each tape (barcode + cartridge serial) under the folder, recursively.</summary>
    Public Function Scan(folder As String, errors As List(Of String)) As List(Of IndexFileInfo)
        Dim result As New List(Of IndexFileInfo)
        If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return result
        Dim all As New List(Of IndexFileInfo)
        For Each f As String In SafeEnumerate(folder, "LTFSIndex_Autosave_*.schema*", errors)
            Dim m As Match = NameRx.Match(Path.GetFileName(f))
            If Not m.Success Then Continue For
            Dim info As New IndexFileInfo With {.Path = f, .Barcode = m.Groups("id").Value, .IsZst = m.Groups("zst").Success,
                .Generation = Long.Parse(m.Groups("gen").Value, CultureInfo.InvariantCulture),
                .StampKey = m.Groups("ts").Value & "." & m.Groups("frac").Value.PadRight(7, "0"c)}
            DateTime.TryParseExact(m.Groups("ts").Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, info.Stamp)
            Try
                Dim fi As New FileInfo(f)
                info.FileSize = fi.Length
                info.MTime = ToUnix(fi.LastWriteTimeUtc)
            Catch ex As IOException
                Continue For
            End Try
            ApplyCartridgeReport(info)
            all.Add(info)
        Next
        For Each g In all.GroupBy(Function(i) i.Barcode.ToUpperInvariant() & "|" & i.Serial)
            result.Add(g.OrderByDescending(Function(i) i.StampKey, StringComparer.Ordinal).First())
        Next
        Return result
    End Function

    Private Function SafeEnumerate(root As String, pattern As String, errors As List(Of String)) As List(Of String)
        Dim found As New List(Of String)
        Dim pending As New Stack(Of String)
        pending.Push(root)
        While pending.Count > 0
            Dim dir As String = pending.Pop()
            Try
                found.AddRange(Directory.GetFiles(dir, pattern))
                For Each d As String In Directory.GetDirectories(dir)
                    pending.Push(d)
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                errors.Add("Cannot read folder " & dir & ": " & ex.Message)
            End Try
        End While
        Return found
    End Function

    ' ---- cartridge report (*.cm, written by LTFSCopyGUI next to each index) ---------------------------
    Private ReadOnly CmCache As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase)

    ''' <summary>A single index file chosen by hand (Catalog > Import index file).</summary>
    Public Function Describe(fileName As String) As IndexFileInfo
        Dim info As New IndexFileInfo With {.Path = fileName, .IsZst = fileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)}
        Dim m As Match = NameRx.Match(Path.GetFileName(fileName))
        If m.Success Then
            info.Barcode = m.Groups("id").Value
            info.Generation = Long.Parse(m.Groups("gen").Value, CultureInfo.InvariantCulture)
        Else
            info.Barcode = Regex.Replace(Path.GetFileName(fileName), "(\.schema)?(\.zst|\.xml)?$", "", RegexOptions.IgnoreCase)
        End If
        Dim fi As New FileInfo(fileName)
        info.FileSize = fi.Length
        info.MTime = ToUnix(fi.LastWriteTimeUtc)
        ApplyCartridgeReport(info, folderFallback:=False)
        Return info
    End Function

    Private Sub ApplyCartridgeReport(info As IndexFileInfo, Optional folderFallback As Boolean = True)
        Dim cm As String = Regex.Replace(info.Path, "\.schema(\.zst)?$", ".cm", RegexOptions.IgnoreCase)
        If Not File.Exists(cm) Then
            If Not folderFallback Then Return
            ' an index without its own report: take the newest report in the same folder
            Dim dir As String = Path.GetDirectoryName(info.Path)
            cm = Nothing
            Try
                Dim cands = Directory.GetFiles(dir, "LTFSIndex_Autosave_*.cm")
                If cands.Length > 0 Then cm = cands.OrderByDescending(Function(x) x, StringComparer.Ordinal).First()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            If cm Is Nothing Then Return
        End If
        Dim f As String() = Nothing
        SyncLock CmCache
            CmCache.TryGetValue(cm, f)
        End SyncLock
        If f Is Nothing Then
            f = ParseCartridgeReport(cm)
            SyncLock CmCache
                CmCache(cm) = f
            End SyncLock
        End If
        If Not String.IsNullOrEmpty(f(0)) Then info.Barcode = f(0)
        info.Serial = f(1)
        info.Format = f(2)
        info.Vendor = f(3)
        info.Capacity = If(String.IsNullOrEmpty(f(4)), 0L, Long.Parse(f(4), CultureInfo.InvariantCulture))
    End Sub

    ''' <summary>barcode, serial, format, vendor, capacity bytes (sum of partition capacities).</summary>
    Public Function ParseCartridgeReport(cmPath As String) As String()
        Dim barcode As String = "", serial As String = "", fmt As String = "", vendor As String = ""
        Dim capacity As Long = 0
        Try
            Using fs As New FileStream(cmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                Using sr As New StreamReader(fs, Encoding.UTF8)
                    For i As Integer = 0 To 80
                        Dim line As String = sr.ReadLine()
                        If line Is Nothing Then Exit For
                        Dim v As String = Field(line, "Barcode")
                        If v IsNot Nothing Then barcode = v
                        v = Field(line, "Serial number")
                        If v IsNot Nothing Then serial = v
                        v = Field(line, "Format")
                        If v IsNot Nothing Then fmt = v.Split(" "c)(0)
                        v = Field(line, "Tape Vendor")
                        If v IsNot Nothing Then vendor = v
                        Dim pm As Match = Regex.Match(line, "Partition \d+ size:\s*[^/]*/\s*(?<cap>[\d.]+\s*\S+)")
                        If pm.Success Then capacity += ParseSize(pm.Groups("cap").Value)
                        If line.Contains("WRAP ANALYSIS") Then Exit For
                    Next
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        Return New String() {barcode, serial, fmt, vendor, capacity.ToString(CultureInfo.InvariantCulture)}
    End Function

    Private Function Field(line As String, name As String) As String
        Dim m As Match = Regex.Match(line, "^\|\s*" & Regex.Escape(name) & ":\s*(?<v>.*?)\s*\|\s*$")
        Return If(m.Success, m.Groups("v").Value, Nothing)
    End Function

    ''' <summary>"5.41 TiB" / "102.63 GiB" / "0 Bytes" to bytes (binary units, as the report prints them).</summary>
    Public Function ParseSize(s As String) As Long
        Dim m As Match = Regex.Match(s.Trim(), "^(?<n>[\d.]+)\s*(?<u>[A-Za-z]*)$")
        If Not m.Success Then Return 0
        Dim n As Double
        If Not Double.TryParse(m.Groups("n").Value, NumberStyles.Float, CultureInfo.InvariantCulture, n) Then Return 0
        Dim mult As Double
        Select Case m.Groups("u").Value.ToUpperInvariant()
            Case "", "B", "BYTE", "BYTES" : mult = 1
            Case "K", "KB", "KIB" : mult = 1024.0
            Case "M", "MB", "MIB" : mult = 1024.0 ^ 2
            Case "G", "GB", "GIB" : mult = 1024.0 ^ 3
            Case "T", "TB", "TIB" : mult = 1024.0 ^ 4
            Case "P", "PB", "PIB" : mult = 1024.0 ^ 5
            Case Else : Return 0
        End Select
        Return CLng(n * mult)
    End Function

    ' ---- index header ------------------------------------------------------------------------
    Public Function OpenIndex(info As IndexFileInfo) As Stream
        Dim fs As New FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 1 << 20, FileOptions.SequentialScan)
        If info.IsZst Then Return New ZstdSharp.DecompressionStream(fs)
        Return fs
    End Function

    ''' <summary>Volume UUID, generation and update time from the start of the index; IsBlank when it holds no file.</summary>
    Public Sub PeekHeader(info As IndexFileInfo)
        Const Limit As Integer = 256 * 1024
        Dim buf(Limit - 1) As Byte
        Dim got As Integer = 0
        Dim ended As Boolean = False
        Using s As Stream = OpenIndex(info)
            While got < Limit
                Dim n As Integer = s.Read(buf, got, Limit - got)
                If n = 0 Then ended = True : Exit While
                got += n
            End While
        End Using
        Dim text As String = Encoding.UTF8.GetString(buf, 0, got)
        Dim m As Match = Regex.Match(text, "<volumeuuid>\s*(?<v>[^<]*?)\s*</volumeuuid>")
        If m.Success Then info.VolumeUuid = m.Groups("v").Value
        m = Regex.Match(text, "<generationnumber>\s*(?<v>\d+)\s*</generationnumber>")
        If m.Success Then info.HeaderGeneration = Long.Parse(m.Groups("v").Value, CultureInfo.InvariantCulture)
        m = Regex.Match(text, "<updatetime>\s*(?<v>[^<]*?)\s*</updatetime>")
        If m.Success Then info.UpdateTime = ParseLtfsTime(m.Groups("v").Value)
        info.IsBlank = ended AndAlso text.IndexOf("<file>", StringComparison.Ordinal) < 0
    End Sub

    ''' <summary>Compares what the index folder holds with the catalog and says what to do.</summary>
    Public Function Plan(store As CatalogStore, folder As String) As ScanPlan
        Dim p As New ScanPlan
        Dim known As New Dictionary(Of String, TapeInfo)(StringComparer.Ordinal)
        Using c As DbConnection = store.OpenConnection()   ' runs off the UI thread: its own connection
            For Each t As TapeInfo In store.GetTapes(c)
                known(t.Barcode.ToUpperInvariant() & "|" & t.Serial) = t
            Next
        End Using
        For Each info As IndexFileInfo In Scan(folder, p.Errors)
            Try
                PeekHeader(info)
            Catch ex As Exception
                p.Errors.Add("Cannot read " & info.Path & ": " & ex.Message)
                Continue For
            End Try
            Dim tape As TapeInfo = Nothing
            known.TryGetValue(info.Barcode.ToUpperInvariant() & "|" & info.Serial, tape)
            If tape Is Nothing OrElse tape.State = TapeInfo.StateAsk Then
                p.NewTapes.Add(info)
            ElseIf tape.State = TapeInfo.StateTracked Then
                Dim sameVolume As Boolean = String.Equals(tape.VolumeUuid, info.VolumeUuid, StringComparison.OrdinalIgnoreCase)
                If sameVolume AndAlso tape.Generation = info.HeaderGeneration Then Continue For  ' same index content
                If Not sameVolume AndAlso Not String.IsNullOrEmpty(tape.VolumeUuid) AndAlso info.IsBlank Then
                    p.ReformattedBlank.Add(Tuple.Create(tape, info))
                Else
                    p.Updates.Add(Tuple.Create(tape, info))
                End If
            End If
        Next
        Return p
    End Function

End Module

''' <summary>Streams one LTFS index into the catalog, replacing that tape's entries in one transaction.</summary>
Friend NotInheritable Class IndexImporter

    Private NotInheritable Class DirFrame
        Public Id As Long
        Public IsRoot As Boolean
        Public Parent As DirFrame
        Public Name As String = ""
        Public CachedPath As String
        Public MTime, CTime, ATime, FileUid As Long?
        Public IsReadOnly As Boolean
        Public Files As Long
        Public Bytes As Long
    End Class

    Private NotInheritable Class FileFrame
        Public Id As Long
        Public Name As String = ""
        Public Length As Long
        Public MTime, CTime, ATime, FileUid, StartBlock As Long?
        Public Partition As String
        Public IsReadOnly As Boolean
        Public Hashes As StringBuilder
        Public Extents As Integer
    End Class

    Private Shared ReadOnly Containers As New HashSet(Of String)(StringComparer.Ordinal) From {
        "ltfsindex", "directory", "contents", "file", "extendedattributes", "xattr", "extentinfo", "extent",
        "location", "previousgenerationlocation", "dataplacementpolicy", "indexpartitioncriteria"}
    Private Shared ReadOnly WantedLeaves As New HashSet(Of String)(StringComparer.Ordinal) From {
        "name", "length", "readonly", "creationtime", "changetime", "modifytime", "accesstime", "fileuid",
        "key", "value", "partition", "startblock", "volumeuuid", "generationnumber", "updatetime"}

    Public FileCount As Long
    Public DirCount As Long
    Public ByteCount As Long
    Public VolumeUuid As String = ""
    Public Generation As Long
    Public UpdateTime As Long?

    Private _nextId As Long
    Private _tapeId As Long
    Private _ins As DbCommand
    Private _fts As DbCommand
    Private _p As New Dictionary(Of String, DbParameter)
    Private _fid, _fk As DbParameter

    ''' <summary>Imports the index for the tape. progress(text) is called now and then; ct cancels (rolls back).</summary>
    Public Shared Function Run(store As CatalogStore, info As IndexFileInfo, tapeId As Long, progress As Action(Of String), ct As CancellationToken) As IndexImporter
        Dim imp As New IndexImporter With {._tapeId = tapeId}
        Using c As DbConnection = store.OpenConnection()
            CatalogStore.Exec(c, "BEGIN IMMEDIATE")
            Dim ok As Boolean = False
            Try
                CatalogStore.DeleteTapeEntries(c, tapeId)
                imp._nextId = Convert.ToInt64(CatalogStore.Scalar(c, "SELECT IFNULL(MAX(id), 0) + 1 FROM entries"), CultureInfo.InvariantCulture)
                imp.PrepareCommands(c)
                Using raw As New FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 1 << 20, FileOptions.SequentialScan)
                    Dim input As Stream = If(info.IsZst, CType(New ZstdSharp.DecompressionStream(raw), Stream), raw)
                    Dim settings As New XmlReaderSettings With {.IgnoreWhitespace = True, .IgnoreComments = True,
                        .IgnoreProcessingInstructions = True, .DtdProcessing = DtdProcessing.Prohibit, .CloseInput = False}
                    Using xr As XmlReader = XmlReader.Create(input, settings)
                        imp.Parse(xr, raw, info.Barcode, progress, ct)
                    End Using
                    input.Dispose()
                End Using
                CatalogStore.Exec(c, "UPDATE tapes SET volume_uuid = @p1, generation = @p2, index_time = @p3, index_file = @p4, " &
                    "index_file_size = @p5, index_file_mtime = @p6, format = @p7, vendor = @p8, capacity = @p9, file_count = @p10, " &
                    "dir_count = @p11, total_bytes = @p12, updated = @p13, hidden = 0, last_error = NULL WHERE id = @p0",
                    tapeId, imp.VolumeUuid, imp.Generation, CatalogStore.DbVal(imp.UpdateTime), info.Path, info.FileSize, info.MTime,
                    info.Format, info.Vendor, info.Capacity, imp.FileCount, imp.DirCount, imp.ByteCount, ToUnix(DateTime.UtcNow))
                CatalogStore.Exec(c, "COMMIT")
                ok = True
            Finally
                If Not ok Then
                    Try
                        CatalogStore.Exec(c, "ROLLBACK")
                    Catch
                    End Try
                End If
                imp._ins?.Dispose()
                imp._fts?.Dispose()
            End Try
            Try
                CatalogStore.Exec(c, "PRAGMA wal_checkpoint(PASSIVE);")
            Catch
            End Try
        End Using
        store.InvalidatePaths()
        Return imp
    End Function

    Private Sub PrepareCommands(c As DbConnection)
        _ins = c.CreateCommand()
        _ins.CommandText = "INSERT INTO entries(id, tape_id, parent_id, is_dir, name, ext, size, items, mtime, ctime, atime, readonly, part, startblock, fileuid, hashes, path, pkey) " &
                           "VALUES(@id, @tape, @parent, @dir, @name, @ext, @size, @items, @mtime, @ctime, @atime, @ro, @part, @sb, @uid, @hashes, @path, @pkey)"
        For Each n As String In {"@id", "@tape", "@parent", "@dir", "@name", "@ext", "@size", "@items", "@mtime", "@ctime", "@atime", "@ro", "@part", "@sb", "@uid", "@hashes", "@path", "@pkey"}
            _p(n) = CatalogStore.NewParam(_ins, n)
        Next
        _ins.Prepare()
        _fts = c.CreateCommand()
        _fts.CommandText = "INSERT INTO entry_fts(rowid, k) VALUES(@fid, @fk)"
        _fid = CatalogStore.NewParam(_fts, "@fid")
        _fk = CatalogStore.NewParam(_fts, "@fk")
        _fts.Prepare()
    End Sub

    Private Shared Function V(x As Long?) As Object
        Return CatalogStore.DbVal(x)
    End Function

    Private Shared Function PathOf(d As DirFrame) As String
        If d.IsRoot Then Return ""
        If d.CachedPath Is Nothing Then d.CachedPath = PathOf(d.Parent) & "\" & d.Name
        Return d.CachedPath
    End Function

    Private Sub InsertRow(id As Long, parent As DirFrame, isDir As Boolean, name As String, size As Long, items As Long?,
                          mtime As Long?, ctime As Long?, atime As Long?, ro As Boolean, part As String, sb As Long?,
                          uid As Long?, hashes As String, path As String)
        _p("@id").Value = id
        _p("@tape").Value = _tapeId
        _p("@parent").Value = If(parent Is Nothing OrElse parent.IsRoot, DBNull.Value, CObj(parent.Id))
        _p("@dir").Value = If(isDir, 1, 0)
        _p("@name").Value = name
        Dim ext As String = If(isDir, Nothing, ExtensionOf(name))
        _p("@ext").Value = If(ext, CObj(DBNull.Value))
        _p("@size").Value = size
        _p("@items").Value = V(items)
        _p("@mtime").Value = V(mtime)
        _p("@ctime").Value = V(ctime)
        _p("@atime").Value = V(atime)
        _p("@ro").Value = If(ro, 1, 0)
        _p("@part").Value = If(String.IsNullOrEmpty(part), DBNull.Value, CObj(part))
        _p("@sb").Value = V(sb)
        _p("@uid").Value = V(uid)
        _p("@hashes").Value = If(String.IsNullOrEmpty(hashes), DBNull.Value, CObj(hashes))
        _p("@path").Value = If(path, CObj(DBNull.Value))
        _p("@pkey").Value = If(path Is Nothing, DBNull.Value, CObj(SearchKey(path)))
        _ins.ExecuteNonQuery()
        _fid.Value = id
        _fk.Value = SearchKey(name)
        _fts.ExecuteNonQuery()
    End Sub

    Private Sub Parse(xr As XmlReader, raw As Stream, label As String, progress As Action(Of String), ct As CancellationToken)
        Dim stack As New Stack(Of String)
        Dim dirs As New Stack(Of DirFrame)
        Dim file As FileFrame = Nothing
        Dim xKey As String = Nothing, xVal As String = Nothing
        Dim exPart As String = Nothing
        Dim exBlock As Long? = Nothing
        Dim count As Long = 0
        Dim needRead As Boolean = True
        While True
            If needRead AndAlso Not xr.Read() Then Exit While
            needRead = True
            Select Case xr.NodeType
                Case XmlNodeType.Element
                    Dim nm As String = xr.LocalName
                    If Containers.Contains(nm) Then
                        If xr.IsEmptyElement Then Continue While
                        Select Case nm
                            Case "directory"
                                Dim parent As DirFrame = If(dirs.Count > 0, dirs.Peek(), Nothing)
                                Dim d As New DirFrame With {.IsRoot = parent Is Nothing, .Parent = parent}
                                If Not d.IsRoot Then
                                    d.Id = _nextId
                                    _nextId += 1
                                End If
                                dirs.Push(d)
                            Case "file"
                                file = New FileFrame With {.Id = _nextId}
                                _nextId += 1
                            Case "xattr"
                                xKey = Nothing
                                xVal = Nothing
                            Case "extent"
                                exPart = Nothing
                                exBlock = Nothing
                        End Select
                        stack.Push(nm)
                    ElseIf WantedLeaves.Contains(nm) Then
                        Dim pe As String = xr.GetAttribute("percentencoded")
                        Dim val As String = xr.ReadElementContentAsString()
                        needRead = False
                        If pe IsNot Nothing AndAlso pe.Equals("true", StringComparison.OrdinalIgnoreCase) Then val = Uri.UnescapeDataString(val)
                        Dim ctx As String = If(stack.Count > 0, stack.Peek(), "")
                        Select Case ctx
                            Case "ltfsindex"
                                Select Case nm
                                    Case "volumeuuid" : VolumeUuid = val.Trim()
                                    Case "generationnumber" : Long.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, Generation)
                                    Case "updatetime" : UpdateTime = ParseLtfsTime(val.Trim())
                                End Select
                            Case "directory"
                                If dirs.Count > 0 Then
                                    Dim d As DirFrame = dirs.Peek()
                                    Select Case nm
                                        Case "name" : d.Name = val
                                        Case "readonly" : d.IsReadOnly = val.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                                        Case "creationtime" : d.CTime = ParseLtfsTime(val.Trim())
                                        Case "modifytime" : d.MTime = ParseLtfsTime(val.Trim())
                                        Case "accesstime" : d.ATime = ParseLtfsTime(val.Trim())
                                        Case "fileuid" : d.FileUid = ParseLong(val)
                                    End Select
                                End If
                            Case "file"
                                If file IsNot Nothing Then
                                    Select Case nm
                                        Case "name" : file.Name = val
                                        Case "length" : file.Length = If(ParseLong(val), 0L)
                                        Case "readonly" : file.IsReadOnly = val.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                                        Case "creationtime" : file.CTime = ParseLtfsTime(val.Trim())
                                        Case "modifytime" : file.MTime = ParseLtfsTime(val.Trim())
                                        Case "accesstime" : file.ATime = ParseLtfsTime(val.Trim())
                                        Case "fileuid" : file.FileUid = ParseLong(val)
                                    End Select
                                End If
                            Case "xattr"
                                If nm = "key" Then xKey = val.Trim() Else If nm = "value" Then xVal = val.Trim()
                            Case "extent"
                                If nm = "partition" Then exPart = val.Trim() Else If nm = "startblock" Then exBlock = ParseLong(val)
                        End Select
                    Else
                        xr.Skip()
                        needRead = False
                    End If
                Case XmlNodeType.EndElement
                    Dim nm As String = xr.LocalName
                    If stack.Count > 0 AndAlso stack.Peek() = nm Then stack.Pop()
                    Select Case nm
                        Case "xattr"
                            If file IsNot Nothing AndAlso xKey IsNot Nothing AndAlso xKey.StartsWith("ltfs.hash.", StringComparison.OrdinalIgnoreCase) AndAlso Not String.IsNullOrEmpty(xVal) Then
                                Dim algo As String = xKey.Substring(10)
                                If algo.EndsWith("sum", StringComparison.OrdinalIgnoreCase) Then algo = algo.Substring(0, algo.Length - 3)
                                If file.Hashes Is Nothing Then file.Hashes = New StringBuilder()
                                If file.Hashes.Length > 0 Then file.Hashes.Append(ControlChars.Lf)
                                file.Hashes.Append(algo.ToUpperInvariant()).Append(":"c).Append(xVal.Replace(vbCr, "").Replace(vbLf, ""))
                            End If
                        Case "extent"
                            If file IsNot Nothing Then
                                If file.Extents = 0 Then
                                    file.Partition = exPart
                                    file.StartBlock = exBlock
                                End If
                                file.Extents += 1
                            End If
                        Case "file"
                            If file IsNot Nothing Then
                                Dim parent As DirFrame = If(dirs.Count > 0, dirs.Peek(), Nothing)
                                InsertRow(file.Id, parent, False, file.Name, file.Length, Nothing, file.MTime, file.CTime, file.ATime,
                                          file.IsReadOnly, file.Partition, file.StartBlock, file.FileUid,
                                          If(file.Hashes Is Nothing, Nothing, file.Hashes.ToString()), Nothing)
                                If parent IsNot Nothing Then
                                    parent.Files += 1
                                    parent.Bytes += file.Length
                                End If
                                FileCount += 1
                                ByteCount += file.Length
                                file = Nothing
                                count += 1
                                Tick(count, raw, label, progress, ct)
                            End If
                        Case "directory"
                            If dirs.Count > 0 Then
                                Dim d As DirFrame = dirs.Pop()
                                If Not d.IsRoot Then
                                    InsertRow(d.Id, d.Parent, True, d.Name, d.Bytes, d.Files, d.MTime, d.CTime, d.ATime,
                                              d.IsReadOnly, Nothing, Nothing, d.FileUid, Nothing, PathOf(d))
                                    If d.Parent IsNot Nothing Then
                                        d.Parent.Files += d.Files
                                        d.Parent.Bytes += d.Bytes
                                    End If
                                    DirCount += 1
                                    count += 1
                                    Tick(count, raw, label, progress, ct)
                                End If
                            End If
                    End Select
            End Select
        End While
        If dirs.Count > 0 OrElse String.IsNullOrEmpty(VolumeUuid) Then
            Throw New InvalidDataException("The index ends early or is not an LTFS index (it may still be being written).")
        End If
    End Sub

    Private Shared Function ParseLong(s As String) As Long?
        Dim v As Long
        If Long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, v) Then Return v
        Return Nothing
    End Function

    Private Shared Sub Tick(count As Long, raw As Stream, label As String, progress As Action(Of String), ct As CancellationToken)
        If count Mod 2000 <> 0 Then Return
        ct.ThrowIfCancellationRequested()
        If progress IsNot Nothing AndAlso count Mod 10000 = 0 Then
            Dim pct As String = ""
            Try
                If raw.Length > 0 Then pct = " (" & CInt(100L * raw.Position \ raw.Length).ToString(CultureInfo.InvariantCulture) & "%)"
            Catch
            End Try
            progress("Importing " & label & ": " & FormatCount(count) & " entries" & pct)
        End If
    End Sub

End Class
