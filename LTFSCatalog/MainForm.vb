Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

''' <summary>The catalog window: tapes and folders on the left, files or search results on the right.</summary>
Friend NotInheritable Class MainForm
    Inherits Form

    Private NotInheritable Class NodeTag
        Public TapeId As Long
        Public DirId As Long?
        Public Loaded As Boolean
    End Class

    Private Enum ViewMode
        Browse
        Search
    End Enum

    Private ReadOnly _opts As AppOptions
    Private _store As CatalogStore
    Private _engine As SearchEngine
    Private _tapes As New Dictionary(Of Long, TapeInfo)

    Private ReadOnly _menu As New MenuStrip()
    Private ReadOnly _bar As New ToolStrip()
    Private ReadOnly _txt As New ToolStripTextBox()
    Private ReadOnly _cboTape As New ToolStripComboBox()
    Private ReadOnly _cboKind As New ToolStripComboBox()
    Private ReadOnly _btnPath As New ToolStripButton()
    Private ReadOnly _btnSearch As New ToolStripButton()
    Private ReadOnly _btnHelp As New ToolStripButton()
    Private ReadOnly _split1 As New SplitContainer()
    Private ReadOnly _split2 As New SplitContainer()
    Private ReadOnly _tree As New TreeView()
    Private ReadOnly _list As New ListView()
    Private ReadOnly _details As New ListView()
    Private ReadOnly _status As New StatusStrip()
    Private ReadOnly _lblTotals As New ToolStripStatusLabel()
    Private ReadOnly _lblView As New ToolStripStatusLabel()
    Private ReadOnly _lblActivity As New ToolStripStatusLabel()
    Private ReadOnly _lblFolder As New ToolStripStatusLabel()
    Private ReadOnly _listMenu As New ContextMenuStrip()
    Private ReadOnly _tapeMenu As New ContextMenuStrip()
    Private ReadOnly _mnuTape As New ToolStripMenuItem("&Tape")
    Private ReadOnly _debounce As New System.Windows.Forms.Timer With {.Interval = 300}
    Private ReadOnly _watchTimer As New System.Windows.Forms.Timer With {.Interval = 5000}
    Private _watcher As FileSystemWatcher
    Private _help As SearchHelpForm

    Private _rows As New List(Of EntryRow)
    Private _mode As ViewMode = ViewMode.Browse
    Private _browseTape As Long = -1
    Private _browseDir As Long?
    Private _sort As SortColumn = SortColumn.Name
    Private _desc As Boolean
    Private _lastCapped As Boolean
    Private _searchCts As CancellationTokenSource
    Private _searchSeq As Integer
    Private _syncing As Boolean
    Private _syncAgain As Boolean
    Private _closing As Boolean
    Private ReadOnly _closeCts As New CancellationTokenSource()
    Private ReadOnly _asked As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _history As New List(Of String)
    Private ReadOnly _comboTapeIds As New List(Of Long?)
    Private _treeBusy As Boolean

    Private Shared ReadOnly ColumnTitles As String() = {"Name", "Size", "Modified", "Tape", "Folder"}
    Private Shared ReadOnly ColumnSorts As SortColumn() = {SortColumn.Name, SortColumn.Size, SortColumn.Modified, SortColumn.Tape, SortColumn.Folder}

    <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As String) As IntPtr
    End Function

    Public Sub New(opts As AppOptions)
        _opts = opts
        SuspendLayout()
        Theme.ApplyForm(Me)
        Text = "LTFSCatalog"
        ClientSize = New Size(1264, 661)
        KeyPreview = True
        BuildMenu()
        BuildSearchBar()
        BuildViews()
        BuildStatus()
        Controls.Add(_split1)
        Controls.Add(_bar)
        Controls.Add(_status)
        Controls.Add(_menu)
        MainMenuStrip = _menu
        ResumeLayout(False)
        PerformLayout()
        AddHandler _debounce.Tick, Sub()
                                       _debounce.Stop()
                                       RunSearch(False)
                                   End Sub
        AddHandler _watchTimer.Tick, Sub()
                                         _watchTimer.Stop()
                                         StartSync()
                                     End Sub
    End Sub

    ' ---- building the window ---------------------------------------------------------------------
    Private Shared Function Item(text As String, onClick As Action, Optional keys As String = Nothing) As ToolStripMenuItem
        Dim m As New ToolStripMenuItem(text)
        If keys IsNot Nothing Then m.ShortcutKeyDisplayString = keys
        AddHandler m.Click, Sub() onClick()
        Return m
    End Function

    Private Sub BuildMenu()
        Dim mCat As New ToolStripMenuItem("&Catalog")
        mCat.DropDownItems.Add(Item("&Refresh from index folder", AddressOf StartSync, "F5"))
        mCat.DropDownItems.Add(Item("&Import index file...", AddressOf DoImportFile))
        mCat.DropDownItems.Add(Item("Index &folder...", AddressOf DoChooseFolder))
        mCat.DropDownItems.Add(Item("&Untracked tapes...", AddressOf DoUntracked))
        mCat.DropDownItems.Add(New ToolStripSeparator())
        mCat.DropDownItems.Add(Item("E&xit", AddressOf Close))
        _mnuTape.DropDownItems.Add(Item("&Re-import from its index", Sub() DoReimport(SelectedTapeId())))
        _mnuTape.DropDownItems.Add(Item("Re&move from catalog...", Sub() DoRemoveTape(SelectedTapeId())))
        _mnuTape.DropDownItems.Add(New ToolStripSeparator())
        _mnuTape.DropDownItems.Add(Item("&Search on this tape", Sub() SearchOnTape(SelectedTapeId())))
        AddHandler _mnuTape.DropDownOpening, Sub()
                                                 Dim has As Boolean = SelectedTapeId().HasValue
                                                 For Each it As ToolStripItem In _mnuTape.DropDownItems
                                                     it.Enabled = has
                                                 Next
                                             End Sub
        Dim mHelp As New ToolStripMenuItem("&Help")
        mHelp.DropDownItems.Add(Item("&Search syntax", AddressOf ShowSearchHelp, "F1"))
        mHelp.DropDownItems.Add(Item("&About", AddressOf ShowAbout))
        _menu.Items.AddRange(New ToolStripItem() {mCat, _mnuTape, mHelp})

        _tapeMenu.Items.Add(Item("Re-import from its index", Sub() DoReimport(SelectedTapeId())))
        _tapeMenu.Items.Add(Item("Remove from catalog...", Sub() DoRemoveTape(SelectedTapeId())))
        _tapeMenu.Items.Add(New ToolStripSeparator())
        _tapeMenu.Items.Add(Item("Search on this tape", Sub() SearchOnTape(SelectedTapeId())))
    End Sub

    Private Sub BuildSearchBar()
        _bar.GripStyle = ToolStripGripStyle.Hidden
        _bar.Padding = New Padding(4, 2, 4, 2)
        _txt.AutoSize = False
        _txt.Width = 420
        _txt.AutoCompleteMode = AutoCompleteMode.SuggestAppend
        _txt.AutoCompleteSource = AutoCompleteSource.CustomSource
        _txt.ToolTipText = "Search every tracked tape. F1 shows the syntax."
        _cboTape.DropDownStyle = ComboBoxStyle.DropDownList
        _cboTape.AutoSize = False
        _cboTape.Width = 130
        _cboKind.DropDownStyle = ComboBoxStyle.DropDownList
        _cboKind.AutoSize = False
        _cboKind.Width = 120
        _cboKind.Items.AddRange(New Object() {"Files and folders", "Files", "Folders"})
        _cboKind.SelectedIndex = 0
        _btnPath.Text = "Match path"
        _btnPath.CheckOnClick = True
        _btnPath.DisplayStyle = ToolStripItemDisplayStyle.Text
        _btnPath.ToolTipText = "Words may also match the names of the folders a file is in"
        _btnSearch.Text = "Search"
        _btnSearch.DisplayStyle = ToolStripItemDisplayStyle.Text
        _btnHelp.Text = "?"
        _btnHelp.DisplayStyle = ToolStripItemDisplayStyle.Text
        _btnHelp.ToolTipText = "Search syntax (F1)"
        _bar.Items.AddRange(New ToolStripItem() {New ToolStripLabel("Search:"), _txt, New ToolStripSeparator(),
                            New ToolStripLabel("Tape:"), _cboTape, New ToolStripLabel("Show:"), _cboKind,
                            New ToolStripSeparator(), _btnPath, _btnSearch, _btnHelp})
        AddHandler _bar.Resize, Sub() LayoutSearchBar()
        AddHandler _txt.TextChanged, AddressOf OnSearchTextChanged
        AddHandler _txt.KeyDown, AddressOf OnSearchKeyDown
        AddHandler _cboTape.SelectedIndexChanged, Sub() OnFilterChanged()
        AddHandler _cboKind.SelectedIndexChanged, Sub() OnFilterChanged()
        AddHandler _btnPath.CheckedChanged, Sub() OnFilterChanged()
        AddHandler _btnSearch.Click, Sub() RunSearch(True)
        AddHandler _btnHelp.Click, Sub() ShowSearchHelp()
    End Sub

    Private Sub LayoutSearchBar()
        Dim others As Integer = 0
        For Each it As ToolStripItem In _bar.Items
            If it IsNot _txt AndAlso it.Available Then others += it.Width + it.Margin.Horizontal
        Next
        Dim w As Integer = _bar.ClientSize.Width - _bar.Padding.Horizontal - others - _txt.Margin.Horizontal - 8
        _txt.Width = Math.Max(160, w)
    End Sub

    Private Sub BuildViews()
        _split1.Dock = DockStyle.Fill
        _split1.FixedPanel = FixedPanel.Panel1
        _split2.Dock = DockStyle.Fill
        _split2.Orientation = Orientation.Horizontal
        _split2.FixedPanel = FixedPanel.Panel2

        _tree.Dock = DockStyle.Fill
        _tree.HideSelection = False
        _tree.ImageList = Theme.Images()
        _tree.ImageIndex = Theme.ImgDir
        _tree.SelectedImageIndex = Theme.ImgDir
        AddHandler _tree.BeforeExpand, Sub(s, e) EnsureChildrenLoaded(e.Node)
        AddHandler _tree.AfterSelect, AddressOf OnTreeSelect
        AddHandler _tree.NodeMouseClick, Sub(s, e)
                                             If e.Button = MouseButtons.Right Then
                                                 _tree.SelectedNode = e.Node
                                                 If e.Node IsNot Nothing AndAlso e.Node.Parent Is Nothing Then _tapeMenu.Show(_tree, e.Location)
                                             End If
                                         End Sub

        _list.Dock = DockStyle.Fill
        _list.View = View.Details
        _list.VirtualMode = True
        _list.FullRowSelect = True
        _list.HideSelection = False
        _list.MultiSelect = True
        _list.SmallImageList = Theme.Images()
        _list.Columns.Add(ColumnTitles(0), 320)
        _list.Columns.Add(ColumnTitles(1), 100, HorizontalAlignment.Right)
        _list.Columns.Add(ColumnTitles(2), 140)
        _list.Columns.Add(ColumnTitles(3), 90)
        _list.Columns.Add(ColumnTitles(4), 480)
        AddHandler _list.RetrieveVirtualItem, AddressOf OnRetrieveItem
        AddHandler _list.CacheVirtualItems, AddressOf OnCacheItems
        AddHandler _list.ColumnClick, AddressOf OnColumnClick
        AddHandler _list.ItemActivate, Sub() OnActivate()
        AddHandler _list.SelectedIndexChanged, Sub() OnListSelection()
        AddHandler _list.VirtualItemsSelectionRangeChanged, Sub() OnListSelection()
        AddHandler _list.KeyDown, AddressOf OnListKeyDown
        AddHandler _list.MouseUp, Sub(s, e)
                                      If e.Button = MouseButtons.Right AndAlso _list.SelectedIndices.Count > 0 Then _listMenu.Show(_list, e.Location)
                                  End Sub
        _listMenu.Items.Add(Item("Open folder", Sub() OnActivate()))
        _listMenu.Items.Add(Item("Copy full path", Sub() CopySelected(True), "Ctrl+C"))
        _listMenu.Items.Add(Item("Copy name", Sub() CopySelected(False)))
        _listMenu.Items.Add(New ToolStripSeparator())
        _listMenu.Items.Add(Item("Search in this folder", Sub() SearchInSelectedFolder()))
        AddHandler _listMenu.Opening, Sub()
                                          Dim r As EntryRow = FocusedRow()
                                          _listMenu.Items(0).Text = If(r IsNot Nothing AndAlso r.IsDir, "Open folder", "Open containing folder")
                                          _listMenu.Items(4).Enabled = r IsNot Nothing AndAlso r.IsDir
                                      End Sub

        _details.Dock = DockStyle.Fill
        _details.View = View.Details
        _details.FullRowSelect = True
        _details.HeaderStyle = ColumnHeaderStyle.Nonclickable
        _details.Columns.Add("Property", 160)
        _details.Columns.Add("Value", 700)
        AddHandler _details.KeyDown, Sub(s, e)
                                         If e.Control AndAlso e.KeyCode = Keys.C AndAlso _details.SelectedItems.Count > 0 Then
                                             Clipboard.SetText(String.Join(Environment.NewLine, _details.SelectedItems.Cast(Of ListViewItem)().Select(Function(i) i.SubItems(1).Text)))
                                         End If
                                     End Sub

        _split1.Panel1.Controls.Add(_tree)
        _split1.Panel2.Controls.Add(_split2)
        _split2.Panel1.Controls.Add(_list)
        _split2.Panel2.Controls.Add(_details)
    End Sub

    Private Sub BuildStatus()
        _lblTotals.BorderSides = ToolStripStatusLabelBorderSides.Right
        _lblView.BorderSides = ToolStripStatusLabelBorderSides.Right
        _lblActivity.Spring = True
        _lblActivity.TextAlign = ContentAlignment.MiddleLeft
        _lblFolder.BorderSides = ToolStripStatusLabelBorderSides.Left
        _status.Items.AddRange(New ToolStripItem() {_lblTotals, _lblView, _lblActivity, _lblFolder})
    End Sub

    ' ---- lifetime -------------------------------------------------------------------------------
    Protected Overrides Sub OnLoad(e As EventArgs)
        MyBase.OnLoad(e)
        Try
            _store = New CatalogStore(_opts.DbPath)
            _engine = New SearchEngine(_store)
        Catch ex As Exception
            LogError("Cannot open the catalog", ex)
            MessageBox.Show(Me, "The catalog cannot be opened:" & Environment.NewLine & ex.Message & Environment.NewLine & Environment.NewLine & _opts.DbPath,
                            "LTFSCatalog", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Close()
            Return
        End Try
        RestoreLayout()
        Dim h As String = _store.GetMeta("history")
        If Not String.IsNullOrEmpty(h) Then _history.AddRange(h.Split(New Char() {ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries))
        RefreshHistory()
        RefreshTapes(Nothing)
        UpdateFolderLabel()
        RestartWatcher()
        If Not _store.FtsAvailable Then SetActivity("Search index unavailable in this SQLite build - searching by scan (slower)")
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        If _store Is Nothing Then Return
        LayoutSearchBar()
        SendMessage(_txt.TextBox.Handle, &H1501, New IntPtr(1), "Search all tapes: words, *.ext, ext:pdf, size:>1GB, modified:2024 ...  (F1 for help)")
        _txt.Focus()
        StartSync()
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        _closing = True
        _closeCts.Cancel()
        _searchCts?.Cancel()
        _debounce.Stop()
        _watchTimer.Stop()
        If _watcher IsNot Nothing Then _watcher.Dispose()
        If _store IsNot Nothing Then
            Try
                SaveLayout()
                _store.SetMeta("history", String.Join(ControlChars.Lf, _history))
            Catch ex As Exception
                LogError("Saving window settings", ex)
            End Try
            _engine?.Dispose()
            _store.Dispose()
        End If
        MyBase.OnFormClosing(e)
    End Sub

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        Select Case keyData
            Case Keys.Control Or Keys.F, Keys.F3
                _txt.Focus()
                _txt.SelectAll()
                Return True
            Case Keys.F1
                ShowSearchHelp()
                Return True
            Case Keys.F5
                StartSync()
                Return True
        End Select
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    Private Sub RestoreLayout()
        Dim parts As String() = If(_store.GetMeta("layout"), "").Split(","c)
        Dim v(6) As Integer
        If parts.Length = 7 AndAlso Enumerable.Range(0, 7).All(Function(i) Integer.TryParse(parts(i), NumberStyles.Integer, CultureInfo.InvariantCulture, v(i))) Then
            Dim r As New Rectangle(v(0), v(1), v(2), v(3))
            If Screen.AllScreens.Any(Function(s) s.WorkingArea.IntersectsWith(r)) AndAlso r.Width > 300 AndAlso r.Height > 200 Then
                StartPosition = FormStartPosition.Manual
                Bounds = r
                If v(4) = 1 Then WindowState = FormWindowState.Maximized
            End If
            SetSplit(_split1, v(5))
            SetSplit(_split2, v(6))
        Else
            SetSplit(_split1, 236)
            SetSplit(_split2, Math.Max(120, _split2.Height - 200))
        End If
    End Sub

    Private Shared Sub SetSplit(s As SplitContainer, distance As Integer)
        Try
            Dim max As Integer = If(s.Orientation = Orientation.Vertical, s.Width, s.Height) - s.Panel2MinSize - s.SplitterWidth
            If max > s.Panel1MinSize Then s.SplitterDistance = Math.Max(s.Panel1MinSize, Math.Min(distance, max))
        Catch ex As InvalidOperationException
        End Try
    End Sub

    Private Sub SaveLayout()
        Dim b As Rectangle = If(WindowState = FormWindowState.Normal, Bounds, RestoreBounds)
        _store.SetMeta("layout", String.Join(",", New Integer() {b.X, b.Y, b.Width, b.Height, If(WindowState = FormWindowState.Maximized, 1, 0),
                                                                  _split1.SplitterDistance, _split2.SplitterDistance}))
    End Sub

    ' ---- index folder, watcher, refresh -----------------------------------------------------------
    Private ReadOnly Property IndexFolder As String
        Get
            If Not String.IsNullOrEmpty(_opts.IndexFolder) Then Return _opts.IndexFolder
            Dim m As String = _store.GetMeta("index_folder")
            Return If(String.IsNullOrEmpty(m), Path.Combine(AppDir, "schema"), m)
        End Get
    End Property

    Private Sub UpdateFolderLabel()
        Dim f As String = IndexFolder
        _lblFolder.Text = "Index folder: " & f & If(Directory.Exists(f), "", " (not found)")
    End Sub

    Private Sub RestartWatcher()
        If _watcher IsNot Nothing Then
            _watcher.Dispose()
            _watcher = Nothing
        End If
        Dim f As String = IndexFolder
        If Not Directory.Exists(f) Then Return
        Try
            _watcher = New FileSystemWatcher(f) With {.IncludeSubdirectories = True,
                .NotifyFilter = NotifyFilters.FileName Or NotifyFilters.DirectoryName Or NotifyFilters.LastWrite Or NotifyFilters.Size}
            AddHandler _watcher.Created, AddressOf OnFolderChanged
            AddHandler _watcher.Changed, AddressOf OnFolderChanged
            AddHandler _watcher.Renamed, AddressOf OnFolderChanged
            AddHandler _watcher.Deleted, AddressOf OnFolderChanged
            _watcher.EnableRaisingEvents = True
        Catch ex As Exception
            LogError("Cannot watch the index folder", ex)
        End Try
    End Sub

    Private Sub OnFolderChanged(sender As Object, e As FileSystemEventArgs)
        If _closing OrElse Not IsHandleCreated Then Return
        Try
            BeginInvoke(New Action(Sub()
                                       _watchTimer.Stop()
                                       _watchTimer.Start()
                                   End Sub))
        Catch ex As InvalidOperationException
        End Try
    End Sub

    Private Sub SetActivity(text As String)
        If InvokeRequired Then
            Try
                BeginInvoke(New Action(Of String)(AddressOf SetActivity), text)
            Catch ex As InvalidOperationException
            End Try
            Return
        End If
        _lblActivity.Text = text
    End Sub

    Private Shared Function TapeKey(info As IndexFileInfo) As String
        Return info.Barcode & "|" & info.Serial
    End Function

    ''' <summary>Reads the index folder, asks about new tapes, imports what changed. Never runs twice at once.</summary>
    Private Async Sub StartSync()
        If _store Is Nothing OrElse _closing Then Return
        If _syncing Then
            _syncAgain = True
            Return
        End If
        _syncing = True
        Dim failures As New List(Of String)
        Try
            Do
                _syncAgain = False
                Dim folder As String = IndexFolder
                SetActivity("Checking " & folder & " ...")
                Dim store As CatalogStore = _store
                Dim plan As ScanPlan = Await Task.Run(Function() IndexScanner.Plan(store, folder))
                If _closing Then Return
                For Each er As String In plan.Errors
                    Log(er)
                Next
                Dim work As New List(Of Tuple(Of Long, IndexFileInfo, Boolean))
                Dim ask As List(Of IndexFileInfo) = plan.NewTapes.Where(Function(i) Not _asked.Contains(TapeKey(i))).ToList()
                If ask.Count > 0 Then
                    For Each i As IndexFileInfo In ask
                        _asked.Add(TapeKey(i))
                    Next
                    For Each a As Tuple(Of Long, IndexFileInfo) In AskNewTapes(ask)
                        work.Add(Tuple.Create(a.Item1, a.Item2, False))
                    Next
                End If
                For Each u As Tuple(Of TapeInfo, IndexFileInfo) In plan.Updates
                    work.Add(Tuple.Create(u.Item1.Id, u.Item2, False))
                Next
                For Each rb As Tuple(Of TapeInfo, IndexFileInfo) In plan.ReformattedBlank
                    Dim choice As Integer = Theme.Ask(Me, "Tape reformatted", rb.Item1.Barcode & " was reformatted.",
                        "Its new index has no files. Keep it in the catalog as a blank tape, or remove it from the view?" & Environment.NewLine &
                        "Either way its old file list is cleared; the next write fills it in again.",
                        New String() {"Keep as blank tape", "Remove"}, 0, 0)
                    work.Add(Tuple.Create(rb.Item1.Id, rb.Item2, choice = 1))
                Next
                For Each w As Tuple(Of Long, IndexFileInfo, Boolean) In work
                    If _closing Then Return
                    Dim info As IndexFileInfo = w.Item2
                    Dim tapeId As Long = w.Item1
                    SetActivity("Importing " & info.Label & " ...")
                    Try
                        Dim ct As CancellationToken = _closeCts.Token
                        Await Task.Run(Sub() IndexImporter.Run(store, info, tapeId, AddressOf SetActivity, ct))
                        If w.Item3 Then _store.SetTapeHidden(tapeId, True)
                    Catch ex As OperationCanceledException
                        Return
                    Catch ex As Exception
                        LogError("Import of " & info.Path & " failed", ex)
                        failures.Add(info.Barcode)
                        _store.SetTapeError(tapeId, ex.Message)
                    End Try
                    If _closing Then Return
                    RefreshTapes(New Long() {tapeId})
                Next
            Loop While _syncAgain AndAlso Not _closing
            If failures.Count > 0 Then
                SetActivity("Import failed for " & String.Join(", ", failures) & " - see catalog\LTFSCatalog.log; it is retried on the next refresh")
            Else
                SetActivity("Up to date (" & DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) & ")")
            End If
        Catch ex As Exception
            LogError("Refresh failed", ex)
            SetActivity("Refresh failed: " & ex.Message)
        Finally
            _syncing = False
        End Try
    End Sub

    ''' <summary>Yes/No/Never for one tape, a list for several. Returns the tapes to import now.</summary>
    Private Function AskNewTapes(infos As List(Of IndexFileInfo)) As List(Of Tuple(Of Long, IndexFileInfo))
        Dim result As New List(Of Tuple(Of Long, IndexFileInfo))
        If infos.Count = 1 Then
            Dim i As IndexFileInfo = infos(0)
            Select Case Theme.Ask(Me, "New tape", "Track " & i.Barcode & " in the catalog?", DescribeIndex(i),
                                  New String() {"Yes", "No", "Never"}, 0, 1)
                Case 0 : result.Add(Tuple.Create(_store.AddTape(i.Barcode, i.Serial, TapeInfo.StateTracked), i))
                Case 2 : _store.AddTape(i.Barcode, i.Serial, TapeInfo.StateNever)
                Case Else : _store.AddTape(i.Barcode, i.Serial, TapeInfo.StateAsk)
            End Select
        Else
            Using d As New NewTapesDialog(infos)
                Dim ok As Boolean = d.ShowDialog(Me) = DialogResult.OK
                Dim yes As List(Of IndexFileInfo) = If(ok, d.Tracked, New List(Of IndexFileInfo))
                For Each i As IndexFileInfo In infos
                    If yes.Contains(i) Then
                        result.Add(Tuple.Create(_store.AddTape(i.Barcode, i.Serial, TapeInfo.StateTracked), i))
                    Else
                        _store.AddTape(i.Barcode, i.Serial, If(ok AndAlso d.NeverForUnticked, TapeInfo.StateNever, TapeInfo.StateAsk))
                    End If
                Next
            End Using
        End If
        Return result
    End Function

    ' ---- tapes in the tree ------------------------------------------------------------------------
    Private Function TapeName(id As Long) As String
        Dim t As TapeInfo = Nothing
        Return If(_tapes.TryGetValue(id, t), t.Barcode, "?")
    End Function

    Private Function TapeText(t As TapeInfo) As String
        Dim s As String = t.Barcode & "  (" & FormatCount(t.FileCount) & " files, " & FormatSize(t.TotalBytes) & ")"
        If Not String.IsNullOrEmpty(t.LastError) Then s &= "  - last import failed"
        Return s
    End Function

    Private Sub RefreshTapes(resetIds As IEnumerable(Of Long))
        Dim reset As New HashSet(Of Long)(If(resetIds, Enumerable.Empty(Of Long)()))
        _tapes = _store.GetTapes().ToDictionary(Function(t) t.Id)
        Dim shown As List(Of TapeInfo) = _tapes.Values.Where(Function(t) t.State = TapeInfo.StateTracked AndAlso Not t.Hidden).ToList()
        _treeBusy = True
        _tree.BeginUpdate()
        Try
            For Each n As TreeNode In _tree.Nodes.Cast(Of TreeNode)().ToList()
                Dim tg As NodeTag = TryCast(n.Tag, NodeTag)
                If tg Is Nothing OrElse Not shown.Any(Function(t) t.Id = tg.TapeId) Then n.Remove()
            Next
            For idx As Integer = 0 To shown.Count - 1
                Dim t As TapeInfo = shown(idx)
                Dim node As TreeNode = TapeNode(t.Id)
                If node Is Nothing Then
                    node = New TreeNode(TapeText(t), Theme.ImgTape, Theme.ImgTape) With {.Tag = New NodeTag With {.TapeId = t.Id}}
                    If t.DirCount > 0 Then node.Nodes.Add(New TreeNode("..."))
                    _tree.Nodes.Insert(Math.Min(idx, _tree.Nodes.Count), node)
                Else
                    node.Text = TapeText(t)
                    If reset.Contains(t.Id) Then
                        node.Collapse()
                        node.Nodes.Clear()
                        DirectCast(node.Tag, NodeTag).Loaded = False
                        If t.DirCount > 0 Then node.Nodes.Add(New TreeNode("..."))
                    End If
                End If
            Next
        Finally
            _tree.EndUpdate()
            _treeBusy = False
        End Try
        ' a re-imported tape has new ids: show its top level again
        If _mode = ViewMode.Browse AndAlso reset.Contains(_browseTape) Then
            Dim n As TreeNode = TapeNode(_browseTape)
            If n IsNot Nothing Then
                _tree.SelectedNode = n
                Browse(_browseTape, Nothing)
            End If
        End If
        RefreshTapeCombo()
        Dim tt As Tuple(Of Long, Long, Long) = _store.Totals()
        _lblTotals.Text = FormatCount(tt.Item1) & If(tt.Item1 = 1, " tape, ", " tapes, ") & FormatCount(tt.Item2) & " files, " & FormatSize(tt.Item3)
        If _mode = ViewMode.Search AndAlso reset.Count > 0 Then RunSearch(False)
    End Sub

    Private Sub RefreshTapeCombo()
        Dim keep As Long? = TapeFilter()
        _cboTape.Items.Clear()
        _comboTapeIds.Clear()
        _cboTape.Items.Add("All tapes")
        _comboTapeIds.Add(Nothing)
        For Each t As TapeInfo In _tapes.Values.Where(Function(x) x.State = TapeInfo.StateTracked AndAlso Not x.Hidden).OrderBy(Function(x) x.Barcode, StringComparer.OrdinalIgnoreCase)
            _cboTape.Items.Add(t.Barcode)
            _comboTapeIds.Add(t.Id)
        Next
        Dim sel As Integer = If(keep.HasValue, _comboTapeIds.IndexOf(keep), 0)
        _cboTape.SelectedIndex = Math.Max(0, sel)
    End Sub

    Private Function TapeFilter() As Long?
        Dim i As Integer = _cboTape.SelectedIndex
        Return If(i > 0 AndAlso i < _comboTapeIds.Count, _comboTapeIds(i), Nothing)
    End Function

    Private Function TapeNode(tapeId As Long) As TreeNode
        For Each n As TreeNode In _tree.Nodes
            Dim tg As NodeTag = TryCast(n.Tag, NodeTag)
            If tg IsNot Nothing AndAlso tg.TapeId = tapeId Then Return n
        Next
        Return Nothing
    End Function

    Private Function SelectedTapeId() As Long?
        Dim n As TreeNode = _tree.SelectedNode
        While n IsNot Nothing AndAlso n.Parent IsNot Nothing
            n = n.Parent
        End While
        Dim tg As NodeTag = If(n Is Nothing, Nothing, TryCast(n.Tag, NodeTag))
        Return If(tg Is Nothing, Nothing, CType(tg.TapeId, Long?))
    End Function

    Private Sub EnsureChildrenLoaded(node As TreeNode)
        Dim tg As NodeTag = TryCast(node.Tag, NodeTag)
        If tg Is Nothing OrElse tg.Loaded Then Return
        tg.Loaded = True
        Dim kids As New List(Of TreeNode)
        For Each d As EntryRow In _store.GetChildren(tg.TapeId, tg.DirId, dirsOnly:=True)
            Dim n As New TreeNode(d.Name, Theme.ImgDir, Theme.ImgDir) With {.Tag = New NodeTag With {.TapeId = tg.TapeId, .DirId = d.Id}}
            If d.HasDirs Then n.Nodes.Add(New TreeNode("..."))
            kids.Add(n)
        Next
        _tree.BeginUpdate()
        node.Nodes.Clear()
        node.Nodes.AddRange(kids.ToArray())
        _tree.EndUpdate()
    End Sub

    Private Sub OnTreeSelect(sender As Object, e As TreeViewEventArgs)
        If _treeBusy Then Return
        Dim tg As NodeTag = TryCast(e.Node?.Tag, NodeTag)
        If tg Is Nothing Then Return
        Browse(tg.TapeId, tg.DirId)
        If tg.DirId.HasValue Then
            ShowEntryDetails(tg.DirId.Value)
        Else
            ShowTapeDetails(tg.TapeId)
        End If
    End Sub

    ' ---- list -----------------------------------------------------------------------------------
    Private Sub Browse(tapeId As Long, dirId As Long?)
        _mode = ViewMode.Browse
        _browseTape = tapeId
        _browseDir = dirId
        If _sort = SortColumn.Relevance Then _sort = SortColumn.Name
        Dim rows As List(Of EntryRow) = _store.GetChildren(tapeId, dirId)
        If _sort <> SortColumn.Name OrElse _desc Then SortRows(rows)
        ShowRows(rows)
        _lblView.Text = TapeName(tapeId) & " " & _store.FolderOf(dirId) & " - " & FormatCount(rows.Count) & If(rows.Count = 1, " item", " items")
    End Sub

    Private Sub ShowRows(rows As List(Of EntryRow))
        _list.SelectedIndices.Clear()
        _rows = rows
        _list.VirtualListSize = rows.Count
        _list.Invalidate()
        If rows.Count > 0 Then _list.EnsureVisible(0)
        UpdateSortGlyph()
    End Sub

    Private Sub OnRetrieveItem(sender As Object, e As RetrieveVirtualItemEventArgs)
        If e.ItemIndex < 0 OrElse e.ItemIndex >= _rows.Count Then
            e.Item = New ListViewItem(New String() {"", "", "", "", ""})
            Return
        End If
        Dim r As EntryRow = _rows(e.ItemIndex)
        Dim img As Integer = If(r.IsDir, Theme.ImgDir, If(IsArchiveName(r.Name), Theme.ImgArchive, Theme.ImgFile))
        Dim it As New ListViewItem(r.Name, img)
        it.SubItems.Add(FormatSize(r.Size))
        it.SubItems.Add(If(r.MTime.HasValue, FormatTime(r.MTime.Value), ""))
        it.SubItems.Add(TapeName(r.TapeId))
        it.SubItems.Add(_store.FolderOf(r.ParentId))
        e.Item = it
    End Sub

    Private Sub OnCacheItems(sender As Object, e As CacheVirtualItemsEventArgs)
        Dim ids As New List(Of Long)
        For i As Integer = Math.Max(0, e.StartIndex) To Math.Min(_rows.Count - 1, e.EndIndex)
            If _rows(i).ParentId.HasValue Then ids.Add(_rows(i).ParentId.Value)
        Next
        _store.PrimeFolders(ids)
    End Sub

    Private Sub OnColumnClick(sender As Object, e As ColumnClickEventArgs)
        Dim col As SortColumn = ColumnSorts(e.Column)
        If _sort = col Then
            _desc = Not _desc
        Else
            _sort = col
            _desc = (col = SortColumn.Size OrElse col = SortColumn.Modified)
        End If
        ' a capped result must be re-queried so the first rows are the right ones; otherwise sort what is shown
        If _mode = ViewMode.Search AndAlso _lastCapped Then
            RunSearch(False)
            Return
        End If
        SortRows(_rows)
        ShowRows(_rows)
    End Sub

    Private Sub SortRows(rows As List(Of EntryRow))
        Dim folderFirst As Boolean = _mode = ViewMode.Browse
        Dim cmp As Comparison(Of EntryRow)
        Select Case _sort
            Case SortColumn.Size : cmp = Function(a, b) a.Size.CompareTo(b.Size)
            Case SortColumn.Modified : cmp = Function(a, b) If(a.MTime, Long.MinValue).CompareTo(If(b.MTime, Long.MinValue))
            Case SortColumn.Tape : cmp = Function(a, b) StringComparer.OrdinalIgnoreCase.Compare(TapeName(a.TapeId), TapeName(b.TapeId))
            Case SortColumn.Folder
                _store.PrimeFoldersAll(rows.Where(Function(r) r.ParentId.HasValue).Select(Function(r) r.ParentId.Value))
                cmp = Function(a, b) StringComparer.CurrentCultureIgnoreCase.Compare(_store.FolderOf(a.ParentId), _store.FolderOf(b.ParentId))
            Case Else : cmp = Function(a, b) StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)
        End Select
        Dim sign As Integer = If(_desc, -1, 1)
        rows.Sort(Function(a, b)
                      If folderFirst AndAlso a.IsDir <> b.IsDir Then Return If(a.IsDir, -1, 1)
                      Dim c As Integer = cmp(a, b) * sign
                      If c = 0 Then c = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name)
                      Return c
                  End Function)
    End Sub

    Private Sub UpdateSortGlyph()
        For i As Integer = 0 To ColumnTitles.Length - 1
            Dim t As String = ColumnTitles(i)
            If ColumnSorts(i) = _sort Then t &= If(_desc, "  v", "  ^")
            If _list.Columns(i).Text <> t Then _list.Columns(i).Text = t
        Next
    End Sub

    Private Function FocusedRow() As EntryRow
        If _list.FocusedItem IsNot Nothing AndAlso _list.FocusedItem.Index >= 0 AndAlso _list.FocusedItem.Index < _rows.Count Then Return _rows(_list.FocusedItem.Index)
        If _list.SelectedIndices.Count > 0 AndAlso _list.SelectedIndices(0) < _rows.Count Then Return _rows(_list.SelectedIndices(0))
        Return Nothing
    End Function

    Private Function SelectedRows() As List(Of EntryRow)
        Dim list As New List(Of EntryRow)
        For Each i As Integer In _list.SelectedIndices
            If i >= 0 AndAlso i < _rows.Count Then list.Add(_rows(i))
        Next
        Return list
    End Function

    Private Sub OnListSelection()
        If _list.SelectedIndices.Count = 0 Then Return
        Dim r As EntryRow = FocusedRow()
        If r IsNot Nothing Then ShowEntryDetails(r.Id)
    End Sub

    Private Sub OnListKeyDown(sender As Object, e As KeyEventArgs)
        If e.Control AndAlso e.KeyCode = Keys.C Then
            CopySelected(True)
            e.Handled = True
        ElseIf e.Control AndAlso e.KeyCode = Keys.A Then
            For i As Integer = 0 To _rows.Count - 1
                _list.SelectedIndices.Add(i)
                If i > 100000 Then Exit For
            Next
            e.Handled = True
        ElseIf e.KeyCode = Keys.Back AndAlso _mode = ViewMode.Browse AndAlso _tree.SelectedNode?.Parent IsNot Nothing Then
            _tree.SelectedNode = _tree.SelectedNode.Parent
            e.Handled = True
        End If
    End Sub

    ''' <summary>Enter / double-click: open a folder, or show a found file in its folder.</summary>
    Private Sub OnActivate()
        Dim r As EntryRow = FocusedRow()
        If r Is Nothing Then Return
        If _mode = ViewMode.Search Then AddHistory(_txt.Text.Trim())
        If r.IsDir Then
            Reveal(r.TapeId, _store.AncestorIds(r.Id).Concat(New Long() {r.Id}).ToList(), Nothing)
        ElseIf _mode = ViewMode.Search Then
            Reveal(r.TapeId, _store.AncestorIds(r.Id), r.Id)
        End If
    End Sub

    ''' <summary>Opens the folder chain in the tree (tape top level first) and selects an entry in the list.</summary>
    Private Sub Reveal(tapeId As Long, chain As List(Of Long), selectId As Long?)
        Dim node As TreeNode = TapeNode(tapeId)
        If node Is Nothing Then Return
        For Each dirId As Long In chain
            EnsureChildrenLoaded(node)
            node.Expand()
            Dim want As Long = dirId
            Dim nextNode As TreeNode = node.Nodes.Cast(Of TreeNode)().FirstOrDefault(
                Function(n)
                    Dim tg As NodeTag = TryCast(n.Tag, NodeTag)
                    Return tg IsNot Nothing AndAlso tg.DirId.HasValue AndAlso tg.DirId.Value = want
                End Function)
            If nextNode Is Nothing Then Exit For
            node = nextNode
        Next
        If _tree.SelectedNode Is node Then
            Dim tg As NodeTag = DirectCast(node.Tag, NodeTag)
            Browse(tg.TapeId, tg.DirId)
        Else
            _tree.SelectedNode = node
        End If
        node.EnsureVisible()
        If _txt.Text.Trim().Length > 0 Then _lblView.Text &= "   (Enter in the search box returns to the results)"
        If selectId.HasValue Then
            Dim idx As Integer = _rows.FindIndex(Function(x) x.Id = selectId.Value)
            If idx >= 0 Then
                _list.SelectedIndices.Clear()
                _list.SelectedIndices.Add(idx)
                _list.FocusedItem = _list.Items(idx)
                _list.EnsureVisible(idx)
                _list.Focus()
                ShowEntryDetails(selectId.Value)
            End If
        End If
    End Sub

    Private Function FullPath(r As EntryRow) As String
        Dim folder As String = _store.FolderOf(r.ParentId)
        Return TapeName(r.TapeId) & ":" & If(folder = "\", "\" & r.Name, folder & "\" & r.Name)
    End Function

    Private Sub CopySelected(fullPaths As Boolean)
        Dim rows As List(Of EntryRow) = SelectedRows()
        If rows.Count = 0 Then Return
        _store.PrimeFoldersAll(rows.Where(Function(r) r.ParentId.HasValue).Select(Function(r) r.ParentId.Value))
        Dim text As String = String.Join(Environment.NewLine, rows.Select(Function(r) If(fullPaths, FullPath(r), r.Name)))
        Try
            Clipboard.SetText(text)
            _lblView.Text = FormatCount(rows.Count) & If(rows.Count = 1, " path", " paths") & " copied"
        Catch ex As ExternalException
            _lblView.Text = "The clipboard is busy - try again"
        End Try
    End Sub

    ' ---- details pane ---------------------------------------------------------------------------
    Private Sub AddDetail(name As String, value As String)
        Dim it As New ListViewItem(name)
        it.SubItems.Add(If(value, ""))
        _details.Items.Add(it)
    End Sub

    Private Sub ShowEntryDetails(id As Long)
        Dim d As EntryDetails = _store.GetDetails(id)
        _details.BeginUpdate()
        _details.Items.Clear()
        If d IsNot Nothing Then
            Dim t As TapeInfo = Nothing
            _tapes.TryGetValue(d.TapeId, t)
            AddDetail("Name", d.Name)
            AddDetail("Type", If(d.IsDir, "Folder", "File"))
            AddDetail("Tape", If(t Is Nothing, "?", t.Label))
            AddDetail("Folder", d.FolderPath)
            AddDetail("Full path", FullPath(d))
            AddDetail("Size", FormatSize(d.Size) & "  (" & d.Size.ToString("N0", CultureInfo.InvariantCulture) & " bytes)")
            If d.IsDir Then AddDetail("Files inside", FormatCount(d.Items))
            AddDetail("Modified", If(d.MTime.HasValue, FormatTime(d.MTime.Value), ""))
            AddDetail("Created", If(d.CTime.HasValue, FormatTime(d.CTime.Value), ""))
            AddDetail("Accessed", If(d.ATime.HasValue, FormatTime(d.ATime.Value), ""))
            If Not d.IsDir Then
                AddDetail("Read-only", If(d.IsReadOnly, "Yes", "No"))
                If d.StartBlock.HasValue Then AddDetail("Position on tape", "partition " & d.Partition & ", block " & d.StartBlock.Value.ToString(CultureInfo.InvariantCulture))
                If d.FileUid.HasValue Then AddDetail("File UID", d.FileUid.Value.ToString(CultureInfo.InvariantCulture))
                For Each line As String In d.Hashes.Split(New Char() {ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                    Dim c As Integer = line.IndexOf(":"c)
                    If c > 0 Then AddDetail(line.Substring(0, c), line.Substring(c + 1))
                Next
            End If
        End If
        _details.EndUpdate()
    End Sub

    Private Sub ShowTapeDetails(tapeId As Long)
        Dim t As TapeInfo = _store.GetTape(tapeId)
        _details.BeginUpdate()
        _details.Items.Clear()
        If t IsNot Nothing Then
            AddDetail("Tape", t.Barcode)
            AddDetail("Cartridge serial", t.Serial)
            If Not String.IsNullOrEmpty(t.Format) Then AddDetail("Format", t.Format & If(String.IsNullOrEmpty(t.Vendor), "", " (" & t.Vendor & ")"))
            If t.Capacity > 0 Then AddDetail("Capacity", FormatSize(t.Capacity))
            AddDetail("Data", FormatSize(t.TotalBytes) & "  (" & t.TotalBytes.ToString("N0", CultureInfo.InvariantCulture) & " bytes)")
            AddDetail("Files", FormatCount(t.FileCount))
            AddDetail("Folders", FormatCount(t.DirCount))
            AddDetail("Index generation", t.Generation.ToString(CultureInfo.InvariantCulture))
            AddDetail("Index written", If(t.IndexTime.HasValue, FormatTime(t.IndexTime.Value), ""))
            AddDetail("LTFS volume", t.VolumeUuid)
            AddDetail("Index file", t.IndexFile)
            AddDetail("In catalog since", If(t.FirstSeen.HasValue, FormatTime(t.FirstSeen.Value), ""))
            AddDetail("Catalog updated", If(t.Updated.HasValue, FormatTime(t.Updated.Value), ""))
            If Not String.IsNullOrEmpty(t.LastError) Then AddDetail("Last import error", t.LastError)
        End If
        _details.EndUpdate()
    End Sub

    ' ---- search ---------------------------------------------------------------------------------
    Private Sub OnSearchTextChanged(sender As Object, e As EventArgs)
        _debounce.Stop()
        If _txt.Text.Trim().Length = 0 Then
            ExitSearch()
        Else
            _debounce.Start()
        End If
    End Sub

    Private Sub OnSearchKeyDown(sender As Object, e As KeyEventArgs)
        Select Case e.KeyCode
            Case Keys.Enter
                e.SuppressKeyPress = True
                _debounce.Stop()
                RunSearch(True)
            Case Keys.Escape
                e.SuppressKeyPress = True
                _txt.Text = ""
            Case Keys.Down
                If _rows.Count > 0 Then
                    e.SuppressKeyPress = True
                    _list.Focus()
                    If _list.SelectedIndices.Count = 0 Then
                        _list.SelectedIndices.Add(0)
                        _list.FocusedItem = _list.Items(0)
                    End If
                End If
        End Select
    End Sub

    Private Sub OnFilterChanged()
        If _store Is Nothing Then Return
        If _txt.Text.Trim().Length > 0 OrElse TapeFilter().HasValue OrElse _cboKind.SelectedIndex > 0 Then
            If _txt.Text.Trim().Length > 0 Then RunSearch(False)
        End If
    End Sub

    Private Sub ExitSearch()
        _searchCts?.Cancel()
        _engine?.CancelRunning()
        If _mode <> ViewMode.Search Then Return
        _mode = ViewMode.Browse
        If _browseTape >= 0 AndAlso TapeNode(_browseTape) IsNot Nothing Then
            Browse(_browseTape, _browseDir)
        Else
            ShowRows(New List(Of EntryRow))
            _lblView.Text = ""
        End If
    End Sub

    Private Async Sub RunSearch(recordHistory As Boolean)
        If _store Is Nothing Then Return
        Dim text As String = _txt.Text.Trim()
        If text.Length = 0 Then
            ExitSearch()
            Return
        End If
        Dim q As ParsedQuery = ParsedQuery.Parse(text)
        If q.Errors.Count > 0 Then
            _lblView.Text = "Not understood: " & q.Errors(0)
            Return
        End If
        If q.IsEmpty Then Return
        If _mode <> ViewMode.Search Then
            _sort = SortColumn.Relevance
            _desc = False
        End If
        Dim o As New SearchOptions With {.TapeId = TapeFilter(), .MatchPath = _btnPath.Checked, .Sort = _sort, .Descending = _desc,
            .Kind = If(_cboKind.SelectedIndex = 1, KindFilter.Files, If(_cboKind.SelectedIndex = 2, KindFilter.Folders, KindFilter.All))}
        _searchCts?.Cancel()
        _engine.CancelRunning()
        Dim cts As New CancellationTokenSource()
        _searchCts = cts
        Dim seq As Integer = Interlocked.Increment(_searchSeq)
        _lblView.Text = "Searching ..."
        Dim engine As SearchEngine = _engine
        Dim res As SearchResult
        Try
            res = Await Task.Run(Function() engine.Run(q, o, cts.Token))
        Catch ex As Exception
            LogError("Search", ex)
            _lblView.Text = "Search failed: " & ex.Message
            Return
        End Try
        If res Is Nothing OrElse seq <> _searchSeq OrElse _closing Then Return
        If res.ErrorText IsNot Nothing Then
            _lblView.Text = "Search failed: " & res.ErrorText
            Return
        End If
        _mode = ViewMode.Search
        _lastCapped = res.Capped
        ShowRows(res.Rows)
        Dim n As Integer = res.Rows.Count
        If res.Capped Then
            _lblView.Text = FormatCount(SearchEngine.Cap) & "+ results; the first " & FormatCount(SearchEngine.Cap) & " are shown - add words or filters to narrow it"
        Else
            _lblView.Text = FormatCount(n) & If(n = 1, " result", " results") & " in " & res.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) & " s"
        End If
        If recordHistory Then AddHistory(text)
    End Sub

    Private Sub AddHistory(text As String)
        If String.IsNullOrWhiteSpace(text) Then Return
        _history.RemoveAll(Function(h) String.Equals(h, text, StringComparison.CurrentCultureIgnoreCase))
        _history.Insert(0, text)
        If _history.Count > 30 Then _history.RemoveRange(30, _history.Count - 30)
        RefreshHistory()
    End Sub

    Private Sub RefreshHistory()
        Dim src As New AutoCompleteStringCollection()
        src.AddRange(_history.ToArray())
        _txt.AutoCompleteCustomSource = src
    End Sub

    Private Sub ShowSearchHelp()
        If _help Is Nothing OrElse _help.IsDisposed Then
            _help = New SearchHelpForm()
            AddHandler _help.ExampleChosen, Sub(t)
                                                _txt.Text = t
                                                _debounce.Stop()
                                                RunSearch(True)
                                            End Sub
        End If
        If Not _help.Visible Then _help.Show(Me)
        _help.Activate()
    End Sub

    Private Sub SearchOnTape(tapeId As Long?)
        If Not tapeId.HasValue Then Return
        Dim i As Integer = _comboTapeIds.IndexOf(tapeId)
        If i > 0 Then _cboTape.SelectedIndex = i
        _txt.Focus()
    End Sub

    Private Sub SearchInSelectedFolder()
        Dim r As EntryRow = FocusedRow()
        If r Is Nothing OrElse Not r.IsDir Then Return
        Dim folder As String = _store.FolderOf(r.Id)
        Dim i As Integer = _comboTapeIds.IndexOf(r.TapeId)
        If i > 0 Then _cboTape.SelectedIndex = i
        _txt.Text = "path:""" & folder & """ "
        _txt.Focus()
        _txt.SelectionStart = _txt.Text.Length
    End Sub

    ' ---- catalog commands -------------------------------------------------------------------------
    Private Sub DoReimport(tapeId As Long?)
        If Not tapeId.HasValue Then Return
        _store.ForceReimport(tapeId.Value)
        StartSync()
    End Sub

    Private Sub DoRemoveTape(tapeId As Long?)
        If Not tapeId.HasValue Then Return
        Dim t As TapeInfo = _store.GetTape(tapeId.Value)
        If t Is Nothing Then Return
        If Theme.Ask(Me, "Remove tape", "Remove " & t.Barcode & " from the catalog?",
                     "Its file list is deleted from the catalog and the tape is no longer tracked." & Environment.NewLine &
                     "You can track it again from Catalog > Untracked tapes.", New String() {"Remove", "Cancel"}, 1, 1) <> 0 Then Return
        _store.ClearTape(t.Id, hide:=False)
        _store.SetTapeState(t.Id, TapeInfo.StateNever)
        If _browseTape = t.Id Then
            _browseTape = -1
            ShowRows(New List(Of EntryRow))
            _details.Items.Clear()
        End If
        RefreshTapes(Nothing)
    End Sub

    Private Sub DoUntracked()
        Dim list As List(Of TapeInfo) = _store.GetTapes().Where(Function(t) t.State <> TapeInfo.StateTracked).ToList()
        Using d As New UntrackedTapesDialog(list)
            If d.ShowDialog(Me) <> DialogResult.OK Then Return
            For Each t As TapeInfo In d.ToTrack
                _store.SetTapeState(t.Id, TapeInfo.StateTracked)
                _store.ForceReimport(t.Id)
            Next
        End Using
        StartSync()
    End Sub

    Private Sub DoChooseFolder()
        Using d As New FolderBrowserDialog With {.Description = "Folder where LTFSCopyGUI saves tape indexes (its ""schema"" folder)", .SelectedPath = IndexFolder}
            If d.ShowDialog(Me) <> DialogResult.OK Then Return
            _store.SetMeta("index_folder", d.SelectedPath)
        End Using
        _opts.IndexFolder = Nothing
        UpdateFolderLabel()
        RestartWatcher()
        StartSync()
    End Sub

    Private Async Sub DoImportFile()
        Dim info As IndexFileInfo
        Using d As New OpenFileDialog With {.Title = "Import an LTFS index", .Filter = "LTFS index (*.schema;*.schema.zst;*.xml)|*.schema;*.zst;*.xml|All files (*.*)|*.*"}
            If d.ShowDialog(Me) <> DialogResult.OK Then Return
            info = IndexScanner.Describe(d.FileName)
        End Using
        Dim barcode As String = Microsoft.VisualBasic.Interaction.InputBox("Barcode (tape name) for this index:", "Import index", info.Barcode)
        If String.IsNullOrWhiteSpace(barcode) Then Return
        info.Barcode = barcode.Trim()
        Try
            IndexScanner.PeekHeader(info)
        Catch ex As Exception
            MessageBox.Show(Me, "This file cannot be read as an LTFS index:" & Environment.NewLine & ex.Message, "Import index", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End Try
        Dim tapeId As Long = _store.AddTape(info.Barcode, info.Serial, TapeInfo.StateTracked)
        Dim store As CatalogStore = _store
        Dim ct As CancellationToken = _closeCts.Token
        SetActivity("Importing " & info.Label & " ...")
        Try
            Await Task.Run(Sub() IndexImporter.Run(store, info, tapeId, AddressOf SetActivity, ct))
            SetActivity("Imported " & info.Label)
        Catch ex As OperationCanceledException
            Return
        Catch ex As Exception
            LogError("Import of " & info.Path, ex)
            _store.SetTapeError(tapeId, ex.Message)
            SetActivity("Import failed: " & ex.Message)
        End Try
        If Not _closing Then RefreshTapes(New Long() {tapeId})
    End Sub

    Private Sub ShowAbout()
        Dim v As Version = GetType(MainForm).Assembly.GetName().Version
        MessageBox.Show(Me, "LTFSCatalog " & v.ToString(3) & Environment.NewLine &
                        "Offline catalog of LTFS tapes, built from the indexes LTFSCopyGUI saves." & Environment.NewLine & Environment.NewLine &
                        "Catalog: " & _store.DbPath & Environment.NewLine & "Index folder: " & IndexFolder,
                        "About LTFSCatalog", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

End Class
