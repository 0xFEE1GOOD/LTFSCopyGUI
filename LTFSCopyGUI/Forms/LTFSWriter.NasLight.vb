' The NAS light in the writer's status line (2.003): blue = files on that NAS will be read through its LTFS NAS Reader
' (LTFSWriter_NasReader is set, the reader answers and accepts LTFSWriter_NasReaderKey); grey = not set; red = set but
' not ready (the tooltip says why). Checked every 20 s in the background and when the light is clicked.
' Kept in its own file so upstream changes to LTFSWriter.vb merge cleanly.
Partial Class LTFSWriter

    Private _nasLight As ToolStripStatusLabel
    Private _nasTimer As Timer
    Private _nasProbing As Integer
    Private Const NasLightLegend As String = "NAS reader: blue = ready, files on that NAS are read through it; grey = off; red = not ready"

    Private Sub NasLight_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        _nasLight = New ToolStripStatusLabel(ChrW(&H25CF) & " NAS") With {.AutoSize = True, .ForeColor = Color.Gray,
                                                                         .ToolTipText = NasLightLegend & vbCrLf & "checking…"}
        Dim at As Integer = StatusStrip2.Items.IndexOf(ToolStripStatusLabelS6)
        StatusStrip2.Items.Insert(If(at < 0, StatusStrip2.Items.Count, at + 1), _nasLight)
        AddHandler _nasLight.Click, Sub() NasLightCheck()
        _nasTimer = New Timer With {.Interval = 20000}
        AddHandler _nasTimer.Tick, Sub() NasLightCheck()
        _nasTimer.Start()
        NasLightCheck()
    End Sub

    Private Sub NasLight_Closed(sender As Object, e As FormClosedEventArgs) Handles MyBase.FormClosed
        If _nasTimer IsNot Nothing Then _nasTimer.Stop()
    End Sub

    Private Sub NasLightCheck()
        If Threading.Interlocked.Exchange(_nasProbing, 1) <> 0 Then Return
        Dim setting As String = My.Settings.LTFSWriter_NasReader
        Dim key As String = My.Settings.LTFSWriter_NasReaderKey
        Task.Run(Function() NasSource.Probe(setting, key)).ContinueWith(
            Sub(t)
                Threading.Interlocked.Exchange(_nasProbing, 0)
                If IsDisposed OrElse Not IsHandleCreated OrElse t.IsFaulted Then Return
                Dim r As NasSource.ProbeResult = t.Result
                Try
                    BeginInvoke(Sub()
                                    If _nasLight Is Nothing Then Return
                                    _nasLight.ForeColor = If(r.State = 1, Color.Blue, If(r.State = 0, Color.Gray, Color.Red))
                                    _nasLight.ToolTipText = NasLightLegend & vbCrLf & r.Text & vbCrLf & "checked " & Now.ToString("HH:mm:ss") & " (click to check now)"
                                End Sub)
                Catch ex As InvalidOperationException
                    ' the window closed meanwhile
                End Try
            End Sub)
    End Sub
End Class
