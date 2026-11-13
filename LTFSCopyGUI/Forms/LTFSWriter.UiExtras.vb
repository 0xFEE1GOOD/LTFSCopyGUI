' Small usability additions to the writer window, shared in spirit with LTFSCatalog:
' - the six status lights get short labels and tooltips that say what each colour means;
' - the I/O light is steady while commands run instead of blinking (UiIoLightLit, used by Timer2_Tick);
' - Tape > Open LTFSCatalog (Ctrl+Shift+F, search all tapes) and Help > Keyboard shortcuts (F1).
' Kept in its own file so upstream changes to LTFSWriter.vb merge cleanly.
Partial Class LTFSWriter

    ' label and tooltip of each status light, S1 to S6 (S1's tooltip comes from SetStatusLight)
    Private Shared ReadOnly UiLightInfo As String(,) = {
        {"Status", "Status: grey = not ready, blue = idle, orange = busy, green = done, red = error"},
        {"Encryption", "Encryption: blue = encryption is on"},
        {"Cleaning", "Cleaning: orange (blinking) = the drive asks for a cleaning cartridge"},
        {"Tape", "Tape: orange (blinking) = the cartridge reports a problem"},
        {"Drive", "Drive: orange = the drive reports a problem"},
        {"I/O", "Drive I/O: grey = no drive open, green = drive open, bright green = commands running"}}

    Private Shared ReadOnly UiShortcuts As String(,) = {
        {"Ctrl+F", "Search the file names in the loaded index"},
        {"F3", "Find the next match"},
        {"F5", "Refresh the view"},
        {"Ctrl+F5", "Sort the files in the current folder"},
        {"Ctrl+N", "New folder"},
        {"Ctrl+X, Ctrl+V", "Cut and paste in the index (Ctrl+V also adds files copied in Explorer)"},
        {"Ctrl+C", "Copy the selection to another tape"},
        {"Delete", "Delete the selected files or folder from the index"},
        {"Ctrl+P", "Pause or resume writing"},
        {"F8", "Lock or unlock the window"},
        {"F12", "Settings of this window"},
        {"Ctrl+Shift+F", "Search all tapes (opens LTFSCatalog)"},
        {"F1", "This list"}}

    Private _uiIoHold As Integer

    ''' <summary>
    ''' The I/O light: lit while commands run and for three more timer ticks (0.6 s), so the short gaps between
    ''' commands do not make it flicker.
    ''' </summary>
    Private Function UiIoLightLit(running As Boolean) As Boolean
        If running Then
            _uiIoHold = 3
        ElseIf _uiIoHold > 0 Then
            _uiIoHold -= 1
        End If
        Return running OrElse _uiIoHold > 0
    End Function

    Private Sub UiExtras_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Dim lights As ToolStripStatusLabel() = {ToolStripStatusLabelS1, ToolStripStatusLabelS2, ToolStripStatusLabelS3,
                                                ToolStripStatusLabelS4, ToolStripStatusLabelS5, ToolStripStatusLabelS6}
        For i As Integer = 0 To lights.Length - 1
            lights(i).AutoSize = True
            lights(i).Text = ChrW(&H25CF) & " " & UiLightInfo(i, 0)
            lights(i).ToolTipText = UiLightInfo(i, 1)
        Next

        Dim openCatalog As New ToolStripMenuItem("Open LTFSCatalog (search all tapes)") With {.ShortcutKeys = Keys.Control Or Keys.Shift Or Keys.F}
        AddHandler openCatalog.Click, Sub() UiStyle.OpenCompanion(Me, "LTFSCatalog.exe")
        磁带ToolStripMenuItem.DropDownItems.Add(New ToolStripSeparator())
        磁带ToolStripMenuItem.DropDownItems.Add(openCatalog)

        ' Help sits at the right end: the command bar occupies the menu row after Tape and Data
        Dim keysItem As New ToolStripMenuItem("Keyboard shortcuts") With {.ShortcutKeys = Keys.F1}
        AddHandler keysItem.Click, Sub() UiStyle.ShowShortcuts(Me, "Keyboard shortcuts - LTFSCopyGUI", UiShortcuts)
        Dim help As New ToolStripMenuItem("&Help") With {.Alignment = ToolStripItemAlignment.Right}
        help.DropDownItems.Add(keysItem)
        MenuStrip1.Items.Add(help)
    End Sub

End Class
