Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Reflection
Imports System.Runtime.CompilerServices
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

''' <summary>
''' The look shared by LTFSCopyGUI and LTFSCatalog. This one source file is linked into both projects.
''' Interface text uses Segoe UI 10 pt, and read-only consoles and logs use Consolas 10 pt. Menus, tool bars and status
''' bars are flat and use the same accent hover as ModernCommandBar; lists and trees are drawn in the Explorer style.
''' Layouts are left alone. Only fonts in the old default faces (SimSun, MS Sans Serif) are replaced: a font picked
''' on purpose stays.
''' </summary>
Friend NotInheritable Class UiStyle
    Private Sub New()
    End Sub

    Public Const FontSize As Single = 10.0F
    Private Const LegacySize As Single = 9.0F

    ' SimSun is the old default of both apps ("宋体" and "新宋体" are spelled with escapes so the file stays ASCII).
    Private Shared ReadOnly LegacyFaces As String() = {"SimSun", ChrW(&H5B8B) & ChrW(&H4F53), "SimSun-ExtB", "NSimSun",
        ChrW(&H65B0) & ChrW(&H5B8B) & ChrW(&H4F53), "Microsoft Sans Serif", "MS Sans Serif", "MS Shell Dlg", "MS Shell Dlg 2"}

    Private Shared ReadOnly Gate As New Object()
    Private Shared _ui As Font
    Private Shared _mono As Font
    Private Shared ReadOnly FontCache As New Dictionary(Of String, Font)(StringComparer.Ordinal)
    Private Shared ReadOnly Hooked As New ConditionalWeakTable(Of Form, Object)()

    ''' <summary>Segoe UI 10 pt (or the system message font where Segoe UI is missing). Shared: never dispose it.</summary>
    Public Shared ReadOnly Property UiFont As Font
        Get
            SyncLock Gate
                If _ui Is Nothing Then _ui = MakeFont("Segoe UI", SystemFonts.MessageBoxFont.FontFamily.Name)
                Return _ui
            End SyncLock
        End Get
    End Property

    ''' <summary>Consolas 10 pt (or the generic monospace font). Shared: never dispose it.</summary>
    Public Shared ReadOnly Property MonoFont As Font
        Get
            SyncLock Gate
                If _mono Is Nothing Then _mono = MakeFont("Consolas", FontFamily.GenericMonospace.Name)
                Return _mono
            End SyncLock
        End Get
    End Property

    ''' <summary>A new copy of the interface font, owned by the caller.</summary>
    Public Shared Function NewUiFont() As Font
        Return New Font(UiFont.FontFamily, FontSize, FontStyle.Regular, GraphicsUnit.Point)
    End Function

    ''' <summary>A new copy of the console font, owned by the caller.</summary>
    Public Shared Function NewMonoFont() As Font
        Return New Font(MonoFont.FontFamily, FontSize, FontStyle.Regular, GraphicsUnit.Point)
    End Function

    Private Shared Function MakeFont(face As String, fallback As String) As Font
        Dim f As New Font(face, FontSize, FontStyle.Regular, GraphicsUnit.Point)
        If String.Equals(f.Name, face, StringComparison.OrdinalIgnoreCase) Then Return f
        f.Dispose()
        Return New Font(fallback, FontSize, FontStyle.Regular, GraphicsUnit.Point)
    End Function

    ''' <summary>True for the old default faces that the shared fonts replace.</summary>
    Public Shared Function IsLegacyFace(f As Font) As Boolean
        If f Is Nothing Then Return False
        For Each n As String In LegacyFaces
            If String.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(f.OriginalFontName, n, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    ''' <summary>The replacement for an old-face font: same style, size kept relative to the old 9 pt default.</summary>
    Private Shared Function Swap(old As Font, baseFont As Font, mono As Boolean) As Font
        Dim size As Single = baseFont.SizeInPoints
        Dim oldSize As Single = old.SizeInPoints
        If Math.Abs(oldSize - LegacySize) > 0.5F Then size = CSng(Math.Round(size * oldSize / LegacySize * 4.0F) / 4.0F)
        Dim family As FontFamily = If(mono, MonoFont.FontFamily, baseFont.FontFamily)
        Dim key As String = family.Name & "|" & size.ToString(System.Globalization.CultureInfo.InvariantCulture) & "|" & CInt(old.Style).ToString()
        SyncLock Gate
            Dim f As Font = Nothing
            If Not FontCache.TryGetValue(key, f) Then
                f = New Font(family, size, old.Style, GraphicsUnit.Point)
                FontCache(key) = f
            End If
            Return f
        End SyncLock
    End Function

    ''' <summary>
    ''' Styles a form and everything in it. Safe to call more than once; it runs again when the form loads, which
    ''' catches controls created in code after InitializeComponent.
    ''' </summary>
    Public Shared Sub Apply(form As Form, Optional baseFont As Font = Nothing)
        If form Is Nothing Then Return
        If baseFont Is Nothing Then baseFont = UiFont
        If Not TypeOf ToolStripManager.Renderer Is FlatRenderer Then ToolStripManager.Renderer = New FlatRenderer()
        Restyle(form, baseFont)
        SyncLock Hooked
            Dim seen As Object = Nothing
            If Hooked.TryGetValue(form, seen) Then Return
            Hooked.Add(form, baseFont)
        End SyncLock
        AddHandler form.Load, Sub(s, e) Restyle(form, baseFont)
    End Sub

    Private Shared Sub Restyle(form As Form, baseFont As Font)
        If IsLegacyFace(form.Font) Then form.Font = baseFont
        StyleChildren(form, baseFont)
        If form.ContextMenuStrip IsNot Nothing Then StyleStrip(form.ContextMenuStrip, baseFont)
        ' Context menus that are only shown from code sit in fields, not in the control tree.
        Dim t As Type = form.GetType()
        While t IsNot Nothing AndAlso t IsNot GetType(Form)
            For Each fi As FieldInfo In t.GetFields(BindingFlags.Instance Or BindingFlags.Public Or BindingFlags.NonPublic Or BindingFlags.DeclaredOnly)
                If GetType(ToolStrip).IsAssignableFrom(fi.FieldType) Then
                    Dim ts As ToolStrip = TryCast(fi.GetValue(form), ToolStrip)
                    If ts IsNot Nothing Then StyleStrip(ts, baseFont)
                End If
            Next
            t = t.BaseType
        End While
    End Sub

    Private Shared Sub StyleChildren(parent As Control, baseFont As Font)
        For Each c As Control In parent.Controls
            StyleControl(c, baseFont)
            If Not TypeOf c Is ToolStrip Then StyleChildren(c, baseFont)
        Next
    End Sub

    Private Shared Sub StyleControl(c As Control, baseFont As Font)
        Dim strip As ToolStrip = TryCast(c, ToolStrip)
        If strip IsNot Nothing Then
            StyleStrip(strip, baseFont)
            Return
        End If
        Dim box As TextBoxBase = TryCast(c, TextBoxBase)
        If IsLegacyFace(c.Font) Then
            c.Font = Swap(c.Font, baseFont, box IsNot Nothing AndAlso box.Multiline)
        ElseIf box IsNot Nothing AndAlso box.Multiline AndAlso box.ReadOnly AndAlso c.Parent IsNot Nothing AndAlso c.Font.Equals(c.Parent.Font) Then
            ' a read-only multi-line box is a log or console: monospace, like the SCSI console
            c.Font = Swap(c.Font, baseFont, True)
        End If
        If c.ContextMenuStrip IsNot Nothing Then StyleStrip(c.ContextMenuStrip, baseFont)
        If TypeOf c Is ListView OrElse TypeOf c Is TreeView Then StyleView(c)
    End Sub

    ' ---- lists and trees: Explorer style (hover highlight, arrows instead of +/- boxes), double-buffered -----------

    <DllImport("uxtheme.dll", CharSet:=CharSet.Unicode)>
    Private Shared Function SetWindowTheme(hWnd As IntPtr, appName As String, idList As String) As Integer
    End Function

    <DllImport("user32.dll")>
    Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
    End Function

    Private Const TVM_SETEXTENDEDSTYLE As Integer = &H1100 + 44
    Private Const TVS_EX_DOUBLEBUFFER As Integer = &H4

    Private Shared Sub StyleView(c As Control)
        Dim tree As TreeView = TryCast(c, TreeView)
        If tree IsNot Nothing AndAlso Not tree.IsHandleCreated AndAlso Not tree.CheckBoxes Then
            ' set before the window exists: changing these later re-creates it
            tree.ShowLines = False
            tree.FullRowSelect = True
        End If
        Dim list As ListView = TryCast(c, ListView)
        If list IsNot Nothing Then
            Try
                GetType(Control).GetProperty("DoubleBuffered", BindingFlags.Instance Or BindingFlags.NonPublic).SetValue(list, True, Nothing)
            Catch
            End Try
        End If
        RemoveHandler c.HandleCreated, AddressOf OnViewHandleCreated
        AddHandler c.HandleCreated, AddressOf OnViewHandleCreated
        If c.IsHandleCreated Then ThemeView(c)
    End Sub

    Private Shared Sub OnViewHandleCreated(sender As Object, e As EventArgs)
        Dim c As Control = TryCast(sender, Control)
        If c IsNot Nothing Then ThemeView(c)
    End Sub

    Private Shared Sub ThemeView(c As Control)
        Try
            SetWindowTheme(c.Handle, "Explorer", Nothing)
            If TypeOf c Is TreeView Then SendMessage(c.Handle, TVM_SETEXTENDEDSTYLE, New IntPtr(TVS_EX_DOUBLEBUFFER), New IntPtr(TVS_EX_DOUBLEBUFFER))
        Catch
            ' cosmetic only
        End Try
    End Sub

    ' ---- menus, tool bars, status bars ------------------------------------------------------------------------

    Private Shared Sub StyleStrip(ts As ToolStrip, baseFont As Font)
        If NeedsFont(ts.Font) Then ts.Font = baseFont
        StyleItems(ts.Items, baseFont)
    End Sub

    ''' <summary>A strip or item font that should follow the interface font: old face, or the system menu default.</summary>
    Private Shared Function NeedsFont(f As Font) As Boolean
        Return IsLegacyFace(f) OrElse IsMenuDefault(f)
    End Function

    ' A strip's own default font (internal ToolStripManager.DefaultFont) is the system menu font.
    Private Shared ReadOnly MenuDefault As Font = SystemFonts.MenuFont

    Private Shared Function IsMenuDefault(f As Font) As Boolean
        Return String.Equals(f.Name, MenuDefault.Name, StringComparison.OrdinalIgnoreCase) AndAlso Math.Abs(f.SizeInPoints - MenuDefault.SizeInPoints) < 0.1F
    End Function

    Private Shared Sub StyleItems(items As ToolStripItemCollection, baseFont As Font)
        For Each it As ToolStripItem In items
            If IsLegacyFace(it.Font) Then
                it.Font = Swap(it.Font, baseFont, False)
            ElseIf IsMenuDefault(it.Font) Then
                it.Font = baseFont
            End If
            Dim dd As ToolStripDropDownItem = TryCast(it, ToolStripDropDownItem)
            If dd IsNot Nothing AndAlso dd.HasDropDownItems Then
                If NeedsFont(dd.DropDown.Font) Then dd.DropDown.Font = baseFont
                StyleItems(dd.DropDownItems, baseFont)
            End If
        Next
    End Sub

    ''' <summary>Professional renderer with flat colours: white bars, hairlines, accent-tinted hover, no gradients.</summary>
    Private NotInheritable Class FlatRenderer
        Inherits ToolStripProfessionalRenderer

        Public Sub New()
            MyBase.New(New FlatColors())
            RoundedEdges = False
        End Sub

        Protected Overrides Sub OnRenderToolStripBorder(e As ToolStripRenderEventArgs)
            Dim ts As ToolStrip = e.ToolStrip
            If TypeOf ts Is ToolStripDropDown Then
                MyBase.OnRenderToolStripBorder(e)
            ElseIf TypeOf ts Is StatusStrip Then
                Using p As New Pen(FlatColors.Line)
                    e.Graphics.DrawLine(p, 0, 0, ts.Width, 0)
                End Using
            ElseIf Not TypeOf ts Is MenuStrip Then
                Using p As New Pen(FlatColors.Line)
                    e.Graphics.DrawLine(p, 0, ts.Height - 1, ts.Width, ts.Height - 1)
                End Using
            End If
        End Sub
    End Class

    Private NotInheritable Class FlatColors
        Inherits ProfessionalColorTable

        Public Shared ReadOnly Property Line As Color = Color.FromArgb(222, 222, 222)
        Private Shared ReadOnly Bar As Color = SystemColors.Window
        Private Shared ReadOnly Status As Color = SystemColors.Control
        Private Shared ReadOnly MenuEdge As Color = Color.FromArgb(204, 204, 204)
        ' the same tints as ModernCommandBar: accent at alpha 30 (hover) and 58 (pressed) over white
        Private Shared ReadOnly Hover As Color = Tint(30)
        Private Shared ReadOnly Press As Color = Tint(58)

        Private Shared Function Tint(alpha As Integer) As Color
            Dim a As Color = SystemColors.Highlight, b As Color = SystemColors.Window
            Return Color.FromArgb((a.R * alpha + b.R * (255 - alpha)) \ 255, (a.G * alpha + b.G * (255 - alpha)) \ 255, (a.B * alpha + b.B * (255 - alpha)) \ 255)
        End Function

        Public Sub New()
            UseSystemColors = False
        End Sub

        Public Overrides ReadOnly Property MenuStripGradientBegin As Color = Bar
        Public Overrides ReadOnly Property MenuStripGradientEnd As Color = Bar
        Public Overrides ReadOnly Property ToolStripGradientBegin As Color = Bar
        Public Overrides ReadOnly Property ToolStripGradientMiddle As Color = Bar
        Public Overrides ReadOnly Property ToolStripGradientEnd As Color = Bar
        Public Overrides ReadOnly Property ToolStripBorder As Color = Line
        Public Overrides ReadOnly Property ToolStripContentPanelGradientBegin As Color = Bar
        Public Overrides ReadOnly Property ToolStripContentPanelGradientEnd As Color = Bar
        Public Overrides ReadOnly Property ToolStripPanelGradientBegin As Color = Bar
        Public Overrides ReadOnly Property ToolStripPanelGradientEnd As Color = Bar
        Public Overrides ReadOnly Property StatusStripGradientBegin As Color = Status
        Public Overrides ReadOnly Property StatusStripGradientEnd As Color = Status
        Public Overrides ReadOnly Property ToolStripDropDownBackground As Color = Bar
        Public Overrides ReadOnly Property ImageMarginGradientBegin As Color = Bar
        Public Overrides ReadOnly Property ImageMarginGradientMiddle As Color = Bar
        Public Overrides ReadOnly Property ImageMarginGradientEnd As Color = Bar
        Public Overrides ReadOnly Property MenuBorder As Color = MenuEdge
        Public Overrides ReadOnly Property MenuItemBorder As Color = Hover
        Public Overrides ReadOnly Property MenuItemSelected As Color = Hover
        Public Overrides ReadOnly Property MenuItemSelectedGradientBegin As Color = Hover
        Public Overrides ReadOnly Property MenuItemSelectedGradientEnd As Color = Hover
        Public Overrides ReadOnly Property MenuItemPressedGradientBegin As Color = Hover
        Public Overrides ReadOnly Property MenuItemPressedGradientMiddle As Color = Hover
        Public Overrides ReadOnly Property MenuItemPressedGradientEnd As Color = Hover
        Public Overrides ReadOnly Property ButtonSelectedHighlight As Color = Hover
        Public Overrides ReadOnly Property ButtonSelectedHighlightBorder As Color = Hover
        Public Overrides ReadOnly Property ButtonSelectedBorder As Color = Hover
        Public Overrides ReadOnly Property ButtonSelectedGradientBegin As Color = Hover
        Public Overrides ReadOnly Property ButtonSelectedGradientMiddle As Color = Hover
        Public Overrides ReadOnly Property ButtonSelectedGradientEnd As Color = Hover
        Public Overrides ReadOnly Property ButtonPressedHighlight As Color = Press
        Public Overrides ReadOnly Property ButtonPressedHighlightBorder As Color = Press
        Public Overrides ReadOnly Property ButtonPressedBorder As Color = Press
        Public Overrides ReadOnly Property ButtonPressedGradientBegin As Color = Press
        Public Overrides ReadOnly Property ButtonPressedGradientMiddle As Color = Press
        Public Overrides ReadOnly Property ButtonPressedGradientEnd As Color = Press
        Public Overrides ReadOnly Property ButtonCheckedHighlight As Color = Press
        Public Overrides ReadOnly Property ButtonCheckedHighlightBorder As Color = Press
        Public Overrides ReadOnly Property ButtonCheckedGradientBegin As Color = Press
        Public Overrides ReadOnly Property ButtonCheckedGradientMiddle As Color = Press
        Public Overrides ReadOnly Property ButtonCheckedGradientEnd As Color = Press
        Public Overrides ReadOnly Property CheckBackground As Color = Hover
        Public Overrides ReadOnly Property CheckSelectedBackground As Color = Press
        Public Overrides ReadOnly Property CheckPressedBackground As Color = Press
        Public Overrides ReadOnly Property SeparatorDark As Color = Line
        Public Overrides ReadOnly Property SeparatorLight As Color = Bar
        Public Overrides ReadOnly Property GripDark As Color = Line
        Public Overrides ReadOnly Property GripLight As Color = Bar
        Public Overrides ReadOnly Property OverflowButtonGradientBegin As Color = Bar
        Public Overrides ReadOnly Property OverflowButtonGradientMiddle As Color = Bar
        Public Overrides ReadOnly Property OverflowButtonGradientEnd As Color = Bar
        Public Overrides ReadOnly Property RaftingContainerGradientBegin As Color = Bar
        Public Overrides ReadOnly Property RaftingContainerGradientEnd As Color = Bar
    End Class
End Class
