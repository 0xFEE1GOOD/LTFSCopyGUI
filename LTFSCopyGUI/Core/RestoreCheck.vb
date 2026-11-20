Imports System.Buffers
Imports System.Security.Cryptography
Imports System.Threading
Imports Blake3

''' <summary>
''' One restored file's check against the checksum the index recorded when the file was written (2.010). The restore
''' feeds it the file's bytes in file order (RestoreChecker.Add); a file whose pieces come out of file order is hashed
''' from its temporary file instead (HashFile). Only standard hex checksums are used (SHA-1, SHA-256, SHA-512, MD5,
''' BLAKE3): the XxHash and CRC32 values may be written in another byte order by the external reader, and a false
''' mismatch would throw away a good file.
''' </summary>
Friend NotInheritable Class RestoreCheck
    Public Enum Outcome
        Pending
        Match
        Mismatch
        Unchecked
    End Enum

    Public ReadOnly Algorithm As String
    Public ReadOnly Expected As String
    Public ReadOnly Length As Long
    ' Consumer (restore) thread only: the file offset the next piece must start at, and whether one did not.
    Friend QueuedOffset As Long
    Friend OutOfOrder As Boolean
    ' Checker thread until Done is set.
    Private _hash As HashAlgorithm
    Private _blake As Hasher
    Private _blakeOpen As Boolean
    Private _actual As String
    Private _result As Outcome = Outcome.Pending
    Private _reason As String
    Private ReadOnly _done As New ManualResetEventSlim(False)

    Private Sub New(algorithm As String, expected As String, length As Long)
        Me.Algorithm = algorithm
        Me.Expected = expected
        Me.Length = length
    End Sub

    ''' <summary>A check for this file, or Nothing when the index holds no checksum this version can compute.</summary>
    Public Shared Function ForFile(f As ltfsindex.file) As RestoreCheck
        If f Is Nothing OrElse f.length <= 0 Then Return Nothing
        Dim keys As String() = {ltfsindex.file.xattr.HashType.SHA1, ltfsindex.file.xattr.HashType.SHA256,
                                ltfsindex.file.xattr.HashType.SHA512, ltfsindex.file.xattr.HashType.MD5,
                                ltfsindex.file.xattr.HashType.BLAKE3}
        Dim names As String() = {"SHA-1", "SHA-256", "SHA-512", "MD5", "BLAKE3"}
        Dim lengths As Integer() = {40, 64, 128, 32, 64}
        For i As Integer = 0 To keys.Length - 1
            Dim v As String = f.GetXAttr(keys(i), True)
            If v Is Nothing Then Continue For
            v = v.Trim()
            If v.Length = lengths(i) Then Return New RestoreCheck(names(i), v, f.length)
        Next
        Return Nothing
    End Function

    Private Function NewHashState() As Boolean
        Select Case Algorithm
            Case "SHA-1" : _hash = SHA1.Create()
            Case "SHA-256" : _hash = SHA256.Create()
            Case "SHA-512" : _hash = SHA512.Create()
            Case "MD5" : _hash = MD5.Create()
            Case "BLAKE3" : _blake = Hasher.NewInstance() : _blakeOpen = True
            Case Else : Return False
        End Select
        Return True
    End Function

    Private Sub Update(data As Byte(), offset As Integer, count As Integer)
        If _hash IsNot Nothing Then
            _hash.TransformBlock(data, offset, count, Nothing, 0)
        ElseIf _blakeOpen Then
            _blake.UpdateWithJoin(New ArraySegment(Of Byte)(data, offset, count))
        End If
    End Sub

    Private Function FinalValue() As String
        If _hash IsNot Nothing Then
            _hash.TransformFinalBlock(Array.Empty(Of Byte)(), 0, 0)
            Dim v As String = BitConverter.ToString(_hash.Hash).Replace("-", "")
            _hash.Dispose()
            _hash = Nothing
            Return v
        End If
        If _blakeOpen Then
            Dim v As String = _blake.Finalize().ToString()
            _blake.Dispose()
            _blakeOpen = False
            Return v
        End If
        Return Nothing
    End Function

    Private Sub ReleaseHash()
        Try
            If _hash IsNot Nothing Then _hash.Dispose()
            If _blakeOpen Then _blake.Dispose()
        Catch
        End Try
        _hash = Nothing
        _blakeOpen = False
    End Sub

    ' Checker thread: the next piece, in file order.
    Friend Sub Append(data As Byte(), count As Integer)
        If _result <> Outcome.Pending OrElse OutOfOrder Then Return
        Try
            If _hash Is Nothing AndAlso Not _blakeOpen AndAlso Not NewHashState() Then Unchecked("unknown checksum type")
            If _result = Outcome.Pending Then Update(data, 0, count)
        Catch ex As Exception
            Unchecked($"the {Algorithm} check failed: {ex.Message}")
        End Try
    End Sub

    ' Checker thread: the file's last piece was queued.
    Friend Sub Complete()
        Try
            If _result = Outcome.Pending AndAlso Not OutOfOrder Then
                If QueuedOffset <> Length Then
                    Unchecked($"only {QueuedOffset:N0} of {Length:N0} bytes came from this tape")
                Else
                    If _hash Is Nothing AndAlso Not _blakeOpen Then NewHashState()
                    Decide(FinalValue())
                End If
            End If
        Catch ex As Exception
            Unchecked($"the {Algorithm} check failed: {ex.Message}")
        Finally
            ReleaseHash() ' an out-of-order file stays Pending: HashFile decides it on the restore thread
            _done.Set()
        End Try
    End Sub

    Private Sub Decide(actual As String)
        _actual = actual
        If actual Is Nothing Then
            Unchecked("no checksum computed")
        ElseIf String.Equals(actual, Expected, StringComparison.OrdinalIgnoreCase) Then
            _result = Outcome.Match
        Else
            _result = Outcome.Mismatch
            _reason = $"its data does not match the {Algorithm} written to tape with it (index {Expected.ToUpperInvariant()}, restored {actual.ToUpperInvariant()})"
        End If
    End Sub

    Private Sub Unchecked(reason As String)
        _result = Outcome.Unchecked
        _reason = reason
        ReleaseHash()
    End Sub

    Public ReadOnly Property IsDone As Boolean
        Get
            Return _done.IsSet
        End Get
    End Property

    Public Sub WaitDone()
        _done.Wait()
    End Sub

    ''' <summary>For a file whose pieces came out of file order: hash the restored copy on disk (restore thread,
    ''' after WaitDone).</summary>
    Public Sub HashFile(path As String)
        If Not OutOfOrder OrElse _result <> Outcome.Pending Then Return
        Dim block As Byte() = ArrayPool(Of Byte).Shared.Rent(1 << 20)
        Try
            If Not NewHashState() Then Unchecked("unknown checksum type") : Return
            Dim total As Long = 0
            Using fs As New IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, 4096, IO.FileOptions.SequentialScan)
                Do
                    Dim n As Integer = fs.Read(block, 0, 1 << 20)
                    If n <= 0 Then Exit Do
                    Update(block, 0, n)
                    total += n
                Loop
            End Using
            If total <> Length Then Unchecked($"the restored copy has {total:N0} of {Length:N0} bytes") : Return
            Decide(FinalValue())
        Catch ex As Exception
            Unchecked($"the {Algorithm} check failed: {ex.Message}")
        Finally
            ArrayPool(Of Byte).Shared.Return(block)
            ReleaseHash()
        End Try
    End Sub

    Public ReadOnly Property Result As Outcome
        Get
            Return _result
        End Get
    End Property

    ''' <summary>Why the file did not match, or why it could not be checked.</summary>
    Public ReadOnly Property Reason As String
        Get
            Return _reason
        End Get
    End Property
End Class

''' <summary>
''' Runs the restore's checksum checks on their own thread (2.010), so the restore keeps reading at the drive's pace:
''' SHA-1 runs at about 720 MiB/s on a P-core here and 370 on an E-core, while one record arrives about every 1.1 ms.
''' The restore copies each piece (a memory copy, about 0.015 ms per 512 KiB record) because its record buffers go back
''' to the read-ahead right after use. At most 256 MiB waits; beyond that the restore waits for the checks.
''' </summary>
Friend NotInheritable Class RestoreChecker
    Implements IDisposable

    Private Structure Item
        Public Check As RestoreCheck
        Public Data As Byte() ' Nothing: the file's last piece was queued
        Public Count As Integer
    End Structure

    Private Const MaxQueuedBytes As Long = 256L << 20
    Private ReadOnly _lock As New Object
    Private ReadOnly _queue As New Queue(Of Item)
    Private _queuedBytes As Long
    Private _closing As Boolean
    Private _waitTicks As Long
    Private ReadOnly _thread As Thread

    Public Sub New()
        _thread = New Thread(AddressOf Run) With {.IsBackground = True, .Name = "Restore checksum check"}
        _thread.Start()
    End Sub

    ''' <summary>Seconds the restore waited because the checks had fallen 256 MiB behind.</summary>
    Public ReadOnly Property WaitSeconds As Double
        Get
            Return Interlocked.Read(_waitTicks) / CDbl(Diagnostics.Stopwatch.Frequency)
        End Get
    End Property

    ''' <summary>Restore thread: count bytes of data(offset) were written at fileOffset of the check's file.</summary>
    Public Sub Add(check As RestoreCheck, data As Byte(), offset As Integer, count As Integer, fileOffset As Long)
        If check Is Nothing OrElse count <= 0 OrElse check.OutOfOrder Then Return
        If fileOffset <> check.QueuedOffset Then
            check.OutOfOrder = True
            Return
        End If
        check.QueuedOffset += count
        Dim copy As Byte() = ArrayPool(Of Byte).Shared.Rent(count)
        Buffer.BlockCopy(data, offset, copy, 0, count)
        SyncLock _lock
            If _queuedBytes > MaxQueuedBytes Then
                Dim t0 As Long = Diagnostics.Stopwatch.GetTimestamp()
                While _queuedBytes > MaxQueuedBytes AndAlso Not _closing
                    Monitor.Wait(_lock, 100)
                End While
                Interlocked.Add(_waitTicks, Diagnostics.Stopwatch.GetTimestamp() - t0)
            End If
            _queue.Enqueue(New Item With {.Check = check, .Data = copy, .Count = count})
            _queuedBytes += count
            Monitor.PulseAll(_lock)
        End SyncLock
    End Sub

    ''' <summary>Restore thread: the check's file has had all its pieces; the result follows (check.IsDone).</summary>
    Public Sub Finish(check As RestoreCheck)
        If check Is Nothing Then Return
        SyncLock _lock
            _queue.Enqueue(New Item With {.Check = check})
            Monitor.PulseAll(_lock)
        End SyncLock
    End Sub

    Private Sub Run()
        Do
            Dim item As Item
            SyncLock _lock
                While _queue.Count = 0 AndAlso Not _closing
                    Monitor.Wait(_lock)
                End While
                If _queue.Count = 0 Then Return
                item = _queue.Dequeue()
            End SyncLock
            If item.Data Is Nothing Then
                item.Check.Complete()
            Else
                item.Check.Append(item.Data, item.Count)
                ArrayPool(Of Byte).Shared.Return(item.Data)
                SyncLock _lock
                    _queuedBytes -= item.Count
                    Monitor.PulseAll(_lock)
                End SyncLock
            End If
        Loop
    End Sub

    ''' <summary>Finishes what is queued (at most 256 MiB) and stops the thread.</summary>
    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock _lock
            _closing = True
            Monitor.PulseAll(_lock)
        End SyncLock
        _thread.Join()
    End Sub
End Class
