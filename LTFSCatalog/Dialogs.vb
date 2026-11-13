Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Forms

''' <summary>LTFSCopyGUI's look: its form font, DPI scaling, icon and tree/list images.</summary>
Friend Module Theme

    Public ReadOnly UiFont As Font = MakeFont()
    Private _icon As Icon
    Private _images As ImageList

    Public Const ImgTape As Integer = 0   ' DirRoot.png - same order as LTFSCopyGUI's ImageList1
    Public Const ImgDir As Integer = 1
    Public Const ImgFile As Integer = 2
    Public Const ImgArchive As Integer = 3

    Private Function MakeFont() As Font
        ' LTFSCopyGUI's writer window uses SimSun-ExtB 9pt; GDI substitutes silently when it is missing.
        Dim f As New Font("SimSun-ExtB", 9.0F, FontStyle.Regular, GraphicsUnit.Point)
        If String.Equals(f.Name, "SimSun-ExtB", StringComparison.OrdinalIgnoreCase) Then Return f
        f.Dispose()
        Return New Font(SystemFonts.MessageBoxFont.FontFamily, 9.0F)
    End Function

    Public ReadOnly Property AppIcon As Icon
        Get
            If _icon Is Nothing Then
                Try
                    _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                Catch
                    _icon = SystemIcons.Application
                End Try
            End If
            Return _icon
        End Get
    End Property

    Public Function Images() As ImageList
        If _images Is Nothing Then
            _images = New ImageList With {.ColorDepth = ColorDepth.Depth32Bit, .ImageSize = New Size(16, 16), .TransparentColor = Color.Transparent}
            For Each n As String In {"DirRoot", "Dir", "File", "Archive"}
                Using s = Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("LTFSCatalog." & n & ".png")
                    If s IsNot Nothing Then
                        _images.Images.Add(n, Image.FromStream(s))
                    Else
                        _images.Images.Add(n, New Bitmap(16, 16))
                    End If
                End Using
            Next
        End If
        Return _images
    End Function

    Public Sub ApplyForm(f As Form)
        f.Font = UiFont
        f.AutoScaleDimensions = New SizeF(96.0F, 96.0F)
        f.AutoScaleMode = AutoScaleMode.Dpi
        f.Icon = AppIcon
    End Sub

    Public Sub ApplyDialog(f As Form, title As String)
        ApplyForm(f)
        f.Text = title
        f.FormBorderStyle = FormBorderStyle.FixedDialog
        f.MaximizeBox = False
        f.MinimizeBox = False
        f.ShowInTaskbar = False
        f.StartPosition = FormStartPosition.CenterParent
    End Sub

    ''' <summary>A message with any buttons; returns the index of the button pressed (Esc/close = cancelIndex).</summary>
    Public Function Ask(owner As IWin32Window, title As String, heading As String, details As String,
                        buttons As String(), defaultIndex As Integer, cancelIndex As Integer) As Integer
        Using f As New Form()
            ApplyDialog(f, title)
            Dim result As Integer = cancelIndex
            Dim layout As New TableLayoutPanel With {.Location = New Point(0, 0), .ColumnCount = 1, .RowCount = 3, .Padding = New Padding(14, 12, 14, 10),
                                                     .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink}
            layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            Dim h As New Label With {.Text = heading, .AutoSize = True, .MaximumSize = New Size(460, 0), .Font = New Font(UiFont, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 8)}
            Dim d As New Label With {.Text = details, .AutoSize = True, .MaximumSize = New Size(460, 0), .Margin = New Padding(0, 0, 0, 12)}
            Dim row As New FlowLayoutPanel With {.FlowDirection = FlowDirection.RightToLeft, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                                 .Anchor = AnchorStyles.Right, .WrapContents = False, .Margin = New Padding(0)}
            ' RightToLeft flow: adding the last button first leaves buttons(0) at the left.
            For i As Integer = buttons.Length - 1 To 0 Step -1
                Dim idx As Integer = i
                Dim b As New Button With {.Text = buttons(i), .AutoSize = True, .MinimumSize = New Size(84, 26)}
                AddHandler b.Click, Sub()
                                        result = idx
                                        f.Close()
                                    End Sub
                row.Controls.Add(b)
                If i = defaultIndex Then f.AcceptButton = b
            Next
            layout.Controls.Add(h, 0, 0)
            layout.Controls.Add(d, 0, 1)
            layout.Controls.Add(row, 0, 2)
            f.Controls.Add(layout)
            f.AutoSize = True
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink
            f.KeyPreview = True
            AddHandler f.KeyDown, Sub(s, e)
                                      If e.KeyCode = Keys.Escape Then
                                          result = cancelIndex
                                          f.Close()
                                      End If
                                  End Sub
            AddHandler f.Shown, Sub()
                                    If f.AcceptButton IsNot Nothing Then DirectCast(f.AcceptButton, Button).Focus()
                                End Sub
            f.ShowDialog(owner)
            Return result
        End Using
    End Function

    Public Function DescribeIndex(info As IndexFileInfo) As String
        Dim lines As New List(Of String)
        If Not String.IsNullOrEmpty(info.Serial) Then lines.Add("Cartridge serial: " & info.Serial)
        If Not String.IsNullOrEmpty(info.Format) Then lines.Add("Format: " & info.Format & If(String.IsNullOrEmpty(info.Vendor), "", " (" & info.Vendor & ")"))
        If info.UpdateTime.HasValue Then lines.Add("Index written: " & FormatTime(info.UpdateTime.Value))
        lines.Add("Index file: " & FormatSize(info.FileSize) & If(info.IsBlank, " (blank tape)", ""))
        Return String.Join(Environment.NewLine, lines)
    End Function

End Module

''' <summary>Several new tapes at once: tick the ones to track; the rest are asked again later or never.</summary>
Friend NotInheritable Class NewTapesDialog
    Inherits Form

    Private ReadOnly _list As New ListView With {.View = View.Details, .CheckBoxes = True, .FullRowSelect = True, .Dock = DockStyle.Fill, .HideSelection = False}
    Private ReadOnly _never As New RadioButton With {.Text = "Never ask about the unticked tapes", .AutoSize = True}
    Private ReadOnly _later As New RadioButton With {.Text = "Ask about the unticked tapes next time", .AutoSize = True, .Checked = True}
    Private ReadOnly _infos As List(Of IndexFileInfo)

    Public ReadOnly Property Tracked As List(Of IndexFileInfo)
        Get
            Return _list.Items.Cast(Of ListViewItem)().Where(Function(i) i.Checked).Select(Function(i) DirectCast(i.Tag, IndexFileInfo)).ToList()
        End Get
    End Property

    Public ReadOnly Property NeverForUnticked As Boolean
        Get
            Return _never.Checked
        End Get
    End Property

    Public Sub New(infos As List(Of IndexFileInfo))
        _infos = infos
        Theme.ApplyDialog(Me, "New tapes")
        FormBorderStyle = FormBorderStyle.Sizable
        MinimumSize = New Size(520, 300)
        ClientSize = New Size(640, 360)
        Dim head As New Label With {.Text = "LTFSCopyGUI has indexes for tapes the catalog does not track yet. Tick the tapes to add.",
                                    .Dock = DockStyle.Top, .AutoSize = False, .Height = 34, .Padding = New Padding(8, 8, 8, 0)}
        _list.Columns.Add("Tape", 110)
        _list.Columns.Add("Serial", 110)
        _list.Columns.Add("Format", 70)
        _list.Columns.Add("Index written", 140)
        _list.Columns.Add("Index size", 90, HorizontalAlignment.Right)
        For Each info As IndexFileInfo In infos
            Dim it As New ListViewItem(info.Barcode) With {.Checked = True, .Tag = info, .ImageIndex = Theme.ImgTape}
            it.SubItems.Add(info.Serial)
            it.SubItems.Add(info.Format)
            it.SubItems.Add(If(info.UpdateTime.HasValue, FormatTime(info.UpdateTime.Value), ""))
            it.SubItems.Add(FormatSize(info.FileSize))
            _list.Items.Add(it)
        Next
        _list.SmallImageList = Theme.Images()
        Dim bottom As New Panel With {.Dock = DockStyle.Bottom, .Height = 86, .Padding = New Padding(8)}
        _later.Location = New Point(10, 6)
        _never.Location = New Point(10, 28)
        Dim ok As New Button With {.Text = "Add ticked", .DialogResult = DialogResult.OK, .Size = New Size(96, 26), .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom}
        Dim cancel As New Button With {.Text = "Not now", .DialogResult = DialogResult.Cancel, .Size = New Size(96, 26), .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom}
        ok.Location = New Point(bottom.Width - 210, 52)
        cancel.Location = New Point(bottom.Width - 108, 52)
        bottom.Controls.AddRange(New Control() {_later, _never, ok, cancel})
        AddHandler bottom.Resize, Sub()
                                      ok.Left = bottom.ClientSize.Width - 210
                                      cancel.Left = bottom.ClientSize.Width - 108
                                  End Sub
        Controls.Add(_list)
        Controls.Add(bottom)
        Controls.Add(head)
        AcceptButton = ok
        CancelButton = cancel
    End Sub

End Class

''' <summary>Search syntax with examples; double-click an example to search for it.</summary>
Friend NotInheritable Class SearchHelpForm
    Inherits Form

    Public Event ExampleChosen(text As String)

    Private Shared ReadOnly Examples As String(,) = {
        {"report 2024", "names containing both ""report"" and ""2024"", in any order"},
        {"""annual report""", "names containing the exact phrase, spaces included"},
        {"invoice|receipt", "names containing either word"},
        {"budget -draft", "names containing ""budget"" but not ""draft"""},
        {"*.mkv", "names matching a wildcard (whole name): * any text, ? one character"},
        {"IMG_20??*", "names starting IMG_20, two characters, then anything"},
        {"ext:pdf,docx", "files with these extensions"},
        {"size:>1GB", "larger than 1 GiB (also <, >=, <=, and ranges like size:10MB..2GB)"},
        {"modified:2024-06", "modified in June 2024 (a year, month or day; > and < work too)"},
        {"modified:lastmonth", "today, yesterday, thisweek, lastweek, thismonth, lastmonth, thisyear, lastyear"},
        {"created:2023..2024-03", "created between the start of 2023 and the end of March 2024"},
        {"tape:TEST01", "only on tapes whose barcode contains TEST01"},
        {"path:""Bike development""", "only inside folders whose path contains this text"},
        {"type:folder backup", "folders only (type:file for files only)"},
        {"ёлка", "case and accents are ignored; ё and е match each other"}}

    Public Sub New()
        Theme.ApplyForm(Me)
        Text = "Search syntax"
        FormBorderStyle = FormBorderStyle.SizableToolWindow
        StartPosition = FormStartPosition.CenterParent
        ShowInTaskbar = False
        ClientSize = New Size(720, 420)
        Dim head As New Label With {
            .Text = "Words are matched anywhere in the name, all of them, in any order. Combine words and filters freely, " &
                    "e.g.  ext:jpg 2019 -thumb size:>2MB" & Environment.NewLine &
                    "Double-click an example to try it. Enter searches at once; Esc clears the search.",
            .Dock = DockStyle.Top, .Height = 46, .Padding = New Padding(8, 8, 8, 0)}
        Dim list As New ListView With {.View = View.Details, .FullRowSelect = True, .Dock = DockStyle.Fill, .HideSelection = False}
        list.Columns.Add("Type this", 200)
        list.Columns.Add("To find", 490)
        For i As Integer = 0 To Examples.GetLength(0) - 1
            Dim it As New ListViewItem(Examples(i, 0))
            it.SubItems.Add(Examples(i, 1))
            list.Items.Add(it)
        Next
        AddHandler list.ItemActivate, Sub()
                                          If list.SelectedItems.Count > 0 Then RaiseEvent ExampleChosen(list.SelectedItems(0).Text)
                                      End Sub
        Controls.Add(list)
        Controls.Add(head)
    End Sub

End Class

''' <summary>Tapes the catalog does not track (never, or not decided): tick to track them.</summary>
Friend NotInheritable Class UntrackedTapesDialog
    Inherits Form

    Private ReadOnly _list As New ListView With {.View = View.Details, .CheckBoxes = True, .FullRowSelect = True, .Dock = DockStyle.Fill}

    Public ReadOnly Property ToTrack As List(Of TapeInfo)
        Get
            Return _list.Items.Cast(Of ListViewItem)().Where(Function(i) i.Checked).Select(Function(i) DirectCast(i.Tag, TapeInfo)).ToList()
        End Get
    End Property

    Public Sub New(tapes As List(Of TapeInfo))
        Theme.ApplyDialog(Me, "Untracked tapes")
        FormBorderStyle = FormBorderStyle.Sizable
        ClientSize = New Size(560, 320)
        Dim head As New Label With {.Text = If(tapes.Count = 0, "Every tape LTFSCopyGUI has an index for is tracked.", "Tick the tapes to add to the catalog."),
                                    .Dock = DockStyle.Top, .Height = 28, .Padding = New Padding(8, 8, 8, 0)}
        _list.Columns.Add("Tape", 120)
        _list.Columns.Add("Serial", 120)
        _list.Columns.Add("Status", 120)
        _list.Columns.Add("First seen", 150)
        For Each t As TapeInfo In tapes
            Dim it As New ListViewItem(t.Barcode) With {.Tag = t, .ImageIndex = Theme.ImgTape}
            it.SubItems.Add(t.Serial)
            it.SubItems.Add(If(t.State = TapeInfo.StateNever, "Never track", "Not decided"))
            it.SubItems.Add(If(t.FirstSeen.HasValue, FormatTime(t.FirstSeen.Value), ""))
            _list.Items.Add(it)
        Next
        _list.SmallImageList = Theme.Images()
        Dim bottom As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .FlowDirection = FlowDirection.RightToLeft, .Height = 40, .Padding = New Padding(6)}
        Dim cancel As New Button With {.Text = "Close", .DialogResult = DialogResult.Cancel, .Size = New Size(96, 26)}
        Dim ok As New Button With {.Text = "Track ticked", .DialogResult = DialogResult.OK, .Size = New Size(110, 26), .Enabled = tapes.Count > 0}
        bottom.Controls.Add(cancel)
        bottom.Controls.Add(ok)
        Controls.Add(_list)
        Controls.Add(bottom)
        Controls.Add(head)
        AcceptButton = ok
        CancelButton = cancel
    End Sub

End Class
