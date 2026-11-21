Imports System.IO
Imports System.Runtime.ExceptionServices
Imports System.Runtime.InteropServices
Imports System.Threading
Imports Microsoft.Win32.SafeHandles
Imports Serilog

''' <summary>
''' 2.019: one entry of a folder listing made for Add. It holds what a FileInfo or DirectoryInfo from
''' DirectoryInfo.GetFileSystemInfos holds (the same find data), without .NET's path checks.
''' </summary>
Friend NotInheritable Class ListedEntry
    ''' <summary>The listed folder as the add names it (the original server name, never an alias).</summary>
    Public ReadOnly ParentPath As String
    Public ReadOnly Name As String
    Public ReadOnly Attributes As FileAttributes
    Public ReadOnly Length As Long
    Private ReadOnly _creation As Long, _access As Long, _write As Long   ' FILETIMEs as listed
    Private _fullName As String

    Public Sub New(parentPath As String, name As String, attributes As FileAttributes, length As Long,
                   creationFileTime As Long, lastAccessFileTime As Long, lastWriteFileTime As Long)
        Me.ParentPath = parentPath
        Me.Name = name
        Me.Attributes = attributes
        Me.Length = length
        _creation = creationFileTime
        _access = lastAccessFileTime
        _write = lastWriteFileTime
    End Sub

    ' Converted when read, as FileSystemInfo does: a time out of range throws here, not when the folder is listed, so
    ' callers fall back exactly as they do for a FileInfo.
    Public ReadOnly Property CreationTimeUtc As DateTime
        Get
            Return DateTime.FromFileTimeUtc(_creation)
        End Get
    End Property
    Public ReadOnly Property LastAccessTimeUtc As DateTime
        Get
            Return DateTime.FromFileTimeUtc(_access)
        End Get
    End Property
    Public ReadOnly Property LastWriteTimeUtc As DateTime
        Get
            Return DateTime.FromFileTimeUtc(_write)
        End Get
    End Property

    Public ReadOnly Property IsDirectory As Boolean
        Get
            Return (Attributes And FileAttributes.Directory) <> 0
        End Get
    End Property

    ''' <summary>The path as FileInfo.FullName gives it for a listed entry: folder and name joined as Path.Combine does.</summary>
    Public ReadOnly Property FullName As String
        Get
            If _fullName Is Nothing Then _fullName = AddListing.JoinPath(ParentPath, Name)
            Return _fullName
        End Get
    End Property
End Class

''' <summary>
''' 2.019: folder listings for Add (LTFSWriter.AddScannedDirectory).
''' (1) Each folder is listed with FindFirstFileExW instead of DirectoryInfo.GetFileSystemInfos. .NET Framework builds a
''' path for every listed name and throws ArgumentException ("Illegal characters in path") at a name with a control
''' character or " &lt; &gt; | (a Mac's "Icon\r" on a NAS), which ended the add of the whole top folder partway. Such names are
''' now left out and reported (FolderListing.Skipped); everything else is listed. The write could not open them anyway.
''' (2) On a network share the folders the add reaches next are listed ahead by several threads, over the server's other
''' names too (SourceRoutes, setting LTFSWriter_SourceServerAliases): Samba answers one request at a time per connection,
''' and Windows opens one connection per server name. The add itself still walks the folders one by one in the same
''' order, so files are queued in the same order as before. DS3615xs, 0P1_lenovo_backup (108,633 files, 16,901
''' folders, cached on the NAS): one folder at a time 17.4 s, 8 threads over one name 6.1 s, over three names 2.8 s.
''' </summary>
Friend NotInheritable Class AddListing
    Implements IDisposable

    Friend NotInheritable Class FolderListing
        Public ReadOnly Path As String
        Public ReadOnly Entries As List(Of ListedEntry)
        ''' <summary>Names left out because .NET paths cannot hold them; Nothing when there are none.</summary>
        Public ReadOnly Skipped As List(Of ListedEntry)
        Public Sub New(path As String, entries As List(Of ListedEntry), skipped As List(Of ListedEntry))
            Me.Path = path
            Me.Entries = entries
            Me.Skipped = skipped
        End Sub
    End Class

    Private NotInheritable Class Slot
        Public ReadOnly Folder As ListedEntry
        Public ReadOnly Key As Integer()   ' position in the add's walk: entry indexes from the top folder down
        Public State As Integer            ' 0 queued, 1 being listed, 2 listed
        Public Result As FolderListing
        Public [Error] As Exception
        Public Sub New(folder As ListedEntry, key As Integer())
            Me.Folder = folder
            Me.Key = key
        End Sub
    End Class

    ''' <summary>Walk order: a folder before its subfolders, subfolders in listing order (what the add does).</summary>
    Private NotInheritable Class WalkOrder
        Implements IComparer(Of Slot)
        Public Shared ReadOnly Instance As New WalkOrder()
        Public Function Compare(a As Slot, b As Slot) As Integer Implements IComparer(Of Slot).Compare
            Dim n As Integer = Math.Min(a.Key.Length, b.Key.Length)
            For i As Integer = 0 To n - 1
                If a.Key(i) <> b.Key(i) Then Return a.Key(i).CompareTo(b.Key(i))
            Next
            Return a.Key.Length.CompareTo(b.Key.Length)
        End Function
    End Class

    Private Const Queued As Integer = 0, Listing As Integer = 1, Listed As Integer = 2
    ''' <summary>Entries listed ahead and not yet taken by the add (about 150 bytes each).</summary>
    Private Const MaxAheadEntries As Integer = 200000

    Private ReadOnly _rootPath As String
    Private ReadOnly _threads As Integer
    Private ReadOnly _routes As SourceRoutes
    Private ReadOnly _descend As Func(Of ListedEntry, Boolean)
    Private ReadOnly _lock As New Object()
    Private ReadOnly _queue As New SortedSet(Of Slot)(WalkOrder.Instance)
    Private ReadOnly _slots As New Dictionary(Of ListedEntry, Slot)()   ' by reference
    Private ReadOnly _namesReady As Lazy(Of Integer)
    Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
    Private _ahead As Integer
    Private _stopping As Boolean
    Private _folders As Integer, _entries As Long, _byAdd As Integer, _retried As Integer
    Private _waitTicks As Long, _ownTicks As Long   ' the add's time waiting for a listing / listing itself (Stopwatch ticks)

    Private Shared ReadOnly InvalidNameChars As Char() = Path.GetInvalidPathChars()

    ''' <param name="rootPath">The top folder as the add names it (DirectoryInfo.FullName).</param>
    ''' <param name="threads">Folders listed at once on a network share (LTFSWriter_AddListThreads); 1 = the add lists
    ''' each folder itself when it gets there, as before. Local folders are always listed that way.</param>
    ''' <param name="descend">Whether the add will go into a listed folder (False for a skipped symlink).</param>
    Public Sub New(rootPath As String, threads As Integer, aliases As String, descend As Func(Of ListedEntry, Boolean))
        _rootPath = rootPath
        _descend = descend
        _routes = New SourceRoutes(If(threads > 1, aliases, "")) ' the names it finds are kept for 10 minutes
        Dim server As String = Nothing, share As String = Nothing, rest As String = Nothing
        _threads = If(_routes.SplitNetworkPath(rootPath, server, share, rest), Math.Max(1, Math.Min(16, threads)), 1)
        ' The server's other names are found (a DNS lookup, then one probe of the share per name, the first connection
        ' to each name: about a second) on a thread of their own; until then every worker lists under the first name.
        _namesReady = New Lazy(Of Integer)(Function()
                                               _routes.Route(_rootPath, 1)
                                               Return 0
                                           End Function, LazyThreadSafetyMode.ExecutionAndPublication)
    End Sub

    ''' <summary>Lists the top folder (here, by the caller's thread) and starts listing its subfolders ahead.</summary>
    Public Function ListRoot() As FolderListing
        Dim root As New ListedEntry(Nothing, Nothing, FileAttributes.Directory, 0, 0, 0, 0)
        Dim s As New Slot(root, Array.Empty(Of Integer)()) With {.State = Listing}
        Dim ownStart As Long = Stopwatch.GetTimestamp()
        Try
            s.Result = ListFolder(_rootPath, _rootPath)
        Catch ex As Exception
            s.Error = ex
        End Try
        Complete(s)
        Threading.Interlocked.Add(_ownTicks, Stopwatch.GetTimestamp() - ownStart)
        If s.Error IsNot Nothing Then ExceptionDispatchInfo.Capture(s.Error).Throw()
        SyncLock _lock
            _ahead -= s.Result.Entries.Count
            _byAdd += 1
        End SyncLock
        If _threads > 1 Then
            ThreadPool.QueueUserWorkItem(Sub(state)
                                             Try
                                                 Dim ignored As Integer = _namesReady.Value
                                             Catch
                                             End Try
                                         End Sub)
        End If
        For i As Integer = 1 To If(_threads > 1, _threads, 0)
            Dim index As Integer = i
            Dim t As New Thread(Sub() WorkerLoop(index)) With {.IsBackground = True, .Name = "Add listing " & index}
            t.Start()
        Next
        Return s.Result
    End Function

    ''' <summary>
    ''' The listing of a subfolder the add has reached: taken when listed ahead, waited for when being listed, else
    ''' listed here. Throws what listing it threw (IOException, UnauthorizedAccessException ...).
    ''' </summary>
    Public Function Take(folder As ListedEntry) As FolderListing
        Dim s As Slot = Nothing
        Dim listHere As Boolean = False
        SyncLock _lock
            If Not _slots.TryGetValue(folder, s) Then
                s = Nothing
            ElseIf s.State = Queued Then
                _queue.Remove(s)
                s.State = Listing
                listHere = True
            ElseIf s.State <> Listed Then
                Dim waitStart As Long = Stopwatch.GetTimestamp()
                While s.State <> Listed
                    Monitor.Wait(_lock)
                End While
                _waitTicks += Stopwatch.GetTimestamp() - waitStart
            End If
        End SyncLock
        If s Is Nothing Then Return ListFolder(folder.FullName, folder.FullName) ' not registered: list it plainly
        If listHere Then
            Dim ownStart As Long = Stopwatch.GetTimestamp()
            Try
                s.Result = ListFolder(folder.FullName, folder.FullName)
            Catch ex As Exception
                s.Error = ex
            End Try
            Complete(s)
            Threading.Interlocked.Add(_ownTicks, Stopwatch.GetTimestamp() - ownStart)
        End If
        SyncLock _lock
            _slots.Remove(folder)
            If s.Result IsNot Nothing Then _ahead -= s.Result.Entries.Count
            If listHere Then _byAdd += 1
            Monitor.PulseAll(_lock)
        End SyncLock
        If s.Error IsNot Nothing Then ExceptionDispatchInfo.Capture(s.Error).Throw()
        Return s.Result
    End Function

    ''' <summary>Marks a slot listed and queues its subfolders at their place in the walk.</summary>
    Private Sub Complete(s As Slot)
        SyncLock _lock
            If _stopping Then
                s.State = Listed
                Monitor.PulseAll(_lock)
                Return
            End If
            Try
                If s.Result IsNot Nothing Then
                    _folders += 1
                    _entries += s.Result.Entries.Count
                    _ahead += s.Result.Entries.Count
                    Dim entries As List(Of ListedEntry) = s.Result.Entries
                    For i As Integer = 0 To entries.Count - 1
                        Dim e As ListedEntry = entries(i)
                        If Not e.IsDirectory OrElse Not _descend(e) Then Continue For
                        Dim key(s.Key.Length) As Integer
                        Array.Copy(s.Key, key, s.Key.Length)
                        key(s.Key.Length) = i
                        Dim child As New Slot(e, key)
                        _slots(e) = child
                        _queue.Add(child)
                    Next
                End If
            Catch ex As Exception
                If s.Error Is Nothing Then s.Error = ex
            End Try
            s.State = Listed
            Monitor.PulseAll(_lock)
        End SyncLock
    End Sub

    Private Sub WorkerLoop(index As Integer)
        While True
            Dim s As Slot
            SyncLock _lock
                While Not _stopping AndAlso (_queue.Count = 0 OrElse _ahead >= MaxAheadEntries)
                    Monitor.Wait(_lock)
                End While
                If _stopping Then Return
                s = _queue.Min
                _queue.Remove(s)
                s.State = Listing
            End SyncLock
            Dim path As String = s.Folder.FullName
            Dim via As String = If(_namesReady.IsValueCreated, _routes.Route(path, index), path)
            Try
                s.Result = ListFolder(path, via)
            Catch ex As Exception When Not ReferenceEquals(via, path)
                ' Through another name: try the name the add uses before reporting the folder unreadable.
                Threading.Interlocked.Increment(_retried)
                Try
                    s.Result = ListFolder(path, path)
                Catch ex2 As Exception
                    s.Error = ex2
                End Try
            Catch ex As Exception
                s.Error = ex
            End Try
            Complete(s)
        End While
    End Sub

    ''' <summary>One line for the writer log: how the add's folders were listed.</summary>
    Public Sub LogSummary()
        Dim folders As Integer, entries As Long, byAdd As Integer, retried As Integer, waited As Double, own As Double
        SyncLock _lock
            folders = _folders : entries = _entries : byAdd = _byAdd : retried = _retried
            waited = _waitTicks / Stopwatch.Frequency : own = Threading.Interlocked.Read(_ownTicks) / Stopwatch.Frequency
        End SyncLock
        Log.Information("Add of {Path:l}: {Folders} folder(s), {Entries} entries in {Seconds:0.0} s; listed {Threads} at once over {Names} server name(s); the add waited {Waited:0.0} s for listings, listed {ByAdd} folder(s) itself in {Own:0.0} s, {Retried} listing(s) retried under the first name.",
                        _rootPath, folders, entries, _clock.Elapsed.TotalSeconds, _threads, If(_threads > 1, _routes.NameCount(_rootPath), 1), waited, byAdd, own, retried)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock _lock
            _stopping = True
            _queue.Clear()
            _slots.Clear()
            Monitor.PulseAll(_lock)
        End SyncLock
    End Sub

    ' ---- listing one folder ----

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure WIN32_FIND_DATA
        Public dwFileAttributes As Integer   ' signed, as .NET declares it: the cast to FileAttributes never overflows
        Public ftCreationTimeLow As UInteger
        Public ftCreationTimeHigh As UInteger
        Public ftLastAccessTimeLow As UInteger
        Public ftLastAccessTimeHigh As UInteger
        Public ftLastWriteTimeLow As UInteger
        Public ftLastWriteTimeHigh As UInteger
        Public nFileSizeHigh As UInteger
        Public nFileSizeLow As UInteger
        Public dwReserved0 As UInteger
        Public dwReserved1 As UInteger
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)> Public cFileName As String
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=14)> Public cAlternateFileName As String
    End Structure

    Private NotInheritable Class FindHandle
        Inherits SafeHandleZeroOrMinusOneIsInvalid
        Public Sub New()
            MyBase.New(True)
        End Sub
        Protected Overrides Function ReleaseHandle() As Boolean
            Return FindClose(handle)
        End Function
    End Class

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function FindFirstFileExW(fileName As String, infoLevel As Integer, ByRef data As WIN32_FIND_DATA,
                                             searchOp As Integer, searchFilter As IntPtr, flags As Integer) As FindHandle
    End Function

    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function FindNextFileW(handle As FindHandle, ByRef data As WIN32_FIND_DATA) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function FindClose(handle As IntPtr) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    Private Const FindExInfoBasic As Integer = 1            ' no 8.3 names
    Private Const FindExSearchNameMatch As Integer = 0
    Private Const FIND_FIRST_EX_LARGE_FETCH As Integer = 2  ' bigger directory queries: fewer round trips to a share
    Private Const ERROR_FILE_NOT_FOUND As Integer = 2, ERROR_PATH_NOT_FOUND As Integer = 3, ERROR_ACCESS_DENIED As Integer = 5
    Private Const ERROR_NO_MORE_FILES As Integer = 18, ERROR_FILENAME_EXCED_RANGE As Integer = 206

    ''' <summary>Joins a folder and a name the way .NET does for listed entries (Path.Combine without its checks).</summary>
    Friend Shared Function JoinPath(folder As String, name As String) As String
        If String.IsNullOrEmpty(folder) Then Return name
        Dim last As Char = folder(folder.Length - 1)
        If last = "\"c OrElse last = "/"c OrElse last = ":"c Then Return folder & name
        Return folder & "\" & name
    End Function

    ''' <summary>
    ''' Lists one folder: every entry except "." and "..", in the order the filesystem gives them (as GetFileSystemInfos
    ''' does). <paramref name="listPath"/> is where to read it (the same folder under another server name, or the folder
    ''' itself); entries are named under <paramref name="folderPath"/>. Errors are thrown as .NET throws them.
    ''' </summary>
    Friend Shared Function ListFolder(folderPath As String, listPath As String) As FolderListing
        Dim entries As New List(Of ListedEntry)
        Dim skipped As List(Of ListedEntry) = Nothing
        Dim search As String = LTFSWriter.FileRecord.ExtendedPath(listPath)
        search = If(search.EndsWith("\", StringComparison.Ordinal), search & "*", search & "\*")
        Dim data As New WIN32_FIND_DATA()
        Using handle As FindHandle = FindFirstFileExW(search, FindExInfoBasic, data, FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH)
            If handle.IsInvalid Then
                Dim err As Integer = Marshal.GetLastWin32Error()
                If err = ERROR_FILE_NOT_FOUND OrElse err = ERROR_NO_MORE_FILES Then Return New FolderListing(folderPath, entries, Nothing)
                Throw ListError(err, folderPath)
            End If
            Do
                Dim name As String = data.cFileName
                If name IsNot Nothing AndAlso name.Length > 0 AndAlso name <> "." AndAlso name <> ".." Then
                    Dim entry As New ListedEntry(folderPath, name, CType(data.dwFileAttributes, FileAttributes),
                                                 (CLng(data.nFileSizeHigh) << 32) Or data.nFileSizeLow,
                                                 (CLng(data.ftCreationTimeHigh) << 32) Or data.ftCreationTimeLow,
                                                 (CLng(data.ftLastAccessTimeHigh) << 32) Or data.ftLastAccessTimeLow,
                                                 (CLng(data.ftLastWriteTimeHigh) << 32) Or data.ftLastWriteTimeLow)
                    If name.IndexOfAny(InvalidNameChars) >= 0 Then
                        If skipped Is Nothing Then skipped = New List(Of ListedEntry)
                        skipped.Add(entry)
                    Else
                        entries.Add(entry)
                    End If
                End If
                If Not FindNextFileW(handle, data) Then
                    Dim err As Integer = Marshal.GetLastWin32Error()
                    If err = ERROR_NO_MORE_FILES OrElse err = ERROR_FILE_NOT_FOUND OrElse err = 0 Then Exit Do
                    Throw ListError(err, folderPath)
                End If
            Loop
        End Using
        Return New FolderListing(folderPath, entries, skipped)
    End Function

    Private Shared Function ListError(err As Integer, path As String) As Exception
        Select Case err
            Case ERROR_PATH_NOT_FOUND
                Return New DirectoryNotFoundException($"Could not find a part of the path '{path}'.")
            Case ERROR_ACCESS_DENIED
                Return New UnauthorizedAccessException($"Access to the path '{path}' is denied.")
            Case ERROR_FILENAME_EXCED_RANGE
                Return New PathTooLongException($"The path is too long: '{path}'.")
            Case Else
                Return New IOException($"{New ComponentModel.Win32Exception(err).Message} '{path}'", &H80070000 Or err)
        End Select
    End Function

    ''' <summary>A name with its control characters shown as [U+000D], for messages.</summary>
    Friend Shared Function VisibleName(name As String) As String
        If name Is Nothing Then Return String.Empty
        Dim sb As New Text.StringBuilder(name.Length + 8)
        For Each c As Char In name
            If AscW(c) < 32 Then sb.Append($"[U+{AscW(c):X4}]") Else sb.Append(c)
        Next
        Return sb.ToString()
    End Function

    ''' <summary>The characters of a name .NET paths cannot hold, as "U+000D, &lt;".</summary>
    Friend Shared Function BadCharacters(name As String) As String
        Return String.Join(", ", name.Where(Function(c) Array.IndexOf(InvalidNameChars, c) >= 0).Distinct().
                                     Select(Function(c) If(AscW(c) < 32, $"U+{AscW(c):X4}", c.ToString())))
    End Function

    ''' <summary>
    ''' For the pattern add (LTFSWriter_MatchPattern): GetFileSystemInfos, or when a name in the folder makes it throw
    ''' ArgumentException, the other entries (looked up one by one) and a warning naming the ones left out.
    ''' </summary>
    Friend Shared Function SafeFileSystemInfos(directory As DirectoryInfo) As IEnumerable(Of FileSystemInfo)
        Try
            Return directory.GetFileSystemInfos()
        Catch ex As ArgumentException
            Dim listing As FolderListing = ListFolder(directory.FullName, directory.FullName)
            If listing.Skipped IsNot Nothing Then
                Log.Warning("Not added, {Count} name(s) with a character Windows paths cannot hold in {Folder:l}: {Names:l}",
                            listing.Skipped.Count, directory.FullName, String.Join(", ", listing.Skipped.Select(Function(e) VisibleName(e.Name))))
            End If
            Return listing.Entries.Select(Function(e) If(e.IsDirectory, CType(New DirectoryInfo(e.FullName), FileSystemInfo), New FileInfo(e.FullName))).ToList()
        End Try
    End Function
End Class
