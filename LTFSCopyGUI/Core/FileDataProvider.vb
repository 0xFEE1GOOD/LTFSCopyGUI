Imports System.Collections.Concurrent
Imports System.IO
Imports System.IO.Pipelines
Imports System.Threading
Imports System.Buffers
Imports System.Runtime.InteropServices
Imports Serilog
Imports Serilog.Context

' 高性能文件数据提供器：
' - 仅暴露一个 PipeReader（单读者），内部 Pipe 使用 256MiB 背压阈值（可配），避免过量内存占用
' - 小文件(<16KiB)积极缓存到内存，最多缓存 1000 个，超出则排队等待
' - 大文件采用 FileStream 异步顺序读取，按顺序积极写入 Pipe（默认开启），以充分利用 256MiB 管线缓存
' - 通过 AutoResetEvent 可选地控制文件间的连续处理（当 requireSignal=True 时，消费者每完成一个文件需调用 RequestNextFile 开始下一个）
' - 生产端自动在后台填充小文件缓存，并顺序将（小/大）文件内容写入 Pipe
' 用法建议（示意）：
'   ' 连续积极缓存（默认）：
'   Dim provider = New FileDataProvider(WriteList)
'   provider.Start()
'   For Each fr In WriteList
'       Dim remaining = fr.File.length
'       While remaining > 0
'           Dim result = Await provider.Reader.ReadAsync()
'           Dim buffer = result.Buffer
'           Dim toConsume = Math.Min(remaining, buffer.Length)
'           Dim slice = buffer.Slice(0, toConsume)
'           ' 将 slice 写入磁带（略）
'           provider.Reader.AdvanceTo(slice.End, slice.End)
'           remaining -= toConsume
'           If result.IsCompleted AndAlso remaining > 0 Then Throw New EndOfStreamException()
'       End While
'   Next
'   Await provider.CompleteAsync()
'   ' 事件驱动（与旧设计兼容）：
'   Dim provider2 = New FileDataProvider(WriteList, requireSignal:=True)
'   provider2.Start()
'   For i = 0 To WriteList.Count - 1
'       provider2.RequestNextFile()
'       ' 同上消费逻辑...

Public Class FileDataProvider
    Private ReadOnly _pipe As Pipe
    Private ReadOnly _writer As PipeWriter
    Public ReadOnly Property Reader As PipeReader
    Public ReadOnly RingBuffer As SpscRingBuffer
    Private _ringBufferEnabled As Boolean

    Private ReadOnly _writeList As List(Of LTFSWriter.FileRecord)
    Private ReadOnly _smallThreshold As Long
    Private ReadOnly _smallCacheCapacity As Integer
    Private ReadOnly _requireSignal As Boolean

    Private ReadOnly _smallCacheMap As New ConcurrentDictionary(Of LTFSWriter.FileRecord, Byte())
    Private _smallCacheBytes As Long = 0
    Private Const SmallCacheByteLimit As Long = 256L * 1024 * 1024 ' with the file-count setting: at most this much held

    ' Reads kept in flight (LTFSWriter_ParallelSourceReads): preload workers for small files, and handles ("lanes") per
    ' large file. From a NAS over 10G (DS3615xs, cold files, 2026-11-19) the single-read path below gave 276 MB/s, four
    ' lanes of 8 MiB 470-495 MB/s, and four preload workers 34 % more small files per second. RandomAccess turns off
    ' Windows read-ahead on each lane: with SequentialScan the read-ahead fights the stride (175 MB/s in a test).
    Private ReadOnly _parallelReads As Integer
    Private Const LaneChunkBytes As Integer = 8 << 20
    Private Const LaneMinBytes As Long = 2L * LaneChunkBytes
    Private _preloadNext As Integer = -1
    Private _preloadCached As Integer = 0
    ' One set of lane buffers for the whole job: the producer streams one file at a time and every read of a file has
    ' finished before the next file starts, so they are reused instead of putting 8 MiB arrays on the LOH per file.
    Private _laneBuffers As Byte()() = Nothing

    Private ReadOnly _nextFileSignal As New AutoResetEvent(False)
    Private ReadOnly _cts As New CancellationTokenSource()
    Private ReadOnly _completionLock As New Object()
    Private _producerTask As Task = Nothing
    Private _preloadTask As Task = Nothing
    Private _completionTask As Task = Nothing
    Private _outputCompleted As Integer = 0
    Private _readerCompleted As Integer = 0

    Private _currentIndex As Integer = -1
    Private _started As Integer = 0
    Private _current As LTFSWriter.FileRecord = Nothing
    Private ReadOnly _logSessionId As String = $"fileprovider-{Guid.NewGuid().ToString("N").Substring(0, 8)}"

    Public ReadOnly Property Current As LTFSWriter.FileRecord
        Get
            Return _current
        End Get
    End Property

    Public ReadOnly Property ProducerCompleted As Boolean
        Get
            Dim task As Task = _producerTask
            Return task IsNot Nothing AndAlso task.IsCompleted
        End Get
    End Property

    Private _producerFault As Exception
    ''' <summary>Why the producer stopped early (e.g. a source file could not be read), or Nothing.</summary>
    Public ReadOnly Property ProducerFault As Exception
        Get
            Return Volatile.Read(_producerFault)
        End Get
    End Property

    ' 参数：
    ' - pipeBufferMiB: Pipe 背压阈值（默认 256MiB）
    ' - smallThresholdBytes: 小文件阈值（默认 16KiB）
    ' - smallCacheCapacity: 小文件缓存容量上限（默认 1000 个）
    ' - requireSignal: 是否需要外部通过 RequestNextFile 触发下一个文件（默认 False=积极连续缓存）
    Public Sub New(writeList As List(Of LTFSWriter.FileRecord),
                   Optional pipeBufferBytes As Long = 256 << 20,
                   Optional smallThresholdBytes As Long = 16 * 1024,
                   Optional smallCacheCapacity As Integer = 1000,
                   Optional requireSignal As Boolean = False,
                   Optional parallelReads As Integer = 1)

        If writeList Is Nothing Then Throw New ArgumentNullException(NameOf(writeList))
        _parallelReads = Math.Max(1, Math.Min(8, parallelReads))

        'Keep a private shallow copy.  The provider may release slots as it
        'advances, while the writer must keep its indexed plan stable until
        'each corresponding record has been consumed.
        _writeList = New List(Of LTFSWriter.FileRecord)(writeList)
        _smallThreshold = Math.Max(1, smallThresholdBytes)
        _smallCacheCapacity = Math.Max(1, smallCacheCapacity)
        _requireSignal = requireSignal
        _ringBufferEnabled = My.Settings.LTFSWriter_RingBufferEnabled
        If _ringBufferEnabled Then
            RingBuffer = New SpscRingBuffer(pipeBufferBytes)
        Else
            Dim pause As Long = pipeBufferBytes
            Dim resumeTh As Long = Math.Max(1L, (pause \ 4) * 3)
            _pipe = New Pipe(New PipeOptions(
                pauseWriterThreshold:=pause,
                resumeWriterThreshold:=resumeTh,
                minimumSegmentSize:=My.Settings.LTFSWriter_MinimumSegmentSize,
                useSynchronizationContext:=False
            ))
            Reader = _pipe.Reader
            _writer = _pipe.Writer
        End If
    End Sub

    Public Sub Start()
        If Interlocked.Exchange(_started, 1) <> 0 Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Warning("File data provider start was ignored because the provider was already started.")
                        End Using
                    End Using
                End Using
            End Using
            Return
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider started. FileCount={FileCount} RingBufferEnabled={RingBufferEnabled} PipeBufferBytes={PipeBufferBytes} SmallThresholdBytes={SmallThresholdBytes} SmallCacheCapacity={SmallCacheCapacity} RequireSignal={RequireSignal} ParallelReads={ParallelReads}.",
                                        _writeList.Count,
                                        _ringBufferEnabled,
                                        If(_ringBufferEnabled, RingBuffer.Capacity, 0L),
                                        _smallThreshold,
                                        _smallCacheCapacity,
                                        _requireSignal,
                                        _parallelReads)
                    End Using
                End Using
            End Using
        End Using
        _preloadTask = Task.Run(AddressOf PreloadSmallFilesAsync)
        _producerTask = Task.Run(AddressOf ProducerLoopAsync)
        ' 积极模式下，立即允许开始
        If Not _requireSignal Then _nextFileSignal.Set()
    End Sub

    ' 由消费者调用，指示可以开始传输下一个文件（仅当 requireSignal=True 时需要）
    Public Sub RequestNextFile()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileSwitch")
                        Log.Information("File data provider requested the next file. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
                    End Using
                End Using
            End Using
        End Using
        Try
            _nextFileSignal.Set()
        Catch ex As ObjectDisposedException
        End Try
    End Sub

    Public Sub Cancel()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                        Log.Information("File data provider cancellation requested. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
                    End Using
                End Using
            End Using
        End Using
        Try
            _cts.Cancel()
        Catch ex As ObjectDisposedException
        End Try
        Try
            _nextFileSignal.Set()
        Catch ex As ObjectDisposedException
        End Try
    End Sub

    Public Function CompleteAsync() As Task
        SyncLock _completionLock
            If _completionTask Is Nothing Then _completionTask = CompleteCoreAsync()
            Return _completionTask
        End SyncLock
    End Function

    Private Async Function CompleteCoreAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider completion started. CurrentIndex={CurrentIndex} CachedSmallFiles={CachedSmallFiles}.",
                                        Interlocked.CompareExchange(_currentIndex, 0, 0),
                                        _smallCacheMap.Count)
                    End Using
                End Using
            End Using
        End Using
        Try
            _cts.Cancel()
        Catch ex As ObjectDisposedException
        End Try
        Try
            _nextFileSignal.Set()
        Catch ex As ObjectDisposedException
        End Try

        CompleteOutput()
        Await AwaitBackgroundTaskAsync(_producerTask, "producer").ConfigureAwait(False)
        Await AwaitBackgroundTaskAsync(_preloadTask, "preloader").ConfigureAwait(False)
        CompleteReader()

        Try
            _nextFileSignal.Dispose()
        Catch
        End Try
        Try
            _cts.Dispose()
        Catch
        End Try
        If _ringBufferEnabled Then
            Try
                RingBuffer.Dispose()
            Catch
            End Try
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider completion requested. CachedSmallFiles={CachedSmallFiles}.", _smallCacheMap.Count)
                    End Using
                End Using
            End Using
        End Using
    End Function

    Private Async Function AwaitBackgroundTaskAsync(task As Task, taskName As String) As Task
        If task Is Nothing Then Return
        Try
            Await task.ConfigureAwait(False)
        Catch ex As OperationCanceledException When _cts.IsCancellationRequested
            Log.Information(ex, "File data provider {TaskName} task stopped during cancellation.", taskName)
        Catch ex As Exception
            Log.Error(ex, "File data provider {TaskName} task failed while completing.", taskName)
        End Try
    End Function

    Private Sub CompleteOutput()
        If Interlocked.Exchange(_outputCompleted, 1) <> 0 Then Return
        Try
            If _ringBufferEnabled Then
                RingBuffer.Complete()
            Else
                _writer.Complete()
            End If
        Catch ex As Exception
            Log.Error(ex, "File data provider output completion failed.")
        End Try
    End Sub

    Private Sub CompleteReader()
        If _ringBufferEnabled OrElse Interlocked.Exchange(_readerCompleted, 1) <> 0 Then Return
        Try
            Reader.Complete()
        Catch ex As Exception
            Log.Error(ex, "File data provider reader completion failed.")
        End Try
    End Sub

    Private Async Function ProducerLoopAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("File data provider producer loop started.")
                    End Using
                End Using
            End Using
        End Using
        Try
            While Not _cts.IsCancellationRequested
                ' requireSignal=True 时按事件推进；否则积极连续推进
                If _requireSignal Then
                    _nextFileSignal.WaitOne()
                    If _cts.IsCancellationRequested Then Exit While
                End If

                Dim nextIdx As Integer = Interlocked.Increment(_currentIndex)
                If nextIdx >= _writeList.Count Then Exit While

                Dim fr As LTFSWriter.FileRecord = _writeList(nextIdx)
                If nextIdx - 1 >= 0 Then
                    _writeList(nextIdx - 1) = Nothing
                End If
                _current = fr

                If fr Is Nothing OrElse fr.File Is Nothing Then
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                                    Log.Warning("File data provider skipped an invalid FileRecord. FileIndex={FileIndex}.", nextIdx)
                                End Using
                            End Using
                        End Using
                    End Using
                    Throw New InvalidDataException($"File data provider encountered an invalid FileRecord at index {nextIdx}.")
                End If

                Dim isSmallFile = IsSmallRecord(fr)
                If Log.IsEnabled(Serilog.Events.LogEventLevel.Debug) Then ' per-file trace: thousands per second
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                                    Log.Debug("File data provider began reading a file. FileIndex={FileIndex} SourcePath={SourcePath} Length={Length} SmallFile={SmallFile} CacheHit={CacheHit}.",
                                                    nextIdx,
                                                    If(fr Is Nothing, String.Empty, fr.SourcePath),
                                                    If(fr Is Nothing OrElse fr.File Is Nothing, 0L, fr.File.length),
                                                    isSmallFile,
                                                    isSmallFile AndAlso _smallCacheMap.ContainsKey(fr))
                                End Using
                            End Using
                        End Using
                    End Using
                End If

                If isSmallFile Then
                    Dim data As Byte() = Nothing
                    If _smallCacheMap.TryRemove(fr, data) Then
                        Interlocked.Add(_smallCacheBytes, -data.LongLength)
                        fr.IsOpened = True ' as ReadAllBytesSafe does: the writer waits for this before taking the data
                    Else
                        data = ReadAllBytesSafe(fr)
                    End If

                    Dim expectedLength As Long = fr.File.length
                    If data Is Nothing OrElse data.LongLength <> expectedLength Then
                        Throw New EndOfStreamException($"Source file length changed while reading: {fr.SourcePath}; expected {expectedLength}, got {If(data Is Nothing, 0L, data.LongLength)}.")
                    End If
                    If data.Length > 0 Then
                        If _ringBufferEnabled Then
                            WriteAllToRing(data, _cts.Token)
                        Else
                            _writer.Write(data.AsSpan())
                            Dim res = Await _writer.FlushAsync(_cts.Token)
                            If res.IsCanceled OrElse res.IsCompleted Then Exit While
                        End If
                    End If
                Else
                    ' 大文件：流式拷贝到 Pipe（积极缓存，受 Pipe 背压调节）
                    Await StreamFileToPipeAsync(fr, _cts.Token)
                End If
                If Log.IsEnabled(Serilog.Events.LogEventLevel.Debug) Then ' per-file trace: thousands per second
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileCompleted")
                                    Log.Debug("File data provider completed a file. FileIndex={FileIndex} SourcePath={SourcePath} CachedSmallFiles={CachedSmallFiles}.",
                                                    nextIdx,
                                                    If(fr Is Nothing, String.Empty, fr.SourcePath),
                                                    _smallCacheMap.Count)
                                End Using
                            End Using
                        End Using
                    End Using
                End If
                ' 在积极模式下，自动继续下一个文件；在信号模式下，等待下一次 RequestNextFile
                If _requireSignal = False Then
                    ' 继续循环即可
                End If
            End While
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Information("File data provider producer loop completed. CurrentIndex={CurrentIndex} CancellationRequested={CancellationRequested}.",
                                            Interlocked.CompareExchange(_currentIndex, 0, 0),
                                            _cts.IsCancellationRequested)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When _cts.IsCancellationRequested
            Log.Information(ex, "File data provider producer loop stopped during cancellation. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
        Catch ex As Exception When _cts.IsCancellationRequested
            Log.Information(ex, "File data provider producer loop stopped after cancellation. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
        Catch ex As Exception
            Volatile.Write(_producerFault, ex)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "File data provider producer loop failed. CurrentIndex={CurrentIndex}.", Interlocked.CompareExchange(_currentIndex, 0, 0))
                        End Using
                    End Using
                End Using
            End Using
        Finally
            CompleteOutput()
        End Try
    End Function

    Private Sub WriteAllToRing(data As Byte(), ct As CancellationToken)
        WriteAllToRing(data, data.Length, ct)
    End Sub

    Private Sub WriteAllToRing(data As Byte(), count As Integer, ct As CancellationToken)
        Dim offset As Integer = 0
        While offset < count
            Dim seg = RingBuffer.GetWriteSegment(1, ct)
            If seg.Count = 0 Then Throw New EndOfStreamException("Ring buffer completed before all data was written.")
            Dim n As Integer = Math.Min(seg.Count, count - offset)
            Buffer.BlockCopy(data, offset, seg.Array, seg.Offset, n)
            RingBuffer.AdvanceWrite(n)
            offset += n
        End While
    End Sub

    Private Function ReadAllBytesSafe(fr As LTFSWriter.FileRecord) As Byte()
        Try
            Dim result As Byte() = fr.ReadAllBytes()
            fr.IsOpened = True
            Return result
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                            Log.Warning(ex, "File data provider managed file read failed; switching to direct file fallback. SourcePath={SourcePath}.", fr.SourcePath)
                        End Using
                    End Using
                End Using
            End Using
            While True
                Try
                    Dim result As Byte() = File.ReadAllBytes(fr.EnsureSourcePathResolved())
                    fr.IsOpened = True
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                                    Log.Information("File data provider direct file fallback completed. SourcePath={SourcePath} Bytes={Bytes}.", fr.SourcePath, result.Length)
                                End Using
                            End Using
                        End Using
                    End Using
                    Return result
                Catch fallbackEx As Exception
                    Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                        Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                            Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                                Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                    Log.Error(fallbackEx, "File data provider direct file fallback failed. SourcePath={SourcePath}.", fr.SourcePath)
                                End Using
                            End Using
                        End Using
                    End Using
                    fr.IsOpened = False
                    Throw fallbackEx
                End Try
            End While
        End Try
        Return Array.Empty(Of Byte)()
    End Function
    Private Async Function StreamFileToPipeAsync(fr As LTFSWriter.FileRecord, ct As CancellationToken) As Task
        Dim fs As FileStream = Nothing
        Dim sourcePath As String = fr.EnsureSourcePathResolved()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                        Log.Information("File data provider stream read started. SourcePath={SourcePath} FileOffset={FileOffset} SegmentLength={SegmentLength}.",
                                        fr.SourcePath,
                                        fr.FileOffset,
                                        fr.SegmentLength)
                    End Using
                End Using
            End Using
        End Using
        Dim lanes As Integer = LaneCountFor(fr)
        Try
            If lanes > 1 Then
                ' Lane 0; StreamFileInLanesAsync opens the others the same way.
                fs = New FileStream(sourcePath, FileMode.Open, FileAccess.Read, LTFSWriter.FileRecord.SourceShare, 4096, FileOptions.Asynchronous Or FileOptions.RandomAccess)
            Else
                ' Cached setting (My.Settings takes a lock), and never a buffer larger than the file itself.
                Dim bufferSize As Integer = CInt(Math.Max(4096L, Math.Min(CLng(CachedSetting(Of Integer)(NameOf(My.Settings.LTFSWriter_FileStreamBufferSize))), fr.File.length)))
                fs = New FileStream(sourcePath, FileMode.Open, FileAccess.Read, LTFSWriter.FileRecord.SourceShare, bufferSize, FileOptions.Asynchronous Or FileOptions.SequentialScan)
            End If
            If fr.File.length = 0 Then
                fr.File.length = fs.Length
                fr.FileOffset = 0
                fr.SegmentLength = fs.Length
            End If
            fr.IsOpened = True
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                            Log.Warning(ex, "File data provider stream open failed; using FileRecord fallback. SourcePath={SourcePath}.", fr.SourcePath)
                        End Using
                    End Using
                End Using
            End Using
            ' 备用：尝试使用现有 FileRecord 打开
            lanes = 1
            Try
                Select Case fr.Open(BufferSize:=64 * 1024)
                    Case DialogResult.Ignore
                        Throw New EndOfStreamException($"Unable to open source file: {fr.SourcePath}")
                    Case DialogResult.Abort
                        Throw New IOException("Open aborted")
                End Select
                fs = fr.fs
            Catch fallbackEx As Exception
                Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                    Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                        Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                            Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                                Log.Error(fallbackEx, "File data provider FileRecord fallback open failed. SourcePath={SourcePath}.", fr.SourcePath)
                            End Using
                        End Using
                    End Using
                End Using
                fr.IsOpened = False
                Throw fallbackEx
            End Try
        End Try

        Dim totalReadLen As Long = 0
        Using fs
            If lanes > 1 Then
                totalReadLen = Await StreamFileInLanesAsync(fr, sourcePath, fs, lanes, ct).ConfigureAwait(False)
            ElseIf _ringBufferEnabled Then
                If fr.FileOffset > 0 Then fs.Seek(fr.FileOffset, SeekOrigin.Begin)
                Dim minChunk As Integer = 1024 * 1024
                While Not ct.IsCancellationRequested
                    If totalReadLen >= fr.SegmentLength Then Exit While
                    Dim remaining As Long = fr.SegmentLength - totalReadLen
                    Dim seg = RingBuffer.GetWriteSegment(CInt(Math.Min(CLng(minChunk), remaining)), ct)
                    If seg.Count = 0 Then
                        ' 理论上不会（除非 completed/disposed/canceled）
                        Throw New EndOfStreamException($"Source stream ended before the expected length was read: {fr.SourcePath}")
                    End If

                    Dim readCapacity As Integer = CInt(Math.Min(CLng(seg.Count), remaining))
                    Dim n As Integer = Await fs.ReadAsync(seg.Array, seg.Offset, readCapacity, ct).ConfigureAwait(False)
                    If n = 0 Then Throw New EndOfStreamException($"Source stream ended before the expected length was read: {fr.SourcePath}")
                    RingBuffer.AdvanceWrite(n)
                    totalReadLen += n
                End While
            Else
                If fr.FileOffset > 0 Then fs.Seek(fr.FileOffset, SeekOrigin.Begin)
                Dim minSize As Integer = 64 * 1024
                While Not ct.IsCancellationRequested
                    If totalReadLen >= fr.SegmentLength Then Exit While
                    Dim remaining As Long = fr.SegmentLength - totalReadLen
                    Dim dest As Memory(Of Byte) = _writer.GetMemory(CInt(Math.Min(CLng(minSize), remaining)))
                    Dim seg As New ArraySegment(Of Byte)
                    If Not MemoryMarshal.TryGetArray(Of Byte)(dest, seg) Then
                        Throw New Exception("TryGetArray failed")
                    End If
                    Dim cap As Integer = CInt(Math.Min(Math.Min(CLng(minSize), CLng(seg.Count)), remaining))
                    Dim n = Await fs.ReadAsync(seg.Array, seg.Offset, cap, ct).ConfigureAwait(False)
                    If n = 0 Then Throw New EndOfStreamException($"Source stream ended before the expected length was read: {fr.SourcePath}")
                    _writer.Advance(n)
                    totalReadLen += n
                    Dim result = Await _writer.FlushAsync(ct).ConfigureAwait(False)
                    If result.IsCanceled OrElse result.IsCompleted Then Exit While
                End While
            End If
        End Using
        If Not ct.IsCancellationRequested AndAlso totalReadLen <> fr.SegmentLength Then
            Throw New EndOfStreamException($"Source stream ended before the expected length was read: {fr.SourcePath}; expected {fr.SegmentLength}, got {totalReadLen}.")
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "FileRead")
                        Log.Information("File data provider stream read completed. SourcePath={SourcePath} BytesRead={BytesRead} CancellationRequested={CancellationRequested}.",
                                        fr.SourcePath,
                                        totalReadLen,
                                        ct.IsCancellationRequested)
                    End Using
                End Using
            End Using
        End Using
    End Function

    ''' <summary>Handles to read this file over: 1 (one read at a time) unless it spans at least two 8 MiB chunks.</summary>
    Private Function LaneCountFor(fr As LTFSWriter.FileRecord) As Integer
        If _parallelReads < 2 OrElse fr.File.length = 0 OrElse fr.SegmentLength < LaneMinBytes Then Return 1
        Return CInt(Math.Min(CLng(_parallelReads), (fr.SegmentLength + LaneChunkBytes - 1) \ LaneChunkBytes))
    End Function

    ''' <summary>
    ''' Reads [FileOffset, FileOffset + SegmentLength) in 8 MiB chunks: chunk c on lane c Mod lanes, one read in flight
    ''' per lane, chunks passed on in order. Each lane has its own buffer, reused for its next chunk once the current one
    ''' is in the pipe, so at most lanes x 8 MiB is held. A lane that cannot be opened only lowers the count.
    ''' </summary>
    Private Async Function StreamFileInLanesAsync(fr As LTFSWriter.FileRecord, sourcePath As String, first As FileStream,
                                                  lanes As Integer, ct As CancellationToken) As Task(Of Long)
        Dim streams As New List(Of FileStream) From {first}
        Dim pending As New Queue(Of KeyValuePair(Of Long, Task(Of Integer)))
        Try
            For i As Integer = 1 To lanes - 1
                Try
                    streams.Add(New FileStream(sourcePath, FileMode.Open, FileAccess.Read, LTFSWriter.FileRecord.SourceShare, 4096, FileOptions.Asynchronous Or FileOptions.RandomAccess))
                Catch ex As Exception
                    Log.Warning(ex, "File data provider opened {Lanes} of {Wanted} read lanes. SourcePath={SourcePath}.", streams.Count, lanes, fr.SourcePath)
                    Exit For
                End Try
            Next
            Dim laneCount As Integer = streams.Count
            If _laneBuffers Is Nothing Then _laneBuffers = New Byte(_parallelReads - 1)() {}
            Dim buffers As Byte()() = _laneBuffers
            For i As Integer = 0 To laneCount - 1
                If buffers(i) Is Nothing Then buffers(i) = New Byte(LaneChunkBytes - 1) {}
            Next
            Dim length As Long = fr.SegmentLength
            Dim chunkCount As Long = (length + LaneChunkBytes - 1) \ LaneChunkBytes
            Dim startChunk = Sub(c As Long)
                                 Dim lane As Integer = CInt(c Mod laneCount)
                                 Dim want As Integer = CInt(Math.Min(CLng(LaneChunkBytes), length - c * LaneChunkBytes))
                                 pending.Enqueue(New KeyValuePair(Of Long, Task(Of Integer))(c, ReadChunkAsync(streams(lane), fr.FileOffset + c * LaneChunkBytes, buffers(lane), want, ct)))
                             End Sub
            For c As Long = 0 To Math.Min(CLng(laneCount), chunkCount) - 1
                startChunk(c)
            Next
            Dim total As Long = 0
            While pending.Count > 0
                Dim head = pending.Peek()
                Dim n As Integer = Await head.Value.ConfigureAwait(False)
                pending.Dequeue()
                Dim want As Integer = CInt(Math.Min(CLng(LaneChunkBytes), length - head.Key * LaneChunkBytes))
                If n <> want Then
                    Throw New EndOfStreamException($"Source stream ended before the expected length was read: {fr.SourcePath}; expected {length}, got {total + n}.")
                End If
                Dim buf As Byte() = buffers(CInt(head.Key Mod laneCount))
                Dim outputClosed As Boolean = False
                If _ringBufferEnabled Then
                    WriteAllToRing(buf, n, ct)
                Else
                    _writer.Write(buf.AsSpan(0, n))
                    Dim result = Await _writer.FlushAsync(ct).ConfigureAwait(False)
                    outputClosed = result.IsCanceled OrElse result.IsCompleted
                End If
                total += n
                If outputClosed Then Exit While
                If head.Key + laneCount < chunkCount Then startChunk(head.Key + laneCount) ' this lane's buffer is free again
            End While
            Return total
        Finally
            ' Reads still in flight (cancel, error, pipe closed) finish before their handles and buffers go.
            For Each p In pending
                Try
                    p.Value.Wait()
                Catch
                End Try
            Next
            For i As Integer = 1 To streams.Count - 1 ' lane 0 belongs to the caller's Using
                streams(i).Dispose()
            Next
        End Try
    End Function

    Private Shared Async Function ReadChunkAsync(fs As FileStream, position As Long, buffer As Byte(), count As Integer,
                                                 ct As CancellationToken) As Task(Of Integer)
        fs.Seek(position, SeekOrigin.Begin)
        Dim got As Integer = 0
        While got < count
            Dim n As Integer = Await fs.ReadAsync(buffer, got, count - got, ct).ConfigureAwait(False)
            If n = 0 Then Exit While
            got += n
        End While
        Return got
    End Function

    Private Function IsSmallRecord(fr As LTFSWriter.FileRecord) As Boolean
        Return fr IsNot Nothing AndAlso fr.File IsNot Nothing AndAlso fr.File.length < _smallThreshold AndAlso
               fr.FileOffset = 0 AndAlso fr.File.length = fr.SegmentLength
    End Function

    ''' <summary>
    ''' Reads small files ahead of the producer so it hands them over from memory instead of waiting on the source
    ''' (a NAS: one network round trip per file). Walks the list by index: the producer clears entries it has passed
    ''' (_writeList(i) = Nothing) and a For Each threw on the first such change, so before v1.007 every preload
    ''' stopped after a few files. Files the producer has already reached are left to it. FileRecord.IsOpened is not
    ''' touched here (the producer sets it when it takes the file), and read failures are left to the producer, which
    ''' retries and reports them.
    ''' </summary>
    Private Async Function PreloadSmallFilesAsync() As Task
        Try
            ' Workers take the next index from one counter, so the files nearest the producer are fetched first.
            Dim workers(_parallelReads - 1) As Task
            For w As Integer = 0 To _parallelReads - 1
                workers(w) = Task.Run(AddressOf PreloadWorkerAsync)
            Next
            Await Task.WhenAll(workers).ConfigureAwait(False)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cache")
                            Log.Information("File data provider small-file preload completed. CachedFiles={CachedFiles} CacheEntries={CacheEntries} Workers={Workers}.", Volatile.Read(_preloadCached), _smallCacheMap.Count, _parallelReads)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException
            ' The write finished or was stopped while the cache was full.
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(FileDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "FileProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "File data provider small-file preload failed. CachedFiles={CachedFiles}.", Volatile.Read(_preloadCached))
                        End Using
                    End Using
                End Using
            End Using
        End Try
    End Function

    Private Async Function PreloadWorkerAsync() As Task
        While Not _cts.IsCancellationRequested
            Dim i As Integer = Interlocked.Increment(_preloadNext)
            If i >= _writeList.Count Then Exit While
            If i <= Volatile.Read(_currentIndex) Then Continue While
            Dim fr As LTFSWriter.FileRecord = _writeList(i)
            If Not IsSmallRecord(fr) OrElse fr.Buffer IsNot Nothing Then Continue While ' a buffered record is in memory already
            ' 控制小文件缓存上限
            While (_smallCacheMap.Count >= _smallCacheCapacity OrElse Interlocked.Read(_smallCacheBytes) >= SmallCacheByteLimit) AndAlso
                  Not _cts.IsCancellationRequested
                Await Task.Delay(10, _cts.Token).ConfigureAwait(False)
            End While
            If i <= Volatile.Read(_currentIndex) Then Continue While

            Dim data As Byte() = Nothing
            Try
                data = fr.ReadAllBytes()
            Catch
                data = Nothing
            End Try
            If data Is Nothing OrElse data.LongLength <> fr.File.length Then Continue While
            Interlocked.Add(_smallCacheBytes, data.LongLength)
            If Not _smallCacheMap.TryAdd(fr, data) Then
                Interlocked.Add(_smallCacheBytes, -data.LongLength)
                Continue While
            End If
            Interlocked.Increment(_preloadCached)
            ' The producer may have reached this file while it was being read: take it back so it is not held to the end.
            Dim stale As Byte() = Nothing
            If i <= Volatile.Read(_currentIndex) AndAlso _smallCacheMap.TryRemove(fr, stale) Then
                Interlocked.Add(_smallCacheBytes, -stale.LongLength)
                Interlocked.Decrement(_preloadCached)
            End If
        End While
    End Function
End Class
Public Class HardDriveDataProvider
    Private ReadOnly _pipe As Pipe
    Private ReadOnly _writer As PipeWriter
    Public ReadOnly Property Reader As PipeReader
    Public ReadOnly RingBuffer As SpscRingBuffer
    Private _ringBufferEnabled As Boolean

    Private ReadOnly _cts As New CancellationTokenSource()
    Private ReadOnly _completionLock As New Object()
    Private _producerTask As Task = Nothing
    Private _completionTask As Task = Nothing
    Private _outputCompleted As Integer = 0
    Private _readerCompleted As Integer = 0

    Private _started As Integer = 0
    Private ReadOnly _logSessionId As String = $"harddrive-provider-{Guid.NewGuid().ToString("N").Substring(0, 8)}"
    Public Property DevicePath As String
    Public Property StartLBA As ULong
    Public Property SectorCount As Long
    Public Property SectorLength As Integer = 512
    Public Property SectorLenUpdated As Boolean = False
    Public ReadOnly Property ProducerCompleted As Boolean
        Get
            Dim task As Task = _producerTask
            Return task IsNot Nothing AndAlso task.IsCompleted
        End Get
    End Property

    Public Class Config
        Public Property DrivePath As String
        Public Property StartLBA As ULong
        Public Property SectorCount As Long
    End Class
    ' 参数：
    ' - pipeBufferMiB: Pipe 背压阈值（默认 256MiB）
    Public Sub New(path As String, StartLBA As ULong, SectorCount As Long,
                   Optional pipeBufferBytes As Long = 256 << 20)

        _ringBufferEnabled = My.Settings.LTFSWriter_RingBufferEnabled
        DevicePath = path
        Me.StartLBA = StartLBA
        Me.SectorCount = SectorCount
        If _ringBufferEnabled Then
            RingBuffer = New SpscRingBuffer(pipeBufferBytes)
        Else
            Dim pause As Long = pipeBufferBytes
            Dim resumeTh As Long = Math.Max(1L, (pause \ 4) * 3)
            _pipe = New Pipe(New PipeOptions(
                pauseWriterThreshold:=pause,
                resumeWriterThreshold:=resumeTh,
                minimumSegmentSize:=My.Settings.LTFSWriter_MinimumSegmentSize,
                useSynchronizationContext:=False
            ))
            Reader = _pipe.Reader
            _writer = _pipe.Writer
        End If
    End Sub

    Public Sub Start()
        If Interlocked.Exchange(_started, 1) <> 0 Then
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Warning("Hard drive data provider start was ignored because the provider was already started. DevicePath={DevicePath}.", DevicePath)
                        End Using
                    End Using
                End Using
            End Using
            Return
        End If
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Hard drive data provider started. DevicePath={DevicePath} StartLba={StartLba} SectorCount={SectorCount} RingBufferEnabled={RingBufferEnabled}.",
                                        DevicePath,
                                        StartLBA,
                                        SectorCount,
                                        _ringBufferEnabled)
                    End Using
                End Using
            End Using
        End Using
        _producerTask = Task.Run(AddressOf ProducerLoopAsync)
    End Sub


    Public Sub Cancel()
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Cancellation")
                        Log.Information("Hard drive data provider cancellation requested. DevicePath={DevicePath}.", DevicePath)
                    End Using
                End Using
            End Using
        End Using
        Try
            _cts.Cancel()
        Catch ex As ObjectDisposedException
        End Try
    End Sub

    Public Function CompleteAsync() As Task
        SyncLock _completionLock
            If _completionTask Is Nothing Then _completionTask = CompleteCoreAsync()
            Return _completionTask
        End SyncLock
    End Function

    Private Async Function CompleteCoreAsync() As Task
        Log.Information("Hard drive data provider completion started. DevicePath={DevicePath}.", DevicePath)
        Try
            _cts.Cancel()
        Catch ex As ObjectDisposedException
        End Try
        CompleteOutput()
        Await AwaitBackgroundTaskAsync(_producerTask).ConfigureAwait(False)
        CompleteReader()

        Try
            _cts.Dispose()
        Catch
        End Try
        If _ringBufferEnabled Then
            Try
                RingBuffer.Dispose()
            Catch
            End Try
        End If
        Log.Information("Hard drive data provider completion requested. DevicePath={DevicePath}.", DevicePath)
    End Function

    Private Async Function AwaitBackgroundTaskAsync(task As Task) As Task
        If task Is Nothing Then Return
        Try
            Await task.ConfigureAwait(False)
        Catch ex As OperationCanceledException When _cts.IsCancellationRequested
            Log.Information(ex, "Hard drive data provider producer stopped during cancellation. DevicePath={DevicePath}.", DevicePath)
        Catch ex As Exception
            Log.Error(ex, "Hard drive data provider producer failed while completing. DevicePath={DevicePath}.", DevicePath)
        End Try
    End Function

    Private Sub CompleteOutput()
        If Interlocked.Exchange(_outputCompleted, 1) <> 0 Then Return
        Try
            If _ringBufferEnabled Then
                RingBuffer.Complete()
            Else
                _writer.Complete()
            End If
        Catch ex As Exception
            Log.Error(ex, "Hard drive data provider output completion failed. DevicePath={DevicePath}.", DevicePath)
        End Try
    End Sub

    Private Sub CompleteReader()
        If _ringBufferEnabled OrElse Interlocked.Exchange(_readerCompleted, 1) <> 0 Then Return
        Try
            Reader.Complete()
        Catch ex As Exception
            Log.Error(ex, "Hard drive data provider reader completion failed. DevicePath={DevicePath}.", DevicePath)
        End Try
    End Sub

    Private Async Function ProducerLoopAsync() As Task
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                        Log.Information("Hard drive data provider producer loop started. DevicePath={DevicePath}.", DevicePath)
                    End Using
                End Using
            End Using
        End Using
        Try
            ' 大文件：流式拷贝到 Pipe（积极缓存，受 Pipe 背压调节）
            If Not _cts.IsCancellationRequested Then
                Await StreamDiskToPipeAsync(DevicePath, StartLBA, SectorCount, _cts.Token)
            End If
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Lifecycle")
                            Log.Information("Hard drive data provider producer loop completed. DevicePath={DevicePath} CancellationRequested={CancellationRequested}.", DevicePath, _cts.IsCancellationRequested)
                        End Using
                    End Using
                End Using
            End Using
        Catch ex As OperationCanceledException When _cts.IsCancellationRequested
            Log.Information(ex, "Hard drive data provider producer stopped during cancellation. DevicePath={DevicePath}.", DevicePath)
        Catch ex As Exception When _cts.IsCancellationRequested
            Log.Information(ex, "Hard drive data provider producer stopped after cancellation. DevicePath={DevicePath}.", DevicePath)
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Hard drive data provider producer loop failed. DevicePath={DevicePath}.", DevicePath)
                        End Using
                    End Using
                End Using
            End Using
        Finally
            CompleteOutput()
        End Try
    End Function

    Private Sub WriteAllToRing(data As Byte(), ct As CancellationToken)
        Dim offset As Integer = 0
        While offset < data.Length
            Dim seg = RingBuffer.GetWriteSegment(1, ct)
            If seg.Count = 0 Then Throw New EndOfStreamException("Ring buffer completed before all data was written.")
            Dim n As Integer = Math.Min(seg.Count, data.Length - offset)
            Buffer.BlockCopy(data, offset, seg.Array, seg.Offset, n)
            RingBuffer.AdvanceWrite(n)
            offset += n
        End While
    End Sub
    Private Async Function StreamDiskToPipeAsync(path As String, StartLBA As ULong, SectorCount As Long, ct As CancellationToken) As Task
        Dim driveHandle As IntPtr
        Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
            Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                    Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRead")
                        Log.Information("Hard drive stream read started. DevicePath={DevicePath} StartLba={StartLba} RequestedSectorCount={RequestedSectorCount}.",
                                        path,
                                        StartLBA,
                                        SectorCount)
                    End Using
                End Using
            End Using
        End Using
        If Not TapeUtils.OpenTapeDrive(path, driveHandle) Then
            Dim openError As Integer = TapeUtils.LastWin32Error
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error("Hard drive stream read could not open the device. DevicePath={DevicePath} Win32Error={Win32Error}.", path, openError)
                        End Using
                    End Using
                End Using
            End Using
            Throw New ComponentModel.Win32Exception(openError, $"Unable to open hard drive device: {path}")
        End If
        Try
            Dim batchSize As Integer = 128
            With DiskQuery.QuerySectorInfo(driveHandle)
                SectorLength = .SectorSize
                If SectorLength <= 0 Then Throw New InvalidDataException("The device reported an invalid sector size.")
                batchSize = Math.Max(1, 65536 \ SectorLength)
                If StartLBA >= .LBACount Then Throw New InvalidDataException("The requested disk start LBA is outside the device.")
                If SectorCount < 0 Then
                    SectorCount = CLng(.LBACount - StartLBA)
                ElseIf CULng(SectorCount) > .LBACount - StartLBA Then
                    Throw New InvalidDataException("The requested disk sector range exceeds the device.")
                End If
                If SectorCount <= 0 Then Throw New InvalidDataException("The requested disk sector range is empty.")
                Me.SectorCount = SectorCount
                SectorLenUpdated = True
            End With
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRead")
                            Log.Information("Hard drive sector geometry resolved. DevicePath={DevicePath} SectorLength={SectorLength} SectorCount={SectorCount}.",
                                            path,
                                            SectorLength,
                                            SectorCount)
                        End Using
                    End Using
                End Using
            End Using
            Dim LBA As ULong = StartLBA
            Dim EndLBA As ULong = StartLBA + CULng(SectorCount) - 1UL
            If _ringBufferEnabled Then
                Dim minChunk As Integer = SectorLength * batchSize
                While Not ct.IsCancellationRequested
                    If LBA > EndLBA Then Exit While
                    Dim batch As Integer = CInt(Math.Min(batchSize, EndLBA - LBA + 1))
                    Dim totalBytes As Integer = batch * SectorLength
                    Dim dataPtr As IntPtr = TapeUtils.SCSIReadParamUnmanaged(driveHandle, {&H28, 0,
                    CByte(CLng((LBA >> 24)) And &HFF), CByte(CLng((LBA >> 16)) And &HFF),
                    CByte(CLng((LBA >> 8)) And &HFF), CByte(CLng((LBA >> 0)) And &HFF),
                    0, CByte((batch >> 8) And &HFF), CByte((batch >> 0) And &HFF), 0}, totalBytes)
                    LBA = CULng(LBA + batch)
                    If dataPtr = IntPtr.Zero Then Throw New IOException("The device reader returned a null data buffer.")
                    Try
                        Dim remaining As Integer = totalBytes
                        Dim srcOffset As Integer = 0
                        While remaining > 0
                            Dim seg = RingBuffer.GetWriteSegment(remaining, ct)
                            If seg.Count = 0 Then Throw New EndOfStreamException("The device data buffer ended before the requested sector range was written.")
                            Dim toCopy As Integer = Math.Min(remaining, seg.Count)
                            Marshal.Copy(IntPtr.Add(dataPtr, srcOffset), seg.Array, seg.Offset, toCopy)
                            RingBuffer.AdvanceWrite(toCopy)
                            srcOffset += toCopy
                            remaining -= toCopy
                        End While
                    Finally
                        Marshal.FreeHGlobal(dataPtr)
                    End Try
                End While
            Else
                Dim minSize As Integer = SectorLength * batchSize
                While Not ct.IsCancellationRequested
                    If LBA > EndLBA Then Exit While
                    Dim batch As Integer = CInt(Math.Min(batchSize, EndLBA - LBA + 1))
                    Dim totalBytes As Integer = batch * SectorLength
                    Dim dataPtr As IntPtr = TapeUtils.SCSIReadParamUnmanaged(driveHandle, {&H28, 0,
                    CByte(CLng((LBA >> 24)) And &HFF), CByte(CLng((LBA >> 16)) And &HFF),
                    CByte(CLng((LBA >> 8)) And &HFF), CByte(CLng((LBA >> 0)) And &HFF),
                    0, CByte((batch >> 8) And &HFF), CByte((batch >> 0) And &HFF), 0}, totalBytes)
                    LBA = CULng(LBA + batch)
                    If dataPtr = IntPtr.Zero Then Throw New IOException("The device reader returned a null data buffer.")
                    Try
                        Dim remaining As Integer = totalBytes
                        Dim srcOffset As Integer = 0
                        While remaining > 0
                            Dim dest As Memory(Of Byte) = _writer.GetMemory(CInt(Math.Min(CLng(minSize), CLng(remaining))))
                            Dim seg As New ArraySegment(Of Byte)
                            If Not MemoryMarshal.TryGetArray(Of Byte)(dest, seg) Then
                                Throw New Exception("TryGetArray failed")
                            End If
                            Dim cap As Integer = CInt(Math.Min(Math.Min(CLng(minSize), CLng(seg.Count)), CLng(remaining)))
                            If cap <= 0 Then Throw New EndOfStreamException("The device data buffer ended before the requested sector range was written.")
                            Dim toCopy As Integer = Math.Min(remaining, cap)
                            Marshal.Copy(IntPtr.Add(dataPtr, srcOffset), seg.Array, seg.Offset, toCopy)
                            _writer.Advance(toCopy)
                            srcOffset += toCopy
                            remaining -= toCopy
                        End While
                    Finally
                        Marshal.FreeHGlobal(dataPtr)
                    End Try
                    Dim result = Await _writer.FlushAsync(ct).ConfigureAwait(False)
                    If result.IsCanceled OrElse result.IsCompleted Then Exit While
                End While
            End If
        Catch ex As Exception
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "Error")
                            Log.Error(ex, "Hard drive stream read failed. DevicePath={DevicePath} StartLba={StartLba} SectorCount={SectorCount}.",
                                      path,
                                      StartLBA,
                                      SectorCount)
                        End Using
                    End Using
                End Using
            End Using
            Throw ex
        Finally
            TapeUtils.CloseTapeDrive(driveHandle)
            Using sourceContextScope As IDisposable = LogContext.PushProperty("SourceContext", NameOf(HardDriveDataProvider))
                Using categoryScope As IDisposable = LogContext.PushProperty("Category", "HardDriveProvider")
                    Using sessionScope As IDisposable = LogContext.PushProperty("SessionId", _logSessionId)
                        Using eventTypeScope As IDisposable = LogContext.PushProperty("EventType", "DeviceRead")
                            Log.Information("Hard drive stream read closed. DevicePath={DevicePath}.", path)
                        End Using
                    End Using
                End Using
            End Using
        End Try

    End Function

End Class
