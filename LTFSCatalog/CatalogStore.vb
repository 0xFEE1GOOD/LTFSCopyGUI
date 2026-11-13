Imports System.Data.Common
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Reflection

''' <summary>One tape known to the catalog. State: 0 = not decided yet, 1 = tracked, 2 = never.</summary>
Friend NotInheritable Class TapeInfo
    Public Id As Long
    Public Barcode As String = ""
    Public Serial As String = ""
    Public State As Integer
    Public Hidden As Boolean
    Public VolumeUuid As String = ""
    Public Generation As Long
    Public IndexTime As Long?
    Public IndexFile As String = ""
    Public IndexFileSize As Long
    Public IndexFileMtime As Long
    Public Format As String = ""
    Public Vendor As String = ""
    Public Capacity As Long
    Public FileCount As Long
    Public DirCount As Long
    Public TotalBytes As Long
    Public FirstSeen As Long?
    Public Updated As Long?
    Public LastError As String = ""

    Public Const StateAsk As Integer = 0
    Public Const StateTracked As Integer = 1
    Public Const StateNever As Integer = 2

    Public ReadOnly Property Label As String
        Get
            Return If(String.IsNullOrEmpty(Serial), Barcode, Barcode & " [" & Serial & "]")
        End Get
    End Property
End Class

''' <summary>A file or folder row as the lists show it.</summary>
Friend Class EntryRow
    Public Id As Long
    Public TapeId As Long
    Public ParentId As Long?
    Public IsDir As Boolean
    Public Name As String = ""
    Public Size As Long
    Public Items As Long
    Public MTime As Long?
    Public CTime As Long?
    Public HasDirs As Boolean
End Class

''' <summary>Everything stored for one entry (the details pane).</summary>
Friend NotInheritable Class EntryDetails
    Inherits EntryRow
    Public ATime As Long?
    Public IsReadOnly As Boolean
    Public Partition As String = ""
    Public StartBlock As Long?
    Public FileUid As Long?
    Public Hashes As String = ""
    Public FolderPath As String = ""
End Class

''' <summary>The catalog database (SQLite, WAL). Each thread that queries uses its own connection.</summary>
Friend NotInheritable Class CatalogStore
    Implements IDisposable

    Public ReadOnly DbPath As String
    Public Property FtsAvailable As Boolean
    Private ReadOnly _connType As Type
    Private ReadOnly _ui As DbConnection
    Private ReadOnly _dirPaths As New Dictionary(Of Long, String)
    Private ReadOnly _cacheLock As New Object()

    Public Sub New(dbPath As String)
        Me.DbPath = dbPath
        Dim asmPath As String = Path.Combine(AppDir, "System.Data.SQLite.dll")
        Dim asm As Assembly = If(File.Exists(asmPath), Assembly.LoadFrom(asmPath), Assembly.Load("System.Data.SQLite"))
        _connType = asm.GetType("System.Data.SQLite.SQLiteConnection", True)
        Dim dir As String = Path.GetDirectoryName(dbPath)
        If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
        _ui = OpenConnection()
        EnsureSchema(_ui)
    End Sub

    Public Function OpenConnection() As DbConnection
        Dim b As New DbConnectionStringBuilder()
        b("Data Source") = DbPath
        b("Pooling") = "False"
        b("Default Timeout") = "15"
        Dim c As DbConnection = DirectCast(Activator.CreateInstance(_connType, New Object() {b.ConnectionString}), DbConnection)
        c.Open()
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;")
        Return c
    End Function

    Private Sub EnsureSchema(c As DbConnection)
        Exec(c, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);")
        Exec(c, "CREATE TABLE IF NOT EXISTS tapes(" &
                "id INTEGER PRIMARY KEY, barcode TEXT NOT NULL, serial TEXT NOT NULL DEFAULT '', " &
                "state INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0, " &
                "volume_uuid TEXT, generation INTEGER, index_time INTEGER, " &
                "index_file TEXT, index_file_size INTEGER, index_file_mtime INTEGER, " &
                "format TEXT, vendor TEXT, capacity INTEGER, " &
                "file_count INTEGER NOT NULL DEFAULT 0, dir_count INTEGER NOT NULL DEFAULT 0, total_bytes INTEGER NOT NULL DEFAULT 0, " &
                "first_seen INTEGER, updated INTEGER, last_error TEXT, " &
                "UNIQUE(barcode, serial));")
        ' Files and folders of every tape. parent_id is NULL for the tape's top level. size of a folder is the
        ' total of everything below it, items the number of files below it. path/pkey are kept for folders only.
        Exec(c, "CREATE TABLE IF NOT EXISTS entries(" &
                "id INTEGER PRIMARY KEY, tape_id INTEGER NOT NULL REFERENCES tapes(id) ON DELETE CASCADE, " &
                "parent_id INTEGER, is_dir INTEGER NOT NULL, name TEXT NOT NULL, ext TEXT, " &
                "size INTEGER NOT NULL DEFAULT 0, items INTEGER, mtime INTEGER, ctime INTEGER, atime INTEGER, " &
                "readonly INTEGER, part TEXT, startblock INTEGER, fileuid INTEGER, hashes TEXT, path TEXT, pkey TEXT);")
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_entries_tape_parent ON entries(tape_id, parent_id);")
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_entries_ext ON entries(ext);")
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_entries_size ON entries(size);")
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_entries_mtime ON entries(mtime);")
        Exec(c, "CREATE INDEX IF NOT EXISTS ix_entries_ctime ON entries(ctime);")
        ' Search index on the normalised name (Util.SearchKey). Trigram = indexed substring, LIKE/GLOB and
        ' boolean MATCH queries. rowid = entries.id.
        Dim existing As String = TryCast(Scalar(c, "SELECT sql FROM sqlite_master WHERE name = 'entry_fts'"), String)
        If String.IsNullOrEmpty(existing) Then
            Try
                Exec(c, "CREATE VIRTUAL TABLE entry_fts USING fts5(k, tokenize = 'trigram case_sensitive 1');")
                FtsAvailable = True
            Catch ex As Exception
                LogError("FTS5 trigram is not available in this SQLite build; search falls back to a scan", ex)
                Exec(c, "CREATE TABLE entry_fts(id INTEGER PRIMARY KEY, k TEXT);")
                FtsAvailable = False
            End Try
        Else
            FtsAvailable = existing.IndexOf("fts5", StringComparison.OrdinalIgnoreCase) >= 0
        End If
        If GetMeta("schema_version") Is Nothing Then SetMeta("schema_version", "1")
    End Sub

    ' ---- small ADO.NET helpers (positional @p0, @p1, ...) -----------------------------------------
    Public Shared Sub AddArgs(cmd As DbCommand, args As Object())
        If args Is Nothing Then Return
        For i As Integer = 0 To args.Length - 1
            Dim p As DbParameter = cmd.CreateParameter()
            p.ParameterName = "@p" & i.ToString(CultureInfo.InvariantCulture)
            p.Value = If(args(i), DBNull.Value)
            cmd.Parameters.Add(p)
        Next
    End Sub

    Public Shared Sub Exec(c As DbConnection, sql As String, ParamArray args As Object())
        Using cmd As DbCommand = c.CreateCommand()
            cmd.CommandText = sql
            AddArgs(cmd, args)
            cmd.ExecuteNonQuery()
        End Using
    End Sub

    Public Shared Function Scalar(c As DbConnection, sql As String, ParamArray args As Object()) As Object
        Using cmd As DbCommand = c.CreateCommand()
            cmd.CommandText = sql
            AddArgs(cmd, args)
            Dim v As Object = cmd.ExecuteScalar()
            Return If(TypeOf v Is DBNull, Nothing, v)
        End Using
    End Function

    Public Shared Function NewParam(cmd As DbCommand, name As String) As DbParameter
        Dim p As DbParameter = cmd.CreateParameter()
        p.ParameterName = name
        cmd.Parameters.Add(p)
        Return p
    End Function

    Public Shared Function L(r As DbDataReader, i As Integer) As Long
        Return If(r.IsDBNull(i), 0L, Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture))
    End Function

    Public Shared Function LN(r As DbDataReader, i As Integer) As Long?
        If r.IsDBNull(i) Then Return Nothing
        Return Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture)
    End Function

    Public Shared Function S(r As DbDataReader, i As Integer) As String
        Return If(r.IsDBNull(i), "", Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))
    End Function

    Public Shared Function DbVal(v As Long?) As Object
        Return If(v.HasValue, CObj(v.Value), DBNull.Value)
    End Function

    ' ---- meta ----------------------------------------------------------------------------------
    Public Function GetMeta(key As String) As String
        Return TryCast(Scalar(_ui, "SELECT value FROM meta WHERE key = @p0", key), String)
    End Function

    Public Sub SetMeta(key As String, value As String)
        Exec(_ui, "INSERT INTO meta(key, value) VALUES(@p0, @p1) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, value)
    End Sub

    ' ---- tapes ---------------------------------------------------------------------------------
    Private Const TapeCols As String = "id, barcode, serial, state, hidden, volume_uuid, generation, index_time, index_file, index_file_size, index_file_mtime, format, vendor, capacity, file_count, dir_count, total_bytes, first_seen, updated, last_error"

    Private Shared Function ReadTape(r As DbDataReader) As TapeInfo
        Return New TapeInfo With {
            .Id = L(r, 0), .Barcode = S(r, 1), .Serial = S(r, 2), .State = CInt(L(r, 3)), .Hidden = L(r, 4) <> 0,
            .VolumeUuid = S(r, 5), .Generation = L(r, 6), .IndexTime = LN(r, 7), .IndexFile = S(r, 8),
            .IndexFileSize = L(r, 9), .IndexFileMtime = L(r, 10), .Format = S(r, 11), .Vendor = S(r, 12),
            .Capacity = L(r, 13), .FileCount = L(r, 14), .DirCount = L(r, 15), .TotalBytes = L(r, 16),
            .FirstSeen = LN(r, 17), .Updated = LN(r, 18), .LastError = S(r, 19)}
    End Function

    Public Function GetTapes(Optional conn As DbConnection = Nothing) As List(Of TapeInfo)
        Dim c As DbConnection = If(conn, _ui)
        Dim list As New List(Of TapeInfo)
        Using cmd As DbCommand = c.CreateCommand()
            cmd.CommandText = "SELECT " & TapeCols & " FROM tapes ORDER BY barcode COLLATE NOCASE, serial"
            Using r As DbDataReader = cmd.ExecuteReader()
                While r.Read()
                    list.Add(ReadTape(r))
                End While
            End Using
        End Using
        Return list
    End Function

    Public Function GetTape(id As Long, Optional conn As DbConnection = Nothing) As TapeInfo
        Dim c As DbConnection = If(conn, _ui)
        Using cmd As DbCommand = c.CreateCommand()
            cmd.CommandText = "SELECT " & TapeCols & " FROM tapes WHERE id = @p0"
            AddArgs(cmd, New Object() {id})
            Using r As DbDataReader = cmd.ExecuteReader()
                If r.Read() Then Return ReadTape(r)
            End Using
        End Using
        Return Nothing
    End Function

    Public Function FindTape(barcode As String, serial As String, Optional conn As DbConnection = Nothing) As TapeInfo
        Dim c As DbConnection = If(conn, _ui)
        Using cmd As DbCommand = c.CreateCommand()
            cmd.CommandText = "SELECT " & TapeCols & " FROM tapes WHERE barcode = @p0 AND serial = @p1"
            AddArgs(cmd, New Object() {barcode, If(serial, "")})
            Using r As DbDataReader = cmd.ExecuteReader()
                If r.Read() Then Return ReadTape(r)
            End Using
        End Using
        Return Nothing
    End Function

    Public Function AddTape(barcode As String, serial As String, state As Integer, Optional conn As DbConnection = Nothing) As Long
        Dim c As DbConnection = If(conn, _ui)
        Exec(c, "INSERT INTO tapes(barcode, serial, state, first_seen) VALUES(@p0, @p1, @p2, @p3) " &
                "ON CONFLICT(barcode, serial) DO UPDATE SET state = excluded.state",
             barcode, If(serial, ""), state, ToUnix(DateTime.UtcNow))
        Return Convert.ToInt64(Scalar(c, "SELECT id FROM tapes WHERE barcode = @p0 AND serial = @p1", barcode, If(serial, "")), CultureInfo.InvariantCulture)
    End Function

    Public Sub SetTapeState(id As Long, state As Integer)
        Exec(_ui, "UPDATE tapes SET state = @p1 WHERE id = @p0", id, state)
    End Sub

    Public Sub SetTapeHidden(id As Long, hidden As Boolean)
        Exec(_ui, "UPDATE tapes SET hidden = @p1 WHERE id = @p0", id, If(hidden, 1, 0))
    End Sub

    Public Sub SetTapeError(id As Long, message As String, Optional conn As DbConnection = Nothing)
        Exec(If(conn, _ui), "UPDATE tapes SET last_error = @p1 WHERE id = @p0", id, If(message, ""))
    End Sub

    ''' <summary>Removes a tape's files and folders (search index first). The caller owns the transaction.</summary>
    Public Shared Sub DeleteTapeEntries(c As DbConnection, tapeId As Long)
        Exec(c, "DELETE FROM entry_fts WHERE rowid IN (SELECT id FROM entries WHERE tape_id = @p0)", tapeId)
        Exec(c, "DELETE FROM entries WHERE tape_id = @p0", tapeId)
    End Sub

    ''' <summary>Empties a tape in the catalog (keeps the tape row and its tracking decision).</summary>
    Public Sub ClearTape(tapeId As Long, hide As Boolean)
        Using tx As DbTransaction = _ui.BeginTransaction()
            DeleteTapeEntries(_ui, tapeId)
            Exec(_ui, "UPDATE tapes SET file_count = 0, dir_count = 0, total_bytes = 0, hidden = @p1, updated = @p2 WHERE id = @p0",
                 tapeId, If(hide, 1, 0), ToUnix(DateTime.UtcNow))
            tx.Commit()
        End Using
        InvalidatePaths()
    End Sub

    ''' <summary>Totals of the tapes that are shown: (tapes, files, bytes).</summary>
    Public Function Totals() As Tuple(Of Long, Long, Long)
        Using cmd As DbCommand = _ui.CreateCommand()
            cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(file_count), 0), IFNULL(SUM(total_bytes), 0) FROM tapes WHERE state = 1 AND hidden = 0"
            Using r As DbDataReader = cmd.ExecuteReader()
                r.Read()
                Return Tuple.Create(L(r, 0), L(r, 1), L(r, 2))
            End Using
        End Using
    End Function

    ' ---- browsing --------------------------------------------------------------------------------
    Public Function GetChildren(tapeId As Long, parentId As Long?, Optional dirsOnly As Boolean = False) As List(Of EntryRow)
        Dim list As New List(Of EntryRow)
        Using cmd As DbCommand = _ui.CreateCommand()
            cmd.CommandText = "SELECT e.id, e.parent_id, e.is_dir, e.name, e.size, e.items, e.mtime, e.ctime, " &
                              "CASE WHEN e.is_dir = 1 AND EXISTS(SELECT 1 FROM entries c WHERE c.tape_id = e.tape_id AND c.parent_id = e.id AND c.is_dir = 1) THEN 1 ELSE 0 END " &
                              "FROM entries e WHERE e.tape_id = @p0 AND e.parent_id IS @p1" & If(dirsOnly, " AND e.is_dir = 1", "")
            AddArgs(cmd, New Object() {tapeId, DbVal(parentId)})
            Using r As DbDataReader = cmd.ExecuteReader()
                While r.Read()
                    list.Add(New EntryRow With {.Id = L(r, 0), .TapeId = tapeId, .ParentId = LN(r, 1), .IsDir = L(r, 2) <> 0,
                             .Name = S(r, 3), .Size = L(r, 4), .Items = L(r, 5), .MTime = LN(r, 6), .CTime = LN(r, 7), .HasDirs = L(r, 8) <> 0})
                End While
            End Using
        End Using
        list.Sort(AddressOf CompareBrowse)
        Return list
    End Function

    ''' <summary>Folders first, then names in the user's culture, ignoring case (as Explorer sorts).</summary>
    Public Shared Function CompareBrowse(a As EntryRow, b As EntryRow) As Integer
        If a.IsDir <> b.IsDir Then Return If(a.IsDir, -1, 1)
        Return StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)
    End Function

    Public Function GetDetails(id As Long) As EntryDetails
        Using cmd As DbCommand = _ui.CreateCommand()
            cmd.CommandText = "SELECT id, tape_id, parent_id, is_dir, name, size, items, mtime, ctime, atime, readonly, part, startblock, fileuid, hashes FROM entries WHERE id = @p0"
            AddArgs(cmd, New Object() {id})
            Using r As DbDataReader = cmd.ExecuteReader()
                If Not r.Read() Then Return Nothing
                Dim d As New EntryDetails With {
                    .Id = L(r, 0), .TapeId = L(r, 1), .ParentId = LN(r, 2), .IsDir = L(r, 3) <> 0, .Name = S(r, 4),
                    .Size = L(r, 5), .Items = L(r, 6), .MTime = LN(r, 7), .CTime = LN(r, 8), .ATime = LN(r, 9),
                    .IsReadOnly = L(r, 10) <> 0, .Partition = S(r, 11), .StartBlock = LN(r, 12), .FileUid = LN(r, 13), .Hashes = S(r, 14)}
                d.FolderPath = FolderOf(d.ParentId)
                Return d
            End Using
        End Using
    End Function

    ''' <summary>Ancestor folder ids of an entry, top level first (for revealing it in the tree).</summary>
    Public Function AncestorIds(id As Long) As List(Of Long)
        Dim chain As New List(Of Long)
        Dim cur As Object = Scalar(_ui, "SELECT parent_id FROM entries WHERE id = @p0", id)
        Dim guard As Integer = 0
        While cur IsNot Nothing AndAlso guard < 4096
            Dim pid As Long = Convert.ToInt64(cur, CultureInfo.InvariantCulture)
            chain.Insert(0, pid)
            cur = Scalar(_ui, "SELECT parent_id FROM entries WHERE id = @p0", pid)
            guard += 1
        End While
        Return chain
    End Function

    ''' <summary>Display path of a folder ("\" for a tape's top level). Cached. UI thread only (uses the UI connection).</summary>
    Public Function FolderOf(parentId As Long?) As String
        If Not parentId.HasValue Then Return "\"
        Dim p As String = Nothing
        SyncLock _cacheLock
            If _dirPaths.TryGetValue(parentId.Value, p) Then Return p
        End SyncLock
        p = TryCast(Scalar(_ui, "SELECT path FROM entries WHERE id = @p0", parentId.Value), String)
        If p Is Nothing Then p = "?"
        SyncLock _cacheLock
            If _dirPaths.Count > 200000 Then _dirPaths.Clear()
            _dirPaths(parentId.Value) = p
        End SyncLock
        Return p
    End Function

    ''' <summary>Folder paths for many parents at once (one query) - used when a result page is shown.</summary>
    Public Sub PrimeFolders(parentIds As IEnumerable(Of Long))
        Dim need As New List(Of Long)
        SyncLock _cacheLock
            For Each id As Long In parentIds
                If Not _dirPaths.ContainsKey(id) AndAlso Not need.Contains(id) Then need.Add(id)
                If need.Count >= 500 Then Exit For
            Next
        End SyncLock
        If need.Count = 0 Then Return
        Using cmd As DbCommand = _ui.CreateCommand()
            cmd.CommandText = "SELECT id, path FROM entries WHERE id IN (" & String.Join(",", need) & ")"
            Using r As DbDataReader = cmd.ExecuteReader()
                SyncLock _cacheLock
                    While r.Read()
                        _dirPaths(L(r, 0)) = S(r, 1)
                    End While
                End SyncLock
            End Using
        End Using
    End Sub

    ''' <summary>PrimeFolders for any number of parents (500 per query).</summary>
    Public Sub PrimeFoldersAll(parentIds As IEnumerable(Of Long))
        Dim all As List(Of Long) = parentIds.Distinct().ToList()
        For i As Integer = 0 To all.Count - 1 Step 500
            PrimeFolders(all.Skip(i).Take(500))
        Next
    End Sub

    ''' <summary>Makes the next refresh import the tape's newest index again.</summary>
    Public Sub ForceReimport(tapeId As Long)
        Exec(_ui, "UPDATE tapes SET generation = -1, hidden = 0 WHERE id = @p0", tapeId)
    End Sub

    Public Sub InvalidatePaths()
        SyncLock _cacheLock
            _dirPaths.Clear()
        End SyncLock
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Try
            Exec(_ui, "PRAGMA wal_checkpoint(TRUNCATE);")
        Catch
        End Try
        _ui.Dispose()
    End Sub

End Class
