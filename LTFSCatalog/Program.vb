Imports System.Data.Common
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Windows.Forms

''' <summary>Command line: [--index-folder DIR] [--db FILE]; tests: --selftest | --import [--track-all] | --search "q" [--kind files|folders] [--request-to DIR --dest PATH]; --report FILE.</summary>
Friend NotInheritable Class AppOptions
    Public IndexFolder As String
    Public DbPath As String = Path.Combine(AppDir, "catalog", "LTFSCatalog.db")
    Public Command As String
    Public Query As String
    Public Report As String
    Public TrackAll As Boolean
    Public Kind As KindFilter = KindFilter.All
    Public RequestTo As String
    Public Dest As String = "C:\LTFSCatalog-restore-test"

    Public Shared Function Parse(args As String()) As AppOptions
        Dim o As New AppOptions
        Dim i As Integer = 0
        While i < args.Length
            Dim a As String = args(i).ToLowerInvariant()
            Dim nextArg As Func(Of String) = Function()
                                                 i += 1
                                                 Return If(i < args.Length, args(i), "")
                                             End Function
            Select Case a
                Case "--index-folder" : o.IndexFolder = Path.GetFullPath(nextArg())
                Case "--db" : o.DbPath = Path.GetFullPath(nextArg())
                Case "--report" : o.Report = Path.GetFullPath(nextArg())
                Case "--selftest" : o.Command = "selftest"
                Case "--import" : o.Command = "import"
                Case "--search"
                    o.Command = "search"
                    o.Query = nextArg()
                Case "--track-all" : o.TrackAll = True
                Case "--request-to" : o.RequestTo = Path.GetFullPath(nextArg())
                Case "--dest" : o.Dest = nextArg()
                Case "--kind"
                    Dim k As String = nextArg().ToLowerInvariant()
                    o.Kind = If(k.StartsWith("file", StringComparison.Ordinal), KindFilter.Files, If(k.StartsWith("folder", StringComparison.Ordinal), KindFilter.Folders, KindFilter.All))
            End Select
            i += 1
        End While
        Util.LogPath = Path.Combine(Path.GetDirectoryName(o.DbPath), "LTFSCatalog.log")
        Return o
    End Function
End Class

Friend Module Program

    <STAThread>
    Public Sub Main(args As String())
        Dim o As AppOptions = AppOptions.Parse(args)
        If o.Command IsNot Nothing Then
            Environment.ExitCode = Headless.Run(o)
            Return
        End If
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)
        AddHandler Application.ThreadException, Sub(s, e)
                                                    LogError("Unhandled", e.Exception)
                                                    MessageBox.Show(e.Exception.Message & Environment.NewLine & Environment.NewLine & "Details are in " & Util.LogPath,
                                                                    "LTFSCatalog", MessageBoxButtons.OK, MessageBoxIcon.Error)
                                                End Sub
        AddHandler AppDomain.CurrentDomain.UnhandledException, Sub(s, e) Log("Fatal: " & e.ExceptionObject.ToString())
        Application.Run(New MainForm(o))
    End Sub

End Module

''' <summary>Non-interactive runs for testing a build: results go to --report.</summary>
Friend Module Headless

    Public Function Run(o As AppOptions) As Integer
        Dim sb As New StringBuilder()
        Dim code As Integer = 0
        Try
            Select Case o.Command
                Case "selftest" : code = SelfTest(sb)
                Case "import" : code = ImportAll(o, sb)
                Case "search" : code = SearchOnce(o, sb)
            End Select
        Catch ex As Exception
            sb.AppendLine("ERROR: " & ex.ToString())
            code = 1
        End Try
        If o.Report IsNot Nothing Then File.WriteAllText(o.Report, sb.ToString(), New UTF8Encoding(False))
        Return code
    End Function

    Private Function SelfTest(sb As StringBuilder) As Integer
        Dim db As String = Path.Combine(Path.GetTempPath(), "ltfscatalog-selftest-" & Guid.NewGuid().ToString("N") & ".db")
        Dim failed As Integer = 0
        Try
            Using store As New CatalogStore(db)
                Using c As DbConnection = store.OpenConnection()
                    sb.AppendLine("SQLite " & CStr(CatalogStore.Scalar(c, "SELECT sqlite_version()")) & ", System.Data.SQLite " &
                                  c.GetType().Assembly.GetName().Version.ToString() & ", " & If(Environment.Is64BitProcess, "64-bit", "32-bit") & " process")
                    sb.AppendLine("FTS5 trigram search index: " & If(store.FtsAvailable, "yes", "NO (scan fallback)"))
                    ' a small tape with names that exercise the matching rules
                    Dim tapeId As Long = store.AddTape("SELFTEST", "X1", TapeInfo.StateTracked, c)
                    Dim names As String() = {"Annual Report 2024.pdf", "ёлка.jpg", "IMG_2019_beach.JPG", "draft budget.xlsx", "budget final.xlsx",
                                             "Äpfel-Liste.txt", "Отчёт за год.docx", "notes [old].txt", "a.b"}
                    Dim id As Long = 1
                    CatalogStore.Exec(c, "INSERT INTO entries(id, tape_id, parent_id, is_dir, name, size, path, pkey) VALUES(1, @p0, NULL, 1, 'Projects', 0, '\Projects', @p1)", tapeId, SearchKey("\Projects"))
                    CatalogStore.Exec(c, "INSERT INTO entry_fts(rowid, k) VALUES(1, @p0)", SearchKey("Projects"))
                    For Each n As String In names
                        id += 1
                        CatalogStore.Exec(c, "INSERT INTO entries(id, tape_id, parent_id, is_dir, name, ext, size, mtime) VALUES(@p0, @p1, 1, 0, @p2, @p3, @p4, @p5)",
                                          id, tapeId, n, ExtensionOf(n), id * 1000000L, ToUnix(New DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)))
                        CatalogStore.Exec(c, "INSERT INTO entry_fts(rowid, k) VALUES(@p0, @p1)", id, SearchKey(n))
                    Next
                End Using
                Dim cases As Object(,) = {
                    {"report 2024", 1}, {"елка", 1}, {"ЁЛКА", 1}, {"*.jpg", 2}, {"budget -draft", 1}, {"ext:xlsx", 2},
                    {"apfel", 1}, {"""annual report""", 1}, {"img|ёлка", 2}, {"отчет", 1}, {"[old]", 1}, {"budget size:>5MB", 1},
                    {"modified:2024-06", 9}, {"modified:2023", 0}, {"path:projects ext:txt", 2}, {"type:folder", 1}, {"a.b", 1},
                    {"zzz", 0}, {"tape:selftest report", 1}}
                Using engine As New SearchEngine(store)
                    For i As Integer = 0 To cases.GetLength(0) - 1
                        Dim q As String = CStr(cases(i, 0))
                        Dim want As Integer = CInt(cases(i, 1))
                        Dim pq As ParsedQuery = ParsedQuery.Parse(q)
                        Dim res As SearchResult = engine.Run(pq, New SearchOptions(), CancellationToken.None)
                        Dim got As Integer = If(res Is Nothing OrElse res.ErrorText IsNot Nothing, -1, res.Rows.Count)
                        Dim ok As Boolean = got = want AndAlso pq.Errors.Count = 0
                        If Not ok Then failed += 1
                        sb.AppendLine(If(ok, "PASS  ", "FAIL  ") & q.PadRight(26) & " want " & want & ", got " & got &
                                      If(res IsNot Nothing AndAlso res.ErrorText IsNot Nothing, "  error: " & res.ErrorText, "") &
                                      If(pq.Errors.Count > 0, "  parse: " & pq.Errors(0), ""))
                    Next
                End Using
            End Using
        Finally
            For Each suffix As String In {"", "-wal", "-shm"}
                Try
                    File.Delete(db & suffix)
                Catch
                End Try
            Next
        End Try
        sb.AppendLine(If(failed = 0, "SELFTEST PASSED", "SELFTEST FAILED: " & failed & " case(s)"))
        Return If(failed = 0, 0, 2)
    End Function

    Private Function ImportAll(o As AppOptions, sb As StringBuilder) As Integer
        Dim folder As String = If(o.IndexFolder, Path.Combine(AppDir, "schema"))
        Dim total As Stopwatch = Stopwatch.StartNew()
        Using store As New CatalogStore(o.DbPath)
            Dim plan As ScanPlan = IndexScanner.Plan(store, folder)
            sb.AppendLine("Index folder: " & folder)
            sb.AppendLine("Plan: " & plan.NewTapes.Count & " new, " & plan.Updates.Count & " to update, " & plan.ReformattedBlank.Count & " reformatted blank (" & total.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) & " s)")
            For Each er As String In plan.Errors
                sb.AppendLine("  scan: " & er)
            Next
            Dim work As New List(Of Tuple(Of Long, IndexFileInfo))
            For Each n As IndexFileInfo In plan.NewTapes
                If o.TrackAll Then
                    work.Add(Tuple.Create(store.AddTape(n.Barcode, n.Serial, TapeInfo.StateTracked), n))
                Else
                    sb.AppendLine("  new (not tracked): " & n.Label & "  " & n.Path)
                End If
            Next
            For Each u As Tuple(Of TapeInfo, IndexFileInfo) In plan.Updates.Concat(plan.ReformattedBlank)
                work.Add(Tuple.Create(u.Item1.Id, u.Item2))
            Next
            Dim failed As Integer = 0
            For Each w As Tuple(Of Long, IndexFileInfo) In work
                Dim sw As Stopwatch = Stopwatch.StartNew()
                Try
                    Dim imp As IndexImporter = IndexImporter.Run(store, w.Item2, w.Item1, Nothing, CancellationToken.None)
                    sb.AppendLine(String.Format(CultureInfo.InvariantCulture, "  imported {0}: {1:N0} files, {2:N0} folders, {3} in {4:0.0} s  (gen {5}, {6}, index {7})",
                                  w.Item2.Label, imp.FileCount, imp.DirCount, FormatSize(imp.ByteCount), sw.Elapsed.TotalSeconds, imp.Generation, w.Item2.Format, FormatSize(w.Item2.FileSize)))
                Catch ex As Exception
                    failed += 1
                    store.SetTapeError(w.Item1, ex.Message)
                    sb.AppendLine("  FAILED " & w.Item2.Label & ": " & ex.Message)
                End Try
            Next
            Dim tt As Tuple(Of Long, Long, Long) = store.Totals()
            sb.AppendLine(String.Format(CultureInfo.InvariantCulture, "Catalog: {0} tapes, {1:N0} files, {2}; database {3}; total {4:0.0} s",
                          tt.Item1, tt.Item2, FormatSize(tt.Item3), FormatSize(New FileInfo(o.DbPath).Length), total.Elapsed.TotalSeconds))
            Return If(failed = 0, 0, 3)
        End Using
    End Function

    Private Function SearchOnce(o As AppOptions, sb As StringBuilder) As Integer
        Using store As New CatalogStore(o.DbPath)
            Dim q As ParsedQuery = ParsedQuery.Parse(o.Query)
            If q.Errors.Count > 0 Then
                sb.AppendLine("Not understood: " & q.Errors(0))
                Return 4
            End If
            Using engine As New SearchEngine(store)
                Dim opts As New SearchOptions With {.Kind = o.Kind}
                Dim res As SearchResult = Nothing
                Dim best As Double = Double.MaxValue
                For run As Integer = 1 To 3
                    res = engine.Run(q, opts, CancellationToken.None)
                    If res.ErrorText IsNot Nothing Then Exit For
                    best = Math.Min(best, res.Elapsed.TotalMilliseconds)
                Next
                If res.ErrorText IsNot Nothing Then
                    sb.AppendLine("Search failed: " & res.ErrorText)
                    Return 5
                End If
                Dim tapes As Dictionary(Of Long, TapeInfo) = store.GetTapes().ToDictionary(Function(t) t.Id)
                sb.AppendLine(String.Format(CultureInfo.InvariantCulture, "Query: {0}{1}Results: {2:N0}{3}; best of 3 runs {4:0.0} ms",
                              o.Query, Environment.NewLine, res.Rows.Count, If(res.Capped, "+ (capped)", ""), best))
                For Each r As EntryRow In res.Rows.Take(30)
                    Dim folder As String = store.FolderOf(r.ParentId)
                    sb.AppendLine("  " & tapes(r.TapeId).Barcode & ":" & If(folder = "\", "\", folder & "\") & r.Name & If(r.IsDir, "\", "") & "  " & FormatSize(r.Size))
                Next
                If o.RequestTo IsNot Nothing Then
                    ' test of the restore handoff: one request per tape for every result, into a test folder
                    Dim cache As New Dictionary(Of Long, String())
                    For Each g As IGrouping(Of Long, EntryRow) In res.Rows.GroupBy(Function(r) r.TapeId)
                        Dim items As List(Of KeyValuePair(Of Boolean, String())) = g.Select(Function(r) New KeyValuePair(Of Boolean, String())(r.IsDir, store.TapePathParts(r.ParentId, r.Name, cache))).
                            Where(Function(kv) kv.Value IsNot Nothing).ToList()
                        sb.AppendLine("Request: " & RestoreRequest.Write(tapes(g.Key), items, o.Dest, True, o.RequestTo) & " (" & items.Count.ToString(CultureInfo.InvariantCulture) & " items)")
                    Next
                End If
            End Using
        End Using
        Return 0
    End Function

End Module
