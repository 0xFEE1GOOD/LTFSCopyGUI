Imports System.Runtime.InteropServices
Imports Serilog

''' <summary>
''' 2.021: runs a piece of CPU work on the fastest cores of a hybrid CPU. On the owner's i9-13900H (6 fast cores with
''' two threads each, 8 slow cores) Windows put the writer thread on a slow core while a write kept the other cores
''' busy, so "Generating index" took 1.7-1.9 times as long as on a fast core (measured in the app on the virtual tape:
''' 0.35 s against 0.20 s for 65,600 files; the same index offline on the slow cores against the fast ones, 0.85 s
''' against 0.46 s). Windows reports each core's efficiency class through CPU sets (Windows 10 / Server 2016 and
''' later). When every core has the same class, or the calls are not there, the work runs where it is.
''' </summary>
Friend NotInheritable Class FastCores
    Private Sub New()
    End Sub

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetSystemCpuSetInformation(information As IntPtr, bufferLength As Integer, ByRef returnedLength As Integer, process As IntPtr, flags As Integer) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetThreadSelectedCpuSets(thread As IntPtr, <Out> cpuSetIds As UInteger(), cpuSetIdCount As Integer, ByRef requiredIdCount As Integer) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetThreadSelectedCpuSets(thread As IntPtr, cpuSetIds As UInteger(), cpuSetIdCount As Integer) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll")>
    Private Shared Function GetCurrentThread() As IntPtr
    End Function

    Private Shared ReadOnly _fastest As UInteger() = FindFastest()
    Private Shared _logged As Integer

    ''' <summary>The CPU set ids of the highest efficiency class, or Nothing when all cores share one class.</summary>
    Private Shared Function FindFastest() As UInteger()
        Try
            Dim length As Integer = 0
            GetSystemCpuSetInformation(IntPtr.Zero, 0, length, IntPtr.Zero, 0)
            If length <= 0 Then Return Nothing
            Dim buffer As IntPtr = Marshal.AllocHGlobal(length)
            Try
                If Not GetSystemCpuSetInformation(buffer, length, length, IntPtr.Zero, 0) Then Return Nothing
                ' SYSTEM_CPU_SET_INFORMATION: Size (4 bytes), Type (4; 0 = CpuSetInformation), Id (4), Group (2), then one
                ' byte each: LogicalProcessorIndex, CoreIndex, LastLevelCacheIndex, NumaNodeIndex, EfficiencyClass.
                Dim sets As New List(Of KeyValuePair(Of UInteger, Byte))
                Dim offset As Integer = 0
                While offset + 19 <= length
                    Dim size As Integer = Marshal.ReadInt32(buffer, offset)
                    If size <= 0 Then Exit While
                    Dim id As Integer = Marshal.ReadInt32(buffer, offset + 8)
                    If size >= 19 AndAlso Marshal.ReadInt32(buffer, offset + 4) = 0 AndAlso id >= 0 Then
                        sets.Add(New KeyValuePair(Of UInteger, Byte)(CUInt(id), Marshal.ReadByte(buffer, offset + 18)))
                    End If
                    offset += size
                End While
                If sets.Count = 0 Then Return Nothing
                Dim top As Byte = sets.Max(Function(s) s.Value)
                If sets.All(Function(s) s.Value = top) Then Return Nothing
                Return sets.Where(Function(s) s.Value = top).Select(Function(s) s.Key).ToArray()
            Finally
                Marshal.FreeHGlobal(buffer)
            End Try
        Catch   ' EntryPointNotFoundException before Windows 10
            Return Nothing
        End Try
    End Function

    ''' <summary>Runs work on this thread, kept on the fastest cores while it runs; the thread's own CPU sets come back after.</summary>
    Friend Shared Function Run(Of T)(work As Func(Of T)) As T
        Dim previous As UInteger() = Nothing
        Dim moved As Boolean = False
        If _fastest IsNot Nothing Then
            Try
                Dim thread As IntPtr = GetCurrentThread()
                Dim count As Integer = 0
                If Not GetThreadSelectedCpuSets(thread, Nothing, 0, count) AndAlso count > 0 Then
                    previous = New UInteger(count - 1) {}
                    If Not GetThreadSelectedCpuSets(thread, previous, count, count) Then previous = Nothing
                End If
                moved = SetThreadSelectedCpuSets(thread, _fastest, _fastest.Length)
                If Threading.Interlocked.Exchange(_logged, 1) = 0 Then
                    Log.Information("Index generation runs on the fastest cores: {Count} of {Total} logical processors (CPU sets), applied {Applied}.",
                                    _fastest.Length, Environment.ProcessorCount, moved)
                End If
            Catch
                moved = False
            End Try
        End If
        Try
            Return work()
        Finally
            If moved Then
                Try
                    SetThreadSelectedCpuSets(GetCurrentThread(), previous, If(previous Is Nothing, 0, previous.Length))
                Catch
                End Try
            End If
        End Try
    End Function
End Class
