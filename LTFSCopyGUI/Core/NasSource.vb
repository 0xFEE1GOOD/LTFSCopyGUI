Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Serilog

''' <summary>
''' Reads source files through the LTFS NAS Reader package on the NAS (NasReader\src\lcg-nasreader.py) instead of over
''' SMB. The write list is still built over the share, so names, times and the index stay as they were; the NAS gets
''' the list once and sends the bytes back in list order, reading ahead with many reads at once on its own disks.
''' DS3615xs, cold office folder of 7,126 files (2026-11-19): over SMB 159 files/s (v1.011: 4 readers over 3
''' connections), on the NAS itself 1,074 files/s with 16 readers. Anything the NAS does not send - a file it cannot
''' find or read, a lost connection - is read over the share as before.
''' Settings: LTFSWriter_NasReader = "host" or "host:port" (empty = off), LTFSWriter_NasReaderKey = the key from the
''' NAS page. Used by FileDataProvider's producer only (one thread); Covers() may be called from any thread.
''' </summary>
Friend NotInheritable Class NasSource
    Implements IDisposable

    Public Const DefaultPort As Integer = 7171
    Private Const ChunkMax As Integer = 8 << 20          ' the service sends at most 8 MiB per frame

    ''' <summary>Status of one entry: 0 read; 1 not found; 2 read error; 3 refused by the NAS; -1 connection lost;
    ''' -2 output closed (the write stopped). Sent = bytes handed to the sink.</summary>
    Public Structure Outcome
        Public Status As Integer
        Public Sent As Long
    End Structure

    Private Class LostException
        Inherits Exception
        Public Sub New(message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
        End Sub
    End Class

    Private ReadOnly _client As TcpClient
    Private ReadOnly _in As Stream
    Private ReadOnly _order As Integer()                  ' list indexes in the order the NAS sends them
    Private ReadOnly _covered As HashSet(Of Integer)
    Private ReadOnly _buffer As Byte() = New Byte(ChunkMax - 1) {}
    Private ReadOnly _head As Byte() = New Byte(12) {}
    Private _next As Integer                              ' position in _order of the entry the stream is at
    Private _dead As Integer
    Private _served As Integer
    Private _notServed As Integer
    Private ReadOnly _problems As New List(Of String)
    Public ReadOnly Property Endpoint As String

    Private Sub New(client As TcpClient, input As Stream, order As Integer(), endpoint As String)
        _client = client
        _in = input
        _order = order
        _covered = New HashSet(Of Integer)(order)
        Me.Endpoint = endpoint
    End Sub

    ''' <summary>
    ''' Connects, proves the key both ways and sends the list of files that are on that NAS; Nothing (logged) when the
    ''' setting is empty, no file is on that NAS, or the NAS cannot be reached - the provider then reads over the share.
    ''' </summary>
    ''' <summary>"host" or "host:port" -> host and port (7171 by default).</summary>
    Private Shared Sub ParseEndpoint(setting As String, ByRef host As String, ByRef port As Integer)
        host = setting
        port = DefaultPort
        Dim colon As Integer = setting.LastIndexOf(":"c)
        If colon > 0 AndAlso Integer.TryParse(setting.Substring(colon + 1), port) Then
            host = setting.Substring(0, colon)
        Else
            port = DefaultPort
        End If
    End Sub

    ''' <summary>The key's bytes, or Nothing when it is not hex.</summary>
    Private Shared Function ParseKey(keyHex As String) As Byte()
        Try
            Dim hex As String = If(keyHex, String.Empty).Trim()
            If hex.Length = 0 OrElse hex.Length Mod 2 <> 0 Then Return Nothing
            Return Enumerable.Range(0, hex.Length \ 2).Select(Function(i) Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray()
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>Opens a connection and proves the key both ways; the stream is then ready for the job or a ping.</summary>
    Private Shared Function Connect(host As String, port As Integer, key As Byte(), connectMs As Integer,
                                    ByRef client As TcpClient) As BufferedStream
        ' No fixed ReceiveBufferSize: setting one turns off Windows' receive window auto-tuning.
        client = New TcpClient() With {.NoDelay = True}
        If Not client.ConnectAsync(host, port).Wait(connectMs) Then Throw New TimeoutException($"no answer within {connectMs \ 1000} s")
        Dim ns As NetworkStream = client.GetStream()
        ns.ReadTimeout = 30000
        ns.WriteTimeout = 60000
        Dim input As New BufferedStream(ns, 1 << 20)
        Dim hello As Byte() = ReadExact(input, 22)
        If Encoding.ASCII.GetString(hello, 0, 4) <> "LNR1" OrElse BitConverter.ToUInt16(hello, 4) <> 1 Then
            Throw New InvalidDataException("the service there is not an LTFS NAS Reader of protocol version 1")
        End If
        Dim nonce(15) As Byte
        Buffer.BlockCopy(hello, 6, nonce, 0, 16)
        Using h As New HMACSHA256(key)
            ns.Write(h.ComputeHash(nonce.Concat(Encoding.ASCII.GetBytes("client")).ToArray()), 0, 32)
            Dim proof As Byte() = ReadExact(input, 32, eofMessage:="the NAS refused the key (LTFSWriter_NasReaderKey)")
            If Not h.ComputeHash(nonce.Concat(Encoding.ASCII.GetBytes("server")).ToArray()).SequenceEqual(proof) Then
                Throw New UnauthorizedAccessException("the NAS answered with another key")
            End If
        End Using
        Return input
    End Function

    ''' <summary>For the NAS light: 0 = off (no setting), 1 = ready, 2 = not ready; Text says why or what it can read.</summary>
    Public Structure ProbeResult
        Public State As Integer
        Public Text As String
    End Structure

    ''' <summary>Is the NAS reader there and does it take the key? Sends no job (a ping); never throws.</summary>
    Public Shared Function Probe(setting As String, keyHex As String) As ProbeResult
        setting = If(setting, String.Empty).Trim()
        If setting.Length = 0 Then Return New ProbeResult With {.State = 0, .Text = "off: LTFSWriter_NasReader is empty, files are read over the share"}
        Dim host As String = Nothing, port As Integer
        ParseEndpoint(setting, host, port)
        Dim key As Byte() = ParseKey(keyHex)
        If key Is Nothing Then Return New ProbeResult With {.State = 2, .Text = $"{host}:{port}: no valid key in LTFSWriter_NasReaderKey"}
        Dim client As TcpClient = Nothing
        Try
            Dim input As BufferedStream = Connect(host, port, key, 3000, client)
            input.WriteByte(2)
            input.Flush()
            Dim version As String = "2.002 or older", shares As String = ""
            Try
                Dim n As Integer = BitConverter.ToUInt16(ReadExact(input, 2), 0)
                Dim json As String = Encoding.UTF8.GetString(ReadExact(input, n))
                Dim v = RegularExpressions.Regex.Match(json, """version""\s*:\s*""([^""]*)""")
                If v.Success Then version = v.Groups(1).Value
                Dim s = RegularExpressions.Regex.Match(json, """shares""\s*:\s*\[([^\]]*)\]")
                If s.Success Then shares = String.Join(", ", RegularExpressions.Regex.Matches(s.Groups(1).Value, """((?:[^""\\]|\\.)*)""").Cast(Of RegularExpressions.Match)().Select(Function(m) RegularExpressions.Regex.Unescape(m.Groups(1).Value)))
            Catch ex As IOException
                ' a 2.002 service closes on the ping; the key was accepted all the same
            End Try
            Return New ProbeResult With {.State = 1, .Text = $"{host}:{port}: ready (service {version})" &
                                         If(shares.Length > 0, $"; it can read {shares}", "")}
        Catch ex As Exception
            Dim reason As String = If(TypeOf ex Is AggregateException AndAlso ex.InnerException IsNot Nothing, ex.InnerException.Message, ex.Message)
            Return New ProbeResult With {.State = 2, .Text = $"{host}:{port}: not ready: {reason}"}
        Finally
            If client IsNot Nothing Then client.Close()
        End Try
    End Function

    Public Shared Function TryOpen(setting As String, keyHex As String, writeList As List(Of LTFSWriter.FileRecord),
                                   routes As SourceRoutes, isSmall As Func(Of LTFSWriter.FileRecord, Boolean)) As NasSource
        setting = If(setting, String.Empty).Trim()
        If setting.Length = 0 Then Return Nothing
        Dim host As String = Nothing, port As Integer
        ParseEndpoint(setting, host, port)
        Dim endpoint As String = $"{host}:{port}"
        Dim key As Byte() = ParseKey(keyHex)
        If key Is Nothing Then
            Log.Warning("NAS reader {Endpoint} not used: no valid key in LTFSWriter_NasReaderKey (copy it from the NAS page). Reading over the share.", endpoint)
            Return Nothing
        End If

        ' The files on that NAS: their path's server resolves to one of the NAS's addresses (192.168.3.5, LinkBigNas,
        ' a mapped drive letter...). Others stay on the share.
        Dim nasAddresses As HashSet(Of String) = IPv4Of(host)
        If nasAddresses.Count = 0 Then
            Log.Warning("NAS reader {Endpoint} not used: the name does not resolve. Reading over the share.", endpoint)
            Return Nothing
        End If
        Dim onNas As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
        Dim order As New List(Of Integer)
        Dim job As New MemoryStream()
        Dim w As New BinaryWriter(job, New UTF8Encoding(False), True)
        For i As Integer = 0 To writeList.Count - 1
            Dim fr As LTFSWriter.FileRecord = writeList(i)
            If fr Is Nothing OrElse fr.File Is Nothing OrElse fr.Buffer IsNot Nothing OrElse fr.IsDirectTapeCopy Then Continue For
            Dim server As String = Nothing, share As String = Nothing, rest As String = Nothing
            If Not routes.SplitNetworkPath(fr.SourcePath, server, share, rest) Then Continue For
            Dim match As Boolean
            If Not onNas.TryGetValue(server, match) Then
                match = IPv4Of(server).Overlaps(nasAddresses)
                onNas(server) = match
            End If
            If Not match Then Continue For
            Dim small As Boolean = isSmall(fr)
            Dim path As Byte() = Encoding.UTF8.GetBytes(share & rest.Replace("\"c, "/"c))
            If path.Length > UShort.MaxValue Then Continue For
            w.Write(CUInt(i))
            w.Write(CULng(If(small, 0L, fr.FileOffset)))
            w.Write(CULng(If(small, fr.File.length, fr.SegmentLength)))
            w.Write(CUShort(path.Length))
            w.Write(path)
            order.Add(i)
        Next
        w.Flush()
        If order.Count = 0 Then
            Log.Information("NAS reader {Endpoint} not used: none of the {Count} files is on that NAS.", endpoint, writeList.Count)
            Return Nothing
        End If

        Dim client As TcpClient = Nothing
        Try
            Dim input As BufferedStream = Connect(host, port, key, 5000, client)
            Dim ns As NetworkStream = client.GetStream()
            Dim name As Byte() = Encoding.UTF8.GetBytes(Environment.MachineName)
            Dim header As New MemoryStream()
            Using hw As New BinaryWriter(header, Encoding.UTF8, True)
                hw.Write(CByte(1))
                hw.Write(CUInt(order.Count))
                hw.Write(CUShort(name.Length))
                hw.Write(name)
            End Using
            ns.Write(header.GetBuffer(), 0, CInt(header.Length))
            ns.Write(job.GetBuffer(), 0, CInt(job.Length))
            ns.ReadTimeout = 120000                       ' the NAS reads ahead; two minutes of silence = stuck
            Log.Information("Source reads go through the NAS reader at {Endpoint}: {Covered} of {Count} files.", endpoint, order.Count, writeList.Count)
            Return New NasSource(client, input, order.ToArray(), endpoint)
        Catch ex As Exception
            Dim reason As String = If(TypeOf ex Is AggregateException AndAlso ex.InnerException IsNot Nothing, ex.InnerException.Message, ex.Message)
            Log.Warning("NAS reader {Endpoint} not used: {Reason}. Reading over the share.", endpoint, reason)
            If client IsNot Nothing Then client.Close()
            Return Nothing
        End Try
    End Function

    Private Shared Function IPv4Of(name As String) As HashSet(Of String)
        Dim result As New HashSet(Of String)(StringComparer.Ordinal)
        Try
            Dim ip As IPAddress = Nothing
            If IPAddress.TryParse(name, ip) Then
                result.Add(ip.ToString())
            Else
                For Each a As IPAddress In Dns.GetHostAddresses(name)
                    If a.AddressFamily = AddressFamily.InterNetwork Then result.Add(a.ToString())
                Next
            End If
        Catch
        End Try
        Return result
    End Function

    ''' <summary>True when the NAS sends this list entry (and the connection is still up).</summary>
    Public Function Covers(index As Integer) As Boolean
        Return Volatile.Read(_dead) = 0 AndAlso _covered.Contains(index)
    End Function

    ''' <summary>A small file's bytes, or Nothing: then read it over the share. A file that is shorter now comes back
    ''' shorter, so the caller's length check reports it as for a share read.</summary>
    Public Function TryReadAll(index As Integer, expected As Long) As Byte()
        Dim data(CInt(expected) - 1) As Byte
        Dim got As Integer = 0
        Dim r As Outcome = ReadAsync(index, Function(buf, n)
                                                Dim take As Integer = Math.Min(n, data.Length - got)
                                                Buffer.BlockCopy(buf, 0, data, got, take)
                                                got += take
                                                Return Task.FromResult(True)
                                            End Function).GetAwaiter().GetResult()
        If r.Status <> 0 Then Return Nothing
        If got < data.Length Then Array.Resize(data, got)
        Return data
    End Function

    ''' <summary>
    ''' Passes the entry's bytes to <paramref name="sink"/> as they arrive (sink returns False when the output closed).
    ''' Only network failures are caught here (the connection is then given up); errors from the sink pass through.
    ''' </summary>
    Public Async Function ReadAsync(index As Integer, sink As Func(Of Byte(), Integer, Task(Of Boolean))) As Task(Of Outcome)
        Dim r As New Outcome With {.Status = -1}
        If Not Covers(index) Then Return r
        Try
            While _next < _order.Length AndAlso _order(_next) < index
                Skip(_order(_next))                        ' an entry the producer did not take (should not happen)
            End While
            If _next >= _order.Length OrElse _order(_next) <> index Then Throw New LostException("out of step")
            While True
                Dim t As Integer = FrameType()
                If t = 1 Then
                    Dim n As Integer = ReadData(index)
                    r.Sent += n
                    If Not Await sink(_buffer, n).ConfigureAwait(False) Then
                        r.Status = -2
                        GiveUp("the write stopped", Nothing)
                        Return r
                    End If
                ElseIf t = 2 Then
                    r.Status = ReadEnd(index, r.Sent)
                    Return r
                Else
                    Throw New LostException(If(t < 0, "connection closed", $"unexpected frame {t}"))
                End If
            End While
        Catch ex As LostException
            GiveUp(ex.Message, ex.InnerException)
            r.Status = -1
        End Try
        Return r
    End Function

    Private Sub Skip(index As Integer)
        Dim sent As Long = 0
        While True
            Dim t As Integer = FrameType()
            If t = 1 Then
                sent += ReadData(index)
            ElseIf t = 2 Then
                ReadEnd(index, sent)
                Return
            Else
                Throw New LostException(If(t < 0, "connection closed", $"unexpected frame {t}"))
            End If
        End While
    End Sub

    ''' <summary>Reads a DATA frame's index and payload into _buffer; returns the payload length.</summary>
    Private Function ReadData(index As Integer) As Integer
        Fill(_head, 8)
        Dim idx As Integer = BitConverter.ToInt32(_head, 0)
        Dim n As Integer = BitConverter.ToInt32(_head, 4)
        If idx <> index OrElse n < 0 OrElse n > ChunkMax Then Throw New LostException("out of step")
        Fill(_buffer, n)
        Return n
    End Function

    ''' <summary>Reads an END frame; returns its status and moves to the next entry.</summary>
    Private Function ReadEnd(index As Integer, sent As Long) As Integer
        Fill(_head, 13)
        Dim status As Integer = _head(4)
        If BitConverter.ToInt32(_head, 0) <> index OrElse BitConverter.ToInt64(_head, 5) <> sent Then Throw New LostException("out of step")
        _next += 1
        If status = 0 Then
            _served += 1
        Else
            _notServed += 1
            If _problems.Count < 20 Then _problems.Add($"#{index}: {StatusText(status)}")
        End If
        Return status
    End Function

    Private Shared Function StatusText(status As Integer) As String
        Select Case status
            Case 1 : Return "not found on the NAS"
            Case 2 : Return "read error on the NAS"
            Case 3 : Return "refused (share unknown to the NAS)"
            Case Else : Return $"status {status}"
        End Select
    End Function

    Private Function FrameType() As Integer
        Fill(_head, 1)
        Return _head(0)
    End Function

    Private Sub Fill(buffer As Byte(), count As Integer)
        Dim got As Integer = 0
        Try
            While got < count
                Dim n As Integer = _in.Read(buffer, got, count - got)
                If n = 0 Then Throw New LostException("connection closed")
                got += n
            End While
        Catch ex As LostException
            Throw
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is SocketException OrElse TypeOf ex Is ObjectDisposedException
            Throw New LostException(ex.Message, ex)
        End Try
    End Sub

    Private Shared Function ReadExact(s As Stream, count As Integer, Optional eofMessage As String = "connection closed") As Byte()
        Dim data(count - 1) As Byte
        Dim got As Integer = 0
        While got < count
            Dim n As Integer = s.Read(data, got, count - got)
            If n = 0 Then Throw New IOException(eofMessage)
            got += n
        End While
        Return data
    End Function

    Private Sub GiveUp(reason As String, inner As Exception)
        If Interlocked.Exchange(_dead, 1) <> 0 Then Return
        If reason <> "the write stopped" Then
            Log.Warning(inner, "NAS reader {Endpoint}: connection lost after {Served} files ({Reason}); the rest is read over the share.", Endpoint, _served, reason)
        End If
        Try
            _client.Close()
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dim wasUp As Boolean = Interlocked.Exchange(_dead, 1) = 0
        Try
            _client.Close()
        Catch
        End Try
        If wasUp OrElse _served > 0 Then
            Log.Information("NAS reader {Endpoint}: {Served} files read by the NAS, {NotServed} read over the share instead{Problems}.",
                            Endpoint, _served, _notServed, If(_problems.Count = 0, "", " (" & String.Join("; ", _problems) & ")"))
        End If
    End Sub
End Class
