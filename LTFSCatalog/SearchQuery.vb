Imports System.Data.Common
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading

Friend Enum KindFilter
    All
    Files
    Folders
End Enum

Friend Enum SortColumn
    Relevance
    Name
    Size
    Modified
    Tape
    Folder
End Enum

''' <summary>One query word: its alternatives (a|b) as normalised keys, and whether it excludes.</summary>
Friend NotInheritable Class SearchTerm
    Public Alternatives As New List(Of String)
    Public Exclude As Boolean

    Public Shared Function IsWildcard(k As String) As Boolean
        Return k.IndexOf("*"c) >= 0 OrElse k.IndexOf("?"c) >= 0
    End Function

    ''' <summary>Can go to the trigram index: positive, no wildcard, every alternative 3+ characters.</summary>
    Public ReadOnly Property Indexable As Boolean
        Get
            If Exclude OrElse Alternatives.Count = 0 Then Return False
            Return Alternatives.All(Function(a) a.Length >= 3 AndAlso Not IsWildcard(a))
        End Get
    End Property
End Class

''' <summary>A parsed search: words plus field filters (ext:, size:, modified:, created:, tape:, path:, type:).</summary>
Friend NotInheritable Class ParsedQuery
    Public Terms As New List(Of SearchTerm)
    Public Exts As New List(Of String)
    Public SizeMin As Long?
    Public SizeMax As Long?
    Public MTimeFrom As Long?
    Public MTimeTo As Long?
    Public CTimeFrom As Long?
    Public CTimeTo As Long?
    Public TapeLike As String
    Public PathKeys As New List(Of String)
    Public Kind As KindFilter = KindFilter.All
    Public Errors As New List(Of String)

    Public ReadOnly Property IsEmpty As Boolean
        Get
            Return Terms.Count = 0 AndAlso Exts.Count = 0 AndAlso Not SizeMin.HasValue AndAlso Not SizeMax.HasValue AndAlso
                   Not MTimeFrom.HasValue AndAlso Not MTimeTo.HasValue AndAlso Not CTimeFrom.HasValue AndAlso Not CTimeTo.HasValue AndAlso
                   String.IsNullOrEmpty(TapeLike) AndAlso PathKeys.Count = 0 AndAlso Kind = KindFilter.All
        End Get
    End Property

    ''' <summary>First positive plain word (for ranking), or Nothing.</summary>
    Public ReadOnly Property RankKey As String
        Get
            For Each t As SearchTerm In Terms
                If Not t.Exclude AndAlso t.Alternatives.Count = 1 AndAlso Not SearchTerm.IsWildcard(t.Alternatives(0)) Then Return t.Alternatives(0)
            Next
            Return Nothing
        End Get
    End Property

    ' ---- parsing -----------------------------------------------------------------------------
    Public Shared Function Parse(text As String) As ParsedQuery
        Dim q As New ParsedQuery
        For Each raw As String In Tokenize(If(text, ""))
            Dim tok As String = raw
            Dim exclude As Boolean = False
            If tok.Length > 1 AndAlso tok(0) = "-"c Then
                exclude = True
                tok = tok.Substring(1)
            End If
            Dim fm As Match = Regex.Match(tok, "^(?<f>[A-Za-z]+):(?<v>.*)$")
            If fm.Success AndAlso q.ApplyField(fm.Groups("f").Value.ToLowerInvariant(), Unquote(fm.Groups("v").Value), exclude) Then Continue For
            Dim term As New SearchTerm With {.Exclude = exclude}
            For Each alt As String In SplitAlternatives(tok)
                Dim k As String = SearchKey(Unquote(alt))
                If k.Length > 0 Then term.Alternatives.Add(k)
            Next
            If term.Alternatives.Count > 0 Then q.Terms.Add(term)
        Next
        Return q
    End Function

    ''' <summary>Splits on white space outside double quotes; quotes stay in the token.</summary>
    Private Shared Function Tokenize(s As String) As List(Of String)
        Dim list As New List(Of String)
        Dim sb As New StringBuilder()
        Dim inQuote As Boolean = False
        For Each ch As Char In s
            If ch = """"c Then
                inQuote = Not inQuote
                sb.Append(ch)
            ElseIf Char.IsWhiteSpace(ch) AndAlso Not inQuote Then
                If sb.Length > 0 Then list.Add(sb.ToString())
                sb.Clear()
            Else
                sb.Append(ch)
            End If
        Next
        If sb.Length > 0 Then list.Add(sb.ToString())
        Return list
    End Function

    Private Shared Function SplitAlternatives(tok As String) As List(Of String)
        Dim list As New List(Of String)
        Dim sb As New StringBuilder()
        Dim inQuote As Boolean = False
        For Each ch As Char In tok
            If ch = """"c Then inQuote = Not inQuote
            If ch = "|"c AndAlso Not inQuote Then
                list.Add(sb.ToString())
                sb.Clear()
            Else
                sb.Append(ch)
            End If
        Next
        list.Add(sb.ToString())
        Return list
    End Function

    Private Shared Function Unquote(s As String) As String
        Return s.Replace("""", "")
    End Function

    Private Function ApplyField(f As String, v As String, exclude As Boolean) As Boolean
        Select Case f
            Case "ext", "extension"
                For Each e As String In v.Split(New Char() {","c, ";"c, "|"c}, StringSplitOptions.RemoveEmptyEntries)
                    Dim x As String = e.Trim().TrimStart("*"c).TrimStart("."c).ToLowerInvariant()
                    If x.Length > 0 Then Exts.Add(x)
                Next
                Return True
            Case "size"
                If Not ParseSizeFilter(v) Then Errors.Add("size:" & v & " - try size:>1GB, size:<500MB or size:10MB..2GB")
                Return True
            Case "modified", "date", "dm", "mtime", "datemodified"
                Dim a As Long?, b As Long?
                If ParseDateFilter(v, a, b) Then
                    MTimeFrom = a
                    MTimeTo = b
                Else
                    Errors.Add("modified:" & v & " - try modified:2024, modified:2024-06, modified:>2024-06-01, modified:lastmonth")
                End If
                Return True
            Case "created", "dc", "ctime", "datecreated"
                Dim a As Long?, b As Long?
                If ParseDateFilter(v, a, b) Then
                    CTimeFrom = a
                    CTimeTo = b
                Else
                    Errors.Add("created:" & v & " - try created:2023 or created:2023-01..2023-06")
                End If
                Return True
            Case "tape", "barcode"
                TapeLike = v.Trim()
                Return True
            Case "path", "folder", "in"
                Dim k As String = SearchKey(v.Replace("/"c, "\"c).Trim())
                If k.Length > 0 Then PathKeys.Add(k)
                Return True
            Case "type", "kind"
                Select Case v.Trim().ToLowerInvariant()
                    Case "file", "files" : Kind = KindFilter.Files
                    Case "folder", "folders", "dir", "dirs", "directory" : Kind = KindFilter.Folders
                    Case Else : Errors.Add("type:" & v & " - use type:file or type:folder")
                End Select
                Return True
        End Select
        Return False
    End Function

    Private Function ParseSizeFilter(v As String) As Boolean
        v = v.Trim()
        Dim dots As Integer = v.IndexOf("..", StringComparison.Ordinal)
        If dots > 0 Then
            Dim a As Long?, b As Long?
            a = SizeValue(v.Substring(0, dots))
            b = SizeValue(v.Substring(dots + 2))
            If Not a.HasValue OrElse Not b.HasValue Then Return False
            SizeMin = a
            SizeMax = b
            Return True
        End If
        Dim m As Match = Regex.Match(v, "^(?<op>>=|<=|>|<|=)?(?<n>.+)$")
        If Not m.Success Then Return False
        Dim n As Long? = SizeValue(m.Groups("n").Value)
        If Not n.HasValue Then Return False
        Select Case m.Groups("op").Value
            Case ">" : SizeMin = n.Value + 1
            Case ">=", "" : SizeMin = n.Value
            Case "<" : SizeMax = n.Value - 1
            Case "<=" : SizeMax = n.Value
            Case "=" : SizeMin = n.Value : SizeMax = n.Value
        End Select
        Return True
    End Function

    ''' <summary>"1.5GB", "500k", "123" (bytes) - binary units, as LTFSCopyGUI shows sizes.</summary>
    Public Shared Function SizeValue(s As String) As Long?
        Dim m As Match = Regex.Match(s.Trim(), "^(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[a-zA-Z]*)$")
        If Not m.Success Then Return Nothing
        Dim n As Double = Double.Parse(m.Groups("n").Value.Replace(","c, "."c), CultureInfo.InvariantCulture)
        Dim mult As Double
        Select Case m.Groups("u").Value.ToLowerInvariant()
            Case "", "b", "byte", "bytes" : mult = 1
            Case "k", "kb", "kib" : mult = 1024.0
            Case "m", "mb", "mib" : mult = 1024.0 ^ 2
            Case "g", "gb", "gib" : mult = 1024.0 ^ 3
            Case "t", "tb", "tib" : mult = 1024.0 ^ 4
            Case Else : Return Nothing
        End Select
        Return CLng(Math.Round(n * mult))
    End Function

    ''' <summary>Date filters in local time; result is [from, to) in Unix seconds (either side may be open).</summary>
    Private Shared Function ParseDateFilter(v As String, ByRef fromSec As Long?, ByRef toSec As Long?) As Boolean
        v = v.Trim().ToLowerInvariant()
        Dim a As DateTime, b As DateTime, a2 As DateTime, b2 As DateTime
        Dim dots As Integer = v.IndexOf("..", StringComparison.Ordinal)
        If dots > 0 Then
            If Not Period(v.Substring(0, dots), a, b) OrElse Not Period(v.Substring(dots + 2), a2, b2) Then Return False
            fromSec = ToUnix(a)
            toSec = ToUnix(b2)
            Return True
        End If
        Dim m As Match = Regex.Match(v, "^(?<op>>=|<=|>|<|=)?(?<d>.+)$")
        If Not m.Success OrElse Not Period(m.Groups("d").Value, a, b) Then Return False
        Select Case m.Groups("op").Value
            Case "", "=" : fromSec = ToUnix(a) : toSec = ToUnix(b)
            Case ">" : fromSec = ToUnix(b) : toSec = Nothing
            Case ">=" : fromSec = ToUnix(a) : toSec = Nothing
            Case "<" : fromSec = Nothing : toSec = ToUnix(a)
            Case "<=" : fromSec = Nothing : toSec = ToUnix(b)
        End Select
        Return True
    End Function

    ''' <summary>A named period or a year / month / day as [start, end) in local time.</summary>
    Private Shared Function Period(s As String, ByRef a As DateTime, ByRef b As DateTime) As Boolean
        Dim today As DateTime = DateTime.Today
        Dim monday As DateTime = today.AddDays(-((CInt(today.DayOfWeek) + 6) Mod 7))
        Select Case s
            Case "today" : a = today : b = today.AddDays(1) : Return True
            Case "yesterday" : a = today.AddDays(-1) : b = today : Return True
            Case "thisweek" : a = monday : b = monday.AddDays(7) : Return True
            Case "lastweek" : a = monday.AddDays(-7) : b = monday : Return True
            Case "thismonth" : a = New DateTime(today.Year, today.Month, 1) : b = a.AddMonths(1) : Return True
            Case "lastmonth" : b = New DateTime(today.Year, today.Month, 1) : a = b.AddMonths(-1) : Return True
            Case "thisyear" : a = New DateTime(today.Year, 1, 1) : b = a.AddYears(1) : Return True
            Case "lastyear" : b = New DateTime(today.Year, 1, 1) : a = b.AddYears(-1) : Return True
        End Select
        Dim m As Match = Regex.Match(s, "^(?<y>\d{4})(?:[-./](?<m>\d{1,2})(?:[-./](?<d>\d{1,2}))?)?$")
        If Not m.Success Then Return False
        Try
            Dim y As Integer = Integer.Parse(m.Groups("y").Value, CultureInfo.InvariantCulture)
            If Not m.Groups("m").Success Then
                a = New DateTime(y, 1, 1) : b = a.AddYears(1)
            ElseIf Not m.Groups("d").Success Then
                a = New DateTime(y, Integer.Parse(m.Groups("m").Value, CultureInfo.InvariantCulture), 1) : b = a.AddMonths(1)
            Else
                a = New DateTime(y, Integer.Parse(m.Groups("m").Value, CultureInfo.InvariantCulture), Integer.Parse(m.Groups("d").Value, CultureInfo.InvariantCulture)) : b = a.AddDays(1)
            End If
            Return True
        Catch ex As ArgumentOutOfRangeException
            Return False
        End Try
    End Function

End Class

Friend NotInheritable Class SearchOptions
    Public TapeId As Long?
    Public Kind As KindFilter = KindFilter.All
    Public MatchPath As Boolean
    Public Sort As SortColumn = SortColumn.Relevance
    Public Descending As Boolean
End Class

Friend NotInheritable Class SearchResult
    Public Rows As New List(Of EntryRow)
    Public Capped As Boolean
    Public Elapsed As TimeSpan
    Public ErrorText As String
End Class

''' <summary>Runs searches on its own connection; a new search cancels the one still running.</summary>
Friend NotInheritable Class SearchEngine
    Implements IDisposable

    ''' <summary>Most rows a search returns (the self-test lowers it).</summary>
    Public Shared Property Cap As Integer = 50000
    Private ReadOnly _store As CatalogStore
    Private ReadOnly _conn As DbConnection
    Private ReadOnly _gate As New SemaphoreSlim(1, 1)
    Private ReadOnly _cmdLock As New Object()
    Private _running As DbCommand

    Public Sub New(store As CatalogStore)
        _store = store
        _conn = store.OpenConnection()
    End Sub

    Public Sub CancelRunning()
        SyncLock _cmdLock
            Try
                If _running IsNot Nothing Then _running.Cancel()
            Catch
            End Try
        End SyncLock
    End Sub

    ''' <summary>Nothing when cancelled.</summary>
    ''' <remarks>Scales to millions of entries: the first query has no ORDER BY, so it stops at Cap + 1 rows, and a
    ''' complete result is sorted in memory. Only a capped result needs more: the exact and prefix matches of the
    ''' first word (a range on ix_entries_k) are merged in front, or an explicit sort column is re-queried in order.</remarks>
    Public Function Run(q As ParsedQuery, opts As SearchOptions, ct As CancellationToken) As SearchResult
        _gate.Wait()
        Try
            If ct.IsCancellationRequested Then Return Nothing
            Dim res As New SearchResult
            Dim sw As Stopwatch = Stopwatch.StartNew()
            Dim fts As Boolean = _store.FtsAvailable
            Dim rows As List(Of EntryRow) = Fetch(q, opts, fts, Shape.Unsorted, ct)
            If rows Is Nothing Then Return Nothing
            If rows.Count <= Cap Then
                SortInMemory(rows, q, opts)
            ElseIf opts.Sort = SortColumn.Relevance Then
                res.Capped = True
                If q.RankKey IsNot Nothing Then
                    Dim first As List(Of EntryRow) = Fetch(q, opts, fts, Shape.Prefix, ct)
                    If first Is Nothing Then Return Nothing
                    Dim seen As New HashSet(Of Long)(first.Select(Function(r) r.Id))
                    first.AddRange(rows.Where(Function(r) Not seen.Contains(r.Id)))
                    rows = first
                End If
                SortInMemory(rows, q, opts)
            Else
                res.Capped = True
                rows = Fetch(q, opts, fts, Shape.Sorted, ct)
                If rows Is Nothing Then Return Nothing
            End If
            If rows.Count > Cap Then rows.RemoveRange(Cap, rows.Count - Cap)
            res.Rows = rows
            res.Elapsed = sw.Elapsed
            Return res
        Catch ex As Exception When ct.IsCancellationRequested
            Return Nothing
        Catch ex As Exception
            LogError("Search failed", ex)
            Return New SearchResult With {.ErrorText = ex.Message}
        Finally
            _gate.Release()
        End Try
    End Function

    Private Enum Shape
        Unsorted  ' any Cap + 1 matches
        Prefix    ' matches whose key starts with the first word, in key order
        Sorted    ' the first Cap + 1 matches in the chosen sort order
    End Enum

    ''' <summary>Up to Cap + 1 rows, or Nothing when cancelled.</summary>
    Private Function Fetch(q As ParsedQuery, opts As SearchOptions, fts As Boolean, sh As Shape, ct As CancellationToken) As List(Of EntryRow)
        Dim rows As New List(Of EntryRow)
        Using cmd As DbCommand = _conn.CreateCommand()
            Build(cmd, q, opts, fts, sh)
            SyncLock _cmdLock
                _running = cmd
            End SyncLock
            Try
                If ct.IsCancellationRequested Then Return Nothing
                Using r As DbDataReader = cmd.ExecuteReader()
                    While r.Read()
                        rows.Add(New EntryRow With {.Id = CatalogStore.L(r, 0), .TapeId = CatalogStore.L(r, 1), .ParentId = CatalogStore.LN(r, 2),
                            .IsDir = CatalogStore.L(r, 3) <> 0, .Name = CatalogStore.S(r, 4), .Size = CatalogStore.L(r, 5), .Items = CatalogStore.L(r, 6),
                            .MTime = CatalogStore.LN(r, 7), .CTime = CatalogStore.LN(r, 8), .Key = CatalogStore.S(r, 9)})
                        If (rows.Count And 4095) = 0 AndAlso ct.IsCancellationRequested Then Return Nothing
                    End While
                End Using
            Finally
                SyncLock _cmdLock
                    _running = Nothing
                End SyncLock
            End Try
        End Using
        Return If(ct.IsCancellationRequested, Nothing, rows)
    End Function

    ''' <summary>Relevance: exact name, then names starting with the first word, then the rest; folders first; then by
    ''' key. Other columns: by key (the window applies its own column sort on top).</summary>
    Private Shared Sub SortInMemory(rows As List(Of EntryRow), q As ParsedQuery, opts As SearchOptions)
        If opts.Sort = SortColumn.Relevance Then
            Dim rk As String = q.RankKey
            Dim rank As Func(Of EntryRow, Integer) =
                Function(r As EntryRow) As Integer
                    If rk Is Nothing Then Return 0
                    If String.Equals(r.Key, rk, StringComparison.Ordinal) Then Return 0
                    Return If(r.Key.StartsWith(rk, StringComparison.Ordinal), 1, 2)
                End Function
            Dim ranked As List(Of KeyValuePair(Of Integer, EntryRow)) = rows.Select(Function(r) New KeyValuePair(Of Integer, EntryRow)(rank(r), r)).ToList()
            ranked.Sort(Function(a, b)
                            If a.Key <> b.Key Then Return a.Key.CompareTo(b.Key)
                            If a.Value.IsDir <> b.Value.IsDir Then Return If(a.Value.IsDir, -1, 1)
                            Return String.CompareOrdinal(a.Value.Key, b.Value.Key)
                        End Function)
            rows.Clear()
            rows.AddRange(ranked.Select(Function(kv) kv.Value))
        Else
            Dim sign As Integer = If(opts.Sort = SortColumn.Name AndAlso opts.Descending, -1, 1)
            rows.Sort(Function(a, b) sign * String.CompareOrdinal(a.Key, b.Key))
        End If
    End Sub

    ''' <summary>GLOB pattern text: literal [ * ? escaped unless wildcards are wanted.</summary>
    Private Shared Function GlobEscape(k As String, keepWildcards As Boolean) As String
        Dim sb As New StringBuilder(k.Length + 8)
        For Each ch As Char In k
            Select Case ch
                Case "["c : sb.Append("[[]")
                Case "*"c : sb.Append(If(keepWildcards, "*", "[*]"))
                Case "?"c : sb.Append(If(keepWildcards, "?", "[?]"))
                Case Else : sb.Append(ch)
            End Select
        Next
        Return sb.ToString()
    End Function

    ''' <summary>Name pattern for a word: whole-name match with wildcards, otherwise "contains".</summary>
    Private Shared Function NamePattern(k As String) As String
        If SearchTerm.IsWildcard(k) Then Return GlobEscape(k, True)
        Return "*" & GlobEscape(k, False) & "*"
    End Function

    ''' <summary>Longest run of characters that are not wildcards (the trigram index needs 3).</summary>
    Private Shared Function LiteralRun(k As String) As Integer
        Dim best As Integer = 0, cur As Integer = 0
        For Each ch As Char In k
            If ch = "*"c OrElse ch = "?"c OrElse ch = "["c OrElse ch = "]"c Then cur = 0 Else cur += 1
            best = Math.Max(best, cur)
        Next
        Return best
    End Function

    Private Shared Function FtsPhrase(k As String) As String
        Return """" & k.Replace("""", """""") & """"
    End Function

    ''' <summary>The highest character: key &lt; prefix + MaxChar for every key that starts with prefix.</summary>
    Private Shared ReadOnly MaxChar As String = Char.ConvertFromUtf32(&H10FFFF)

    Private Shared Sub Build(cmd As DbCommand, q As ParsedQuery, o As SearchOptions, fts As Boolean, sh As Shape)
        Dim n As Integer = 0
        Dim P As Func(Of Object, String) =
            Function(v As Object) As String
                Dim name As String = "@q" & n.ToString(CultureInfo.InvariantCulture)
                n += 1
                Dim prm As DbParameter = cmd.CreateParameter()
                prm.ParameterName = name
                prm.Value = If(v, DBNull.Value)
                cmd.Parameters.Add(prm)
                Return name
            End Function

        ' Words: indexed MATCH for plain positive words (unless names must also match the folder path); a positive
        ' wildcard pattern with 3+ literal characters lets FTS5 use the trigram index for GLOB; everything else
        ' (short words, exclusions) tests e.k on the rows the other conditions select.
        Dim where As New StringBuilder()
        Dim matchParts As New List(Of String)
        Dim useFts As Boolean = False
        For Each t As SearchTerm In q.Terms
            If fts AndAlso Not o.MatchPath AndAlso t.Indexable Then
                matchParts.Add(If(t.Alternatives.Count = 1, FtsPhrase(t.Alternatives(0)),
                                  "(" & String.Join(" OR ", t.Alternatives.Select(Function(a) FtsPhrase(a))) & ")"))
                useFts = True
                Continue For
            End If
            If fts AndAlso Not t.Exclude AndAlso t.Alternatives.Count = 1 AndAlso SearchTerm.IsWildcard(t.Alternatives(0)) AndAlso
               LiteralRun(t.Alternatives(0)) >= 3 Then
                where.Append(" AND entry_fts.k GLOB ").Append(P(NamePattern(t.Alternatives(0))))
                useFts = True
                Continue For
            End If
            Dim alts As New List(Of String)
            For Each a As String In t.Alternatives
                Dim cond As String = "e.k GLOB " & P(NamePattern(a))
                If o.MatchPath AndAlso Not t.Exclude AndAlso Not SearchTerm.IsWildcard(a) Then
                    cond = "(" & cond & " OR e.parent_id IN (SELECT d.id FROM entries d WHERE d.is_dir = 1 AND d.pkey GLOB " & P("*" & GlobEscape(a, False) & "*") & "))"
                End If
                alts.Add(cond)
            Next
            Dim joined As String = If(alts.Count = 1, alts(0), "(" & String.Join(" OR ", alts) & ")")
            where.Append(If(t.Exclude, " AND NOT ", " AND ")).Append(joined)
        Next
        If matchParts.Count > 0 Then where.Append(" AND entry_fts MATCH ").Append(P(String.Join(" AND ", matchParts)))

        If q.Exts.Count > 0 Then where.Append(" AND e.ext IN (").Append(String.Join(", ", q.Exts.Select(Function(x) P(x)))).Append(")")
        If q.SizeMin.HasValue Then where.Append(" AND e.size >= ").Append(P(q.SizeMin.Value))
        If q.SizeMax.HasValue Then where.Append(" AND e.size <= ").Append(P(q.SizeMax.Value))
        If q.MTimeFrom.HasValue Then where.Append(" AND e.mtime >= ").Append(P(q.MTimeFrom.Value))
        If q.MTimeTo.HasValue Then where.Append(" AND e.mtime < ").Append(P(q.MTimeTo.Value))
        If q.CTimeFrom.HasValue Then where.Append(" AND e.ctime >= ").Append(P(q.CTimeFrom.Value))
        If q.CTimeTo.HasValue Then where.Append(" AND e.ctime < ").Append(P(q.CTimeTo.Value))
        ' Tapes as a subquery, and "+" keeps the planner off ix_entries_tape_parent: with a join it walked every
        ' tracked tape's entries even when a size or date index would find a handful of rows.
        where.Append(" AND +e.tape_id IN (SELECT id FROM tapes WHERE state = 1 AND hidden = 0")
        If Not String.IsNullOrEmpty(q.TapeLike) Then
            Dim lit As String = q.TapeLike.Replace("\", "\\").Replace("%", "\%").Replace("_", "\_")
            where.Append(" AND barcode LIKE ").Append(P("%" & lit & "%")).Append(" ESCAPE '\'")
        End If
        where.Append(")")
        If o.TapeId.HasValue Then where.Append(" AND +e.tape_id = ").Append(P(o.TapeId.Value))
        For Each pk As String In q.PathKeys
            where.Append(" AND e.parent_id IN (SELECT d.id FROM entries d WHERE d.is_dir = 1 AND d.pkey GLOB ").Append(P("*" & GlobEscape(pk, False) & "*")).Append(")")
        Next
        If o.Kind = KindFilter.Files OrElse q.Kind = KindFilter.Files Then where.Append(" AND e.is_dir = 0")
        If o.Kind = KindFilter.Folders OrElse q.Kind = KindFilter.Folders Then where.Append(" AND e.is_dir = 1")

        Dim sql As New StringBuilder("SELECT e.id, e.tape_id, e.parent_id, e.is_dir, e.name, e.size, e.items, e.mtime, e.ctime, e.k ")
        sql.Append(If(useFts, "FROM entry_fts JOIN entries e ON e.id = entry_fts.rowid ", "FROM entries e "))
        sql.Append("WHERE 1").Append(where.ToString())
        Dim dir As String = If(o.Descending, " DESC", "")
        Select Case sh
            Case Shape.Prefix
                Dim rk As String = q.RankKey
                sql.Append(" AND e.k >= ").Append(P(rk)).Append(" AND e.k < ").Append(P(rk & MaxChar)).Append(" ORDER BY e.k")
            Case Shape.Sorted
                Select Case o.Sort
                    Case SortColumn.Size : sql.Append(" ORDER BY e.size").Append(dir).Append(", e.k")
                    Case SortColumn.Modified : sql.Append(" ORDER BY e.mtime").Append(dir).Append(", e.k")
                    Case SortColumn.Tape : sql.Append(" ORDER BY (SELECT barcode FROM tapes WHERE id = e.tape_id) COLLATE NOCASE").Append(dir).Append(", e.k")
                    Case SortColumn.Folder : sql.Append(" ORDER BY (SELECT d.path FROM entries d WHERE d.id = e.parent_id) COLLATE NOCASE").Append(dir).Append(", e.k")
                    Case Else : sql.Append(" ORDER BY e.k").Append(dir)
                End Select
        End Select
        sql.Append(" LIMIT ").Append(P(Cap + 1))
        cmd.CommandText = sql.ToString()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        CancelRunning()
        _conn.Dispose()
    End Sub

End Class
