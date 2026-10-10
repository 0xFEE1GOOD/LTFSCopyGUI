Option Strict On
Option Explicit On

Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Drawing
Imports System.Windows.Forms

<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()>
Partial Public Class SCSIGUI
    Inherits Form

    Private components As IContainer

    Friend WithEvents cboTemplates As ComboBox
    Friend WithEvents lstCommands As ListBox
    Friend WithEvents pg As PropertyGrid
    Friend WithEvents tvElements As TreeView
    Friend WithEvents chkEdit As CheckBox
    Friend WithEvents btnSave As Button
    Friend WithEvents btnAdd As Button
    Friend WithEvents btnDelete As Button
    Friend WithEvents btnUp As Button
    Friend WithEvents btnDown As Button
    Friend WithEvents tabs As TabControl
    Friend WithEvents tabValues As TabPage
    Friend WithEvents tabDefinition As TabPage
    Friend WithEvents pnlValues As FlowLayoutPanel

    ' 所有布局容器均为窗体字段，避免在 InitializeComponent 中使用局部容器，
    ' 使 Visual Studio WinForms 设计器能够识别并维护控件层级。
    Friend rootLayout As TableLayoutPanel
    Friend topPanel As FlowLayoutPanel
    Friend leftLayout As TableLayoutPanel
    Friend commandButtonsPanel As FlowLayoutPanel
    Friend rightLayout As TableLayoutPanel
    Friend toolbarPanel As FlowLayoutPanel
    Friend definitionLayout As TableLayoutPanel
    Friend lblTemplate As Label
    Friend lblHelp As Label

    Public Sub New()

        ' 此调用是设计器所必需的。
        InitializeComponent()

        ' 在 InitializeComponent() 调用之后添加任何初始化。
        DisplayHelper.ApplyApplicationFont(Me)
    End Sub

    <DebuggerNonUserCode()>
    Protected Overrides Sub Dispose(disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    <DebuggerStepThrough()>
    Private Sub InitializeComponent()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(SCSIGUI))
        Me.rootLayout = New System.Windows.Forms.TableLayoutPanel()
        Me.topPanel = New System.Windows.Forms.FlowLayoutPanel()
        Me.lblTemplate = New System.Windows.Forms.Label()
        Me.cboTemplates = New System.Windows.Forms.ComboBox()
        Me.leftLayout = New System.Windows.Forms.TableLayoutPanel()
        Me.chkEdit = New System.Windows.Forms.CheckBox()
        Me.lstCommands = New System.Windows.Forms.ListBox()
        Me.commandButtonsPanel = New System.Windows.Forms.FlowLayoutPanel()
        Me.btnAdd = New System.Windows.Forms.Button()
        Me.btnDelete = New System.Windows.Forms.Button()
        Me.btnUp = New System.Windows.Forms.Button()
        Me.btnDown = New System.Windows.Forms.Button()
        Me.rightLayout = New System.Windows.Forms.TableLayoutPanel()
        Me.toolbarPanel = New System.Windows.Forms.FlowLayoutPanel()
        Me.btnSave = New System.Windows.Forms.Button()
        Me.lblHelp = New System.Windows.Forms.Label()
        Me.tabs = New System.Windows.Forms.TabControl()
        Me.tabValues = New System.Windows.Forms.TabPage()
        Me.pnlValues = New System.Windows.Forms.FlowLayoutPanel()
        Me.tabDefinition = New System.Windows.Forms.TabPage()
        Me.definitionLayout = New System.Windows.Forms.TableLayoutPanel()
        Me.tvElements = New System.Windows.Forms.TreeView()
        Me.pg = New System.Windows.Forms.PropertyGrid()
        Me.rootLayout.SuspendLayout()
        Me.topPanel.SuspendLayout()
        Me.leftLayout.SuspendLayout()
        Me.commandButtonsPanel.SuspendLayout()
        Me.rightLayout.SuspendLayout()
        Me.toolbarPanel.SuspendLayout()
        Me.tabs.SuspendLayout()
        Me.tabValues.SuspendLayout()
        Me.tabDefinition.SuspendLayout()
        Me.definitionLayout.SuspendLayout()
        Me.SuspendLayout()
        '
        'rootLayout
        '
        Me.rootLayout.ColumnCount = 2
        Me.rootLayout.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 257.0!))
        Me.rootLayout.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.rootLayout.Controls.Add(Me.topPanel, 0, 0)
        Me.rootLayout.Controls.Add(Me.leftLayout, 0, 1)
        Me.rootLayout.Controls.Add(Me.rightLayout, 1, 1)
        Me.rootLayout.Dock = System.Windows.Forms.DockStyle.Fill
        Me.rootLayout.Location = New System.Drawing.Point(0, 0)
        Me.rootLayout.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.rootLayout.Name = "rootLayout"
        Me.rootLayout.RowCount = 2
        Me.rootLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 34.0!))
        Me.rootLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.rootLayout.Size = New System.Drawing.Size(1058, 537)
        Me.rootLayout.TabIndex = 0
        '
        'topPanel
        '
        Me.rootLayout.SetColumnSpan(Me.topPanel, 2)
        Me.topPanel.Controls.Add(Me.lblTemplate)
        Me.topPanel.Controls.Add(Me.cboTemplates)
        Me.topPanel.Dock = System.Windows.Forms.DockStyle.Fill
        Me.topPanel.Location = New System.Drawing.Point(3, 2)
        Me.topPanel.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.topPanel.Name = "topPanel"
        Me.topPanel.Padding = New System.Windows.Forms.Padding(7, 6, 3, 3)
        Me.topPanel.Size = New System.Drawing.Size(1052, 30)
        Me.topPanel.TabIndex = 0
        Me.topPanel.WrapContents = False
        '
        'lblTemplate
        '
        Me.lblTemplate.AutoSize = True
        Me.lblTemplate.Location = New System.Drawing.Point(7, 11)
        Me.lblTemplate.Margin = New System.Windows.Forms.Padding(0, 5, 0, 0)
        Me.lblTemplate.Name = "lblTemplate"
        Me.lblTemplate.Size = New System.Drawing.Size(41, 12)
        Me.lblTemplate.TabIndex = 0
        Me.lblTemplate.Text = "模板："
        '
        'cboTemplates
        '
        Me.cboTemplates.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.cboTemplates.Location = New System.Drawing.Point(51, 6)
        Me.cboTemplates.Margin = New System.Windows.Forms.Padding(3, 0, 3, 2)
        Me.cboTemplates.Name = "cboTemplates"
        Me.cboTemplates.Size = New System.Drawing.Size(198, 20)
        Me.cboTemplates.TabIndex = 1
        '
        'leftLayout
        '
        Me.leftLayout.ColumnCount = 1
        Me.leftLayout.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.leftLayout.Controls.Add(Me.chkEdit, 0, 0)
        Me.leftLayout.Controls.Add(Me.lstCommands, 0, 1)
        Me.leftLayout.Controls.Add(Me.commandButtonsPanel, 0, 2)
        Me.leftLayout.Dock = System.Windows.Forms.DockStyle.Fill
        Me.leftLayout.Location = New System.Drawing.Point(3, 36)
        Me.leftLayout.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.leftLayout.Name = "leftLayout"
        Me.leftLayout.Padding = New System.Windows.Forms.Padding(7, 6, 7, 6)
        Me.leftLayout.RowCount = 3
        Me.leftLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 21.0!))
        Me.leftLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.leftLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 28.0!))
        Me.leftLayout.Size = New System.Drawing.Size(251, 499)
        Me.leftLayout.TabIndex = 1
        '
        'chkEdit
        '
        Me.chkEdit.AutoSize = True
        Me.chkEdit.Location = New System.Drawing.Point(10, 8)
        Me.chkEdit.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.chkEdit.Name = "chkEdit"
        Me.chkEdit.Size = New System.Drawing.Size(96, 16)
        Me.chkEdit.TabIndex = 0
        Me.chkEdit.Text = "编辑模板模式"
        Me.chkEdit.UseVisualStyleBackColor = True
        '
        'lstCommands
        '
        Me.lstCommands.Dock = System.Windows.Forms.DockStyle.Fill
        Me.lstCommands.FormattingEnabled = True
        Me.lstCommands.ItemHeight = 12
        Me.lstCommands.Location = New System.Drawing.Point(10, 29)
        Me.lstCommands.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.lstCommands.Name = "lstCommands"
        Me.lstCommands.Size = New System.Drawing.Size(231, 434)
        Me.lstCommands.TabIndex = 1
        '
        'commandButtonsPanel
        '
        Me.commandButtonsPanel.Controls.Add(Me.btnAdd)
        Me.commandButtonsPanel.Controls.Add(Me.btnDelete)
        Me.commandButtonsPanel.Controls.Add(Me.btnUp)
        Me.commandButtonsPanel.Controls.Add(Me.btnDown)
        Me.commandButtonsPanel.Dock = System.Windows.Forms.DockStyle.Fill
        Me.commandButtonsPanel.Location = New System.Drawing.Point(10, 467)
        Me.commandButtonsPanel.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.commandButtonsPanel.Name = "commandButtonsPanel"
        Me.commandButtonsPanel.Size = New System.Drawing.Size(231, 24)
        Me.commandButtonsPanel.TabIndex = 2
        Me.commandButtonsPanel.WrapContents = False
        '
        'btnAdd
        '
        Me.btnAdd.Location = New System.Drawing.Point(0, 2)
        Me.btnAdd.Margin = New System.Windows.Forms.Padding(0, 2, 3, 2)
        Me.btnAdd.Name = "btnAdd"
        Me.btnAdd.Size = New System.Drawing.Size(53, 20)
        Me.btnAdd.TabIndex = 0
        Me.btnAdd.Text = "新增"
        Me.btnAdd.UseVisualStyleBackColor = True
        '
        'btnDelete
        '
        Me.btnDelete.Location = New System.Drawing.Point(56, 2)
        Me.btnDelete.Margin = New System.Windows.Forms.Padding(0, 2, 3, 2)
        Me.btnDelete.Name = "btnDelete"
        Me.btnDelete.Size = New System.Drawing.Size(53, 20)
        Me.btnDelete.TabIndex = 1
        Me.btnDelete.Text = "删除"
        Me.btnDelete.UseVisualStyleBackColor = True
        '
        'btnUp
        '
        Me.btnUp.Location = New System.Drawing.Point(112, 2)
        Me.btnUp.Margin = New System.Windows.Forms.Padding(0, 2, 3, 2)
        Me.btnUp.Name = "btnUp"
        Me.btnUp.Size = New System.Drawing.Size(53, 20)
        Me.btnUp.TabIndex = 2
        Me.btnUp.Text = "上移"
        Me.btnUp.UseVisualStyleBackColor = True
        '
        'btnDown
        '
        Me.btnDown.Location = New System.Drawing.Point(168, 2)
        Me.btnDown.Margin = New System.Windows.Forms.Padding(0, 2, 3, 2)
        Me.btnDown.Name = "btnDown"
        Me.btnDown.Size = New System.Drawing.Size(53, 20)
        Me.btnDown.TabIndex = 3
        Me.btnDown.Text = "下移"
        Me.btnDown.UseVisualStyleBackColor = True
        '
        'rightLayout
        '
        Me.rightLayout.ColumnCount = 1
        Me.rightLayout.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.rightLayout.Controls.Add(Me.toolbarPanel, 0, 0)
        Me.rightLayout.Controls.Add(Me.tabs, 0, 1)
        Me.rightLayout.Dock = System.Windows.Forms.DockStyle.Fill
        Me.rightLayout.Location = New System.Drawing.Point(260, 36)
        Me.rightLayout.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.rightLayout.Name = "rightLayout"
        Me.rightLayout.Padding = New System.Windows.Forms.Padding(3, 0, 7, 6)
        Me.rightLayout.RowCount = 2
        Me.rightLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 28.0!))
        Me.rightLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.rightLayout.Size = New System.Drawing.Size(795, 499)
        Me.rightLayout.TabIndex = 2
        '
        'toolbarPanel
        '
        Me.toolbarPanel.Controls.Add(Me.btnSave)
        Me.toolbarPanel.Controls.Add(Me.lblHelp)
        Me.toolbarPanel.Dock = System.Windows.Forms.DockStyle.Fill
        Me.toolbarPanel.Location = New System.Drawing.Point(6, 2)
        Me.toolbarPanel.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.toolbarPanel.Name = "toolbarPanel"
        Me.toolbarPanel.Size = New System.Drawing.Size(779, 24)
        Me.toolbarPanel.TabIndex = 0
        Me.toolbarPanel.WrapContents = False
        '
        'btnSave
        '
        Me.btnSave.Location = New System.Drawing.Point(3, 2)
        Me.btnSave.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.btnSave.Name = "btnSave"
        Me.btnSave.Size = New System.Drawing.Size(77, 21)
        Me.btnSave.TabIndex = 0
        Me.btnSave.Text = "保存模板"
        Me.btnSave.UseVisualStyleBackColor = True
        '
        'lblHelp
        '
        Me.lblHelp.AutoSize = True
        Me.lblHelp.Location = New System.Drawing.Point(93, 6)
        Me.lblHelp.Margin = New System.Windows.Forms.Padding(10, 6, 0, 0)
        Me.lblHelp.Name = "lblHelp"
        Me.lblHelp.Size = New System.Drawing.Size(323, 12)
        Me.lblHelp.TabIndex = 1
        Me.lblHelp.Text = "选中元素后可查看/修改属性；运行模式下修改右侧参数值。"
        '
        'tabs
        '
        Me.tabs.Controls.Add(Me.tabValues)
        Me.tabs.Controls.Add(Me.tabDefinition)
        Me.tabs.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tabs.Location = New System.Drawing.Point(6, 30)
        Me.tabs.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.tabs.Name = "tabs"
        Me.tabs.SelectedIndex = 0
        Me.tabs.Size = New System.Drawing.Size(779, 461)
        Me.tabs.TabIndex = 1
        '
        'tabValues
        '
        Me.tabValues.Controls.Add(Me.pnlValues)
        Me.tabValues.Location = New System.Drawing.Point(4, 22)
        Me.tabValues.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.tabValues.Name = "tabValues"
        Me.tabValues.Padding = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.tabValues.Size = New System.Drawing.Size(771, 435)
        Me.tabValues.TabIndex = 0
        Me.tabValues.Text = "参数值"
        Me.tabValues.UseVisualStyleBackColor = True
        '
        'pnlValues
        '
        Me.pnlValues.AutoScroll = True
        Me.pnlValues.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pnlValues.FlowDirection = System.Windows.Forms.FlowDirection.TopDown
        Me.pnlValues.Location = New System.Drawing.Point(3, 2)
        Me.pnlValues.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.pnlValues.Name = "pnlValues"
        Me.pnlValues.Size = New System.Drawing.Size(765, 431)
        Me.pnlValues.TabIndex = 0
        Me.pnlValues.WrapContents = False
        '
        'tabDefinition
        '
        Me.tabDefinition.Controls.Add(Me.definitionLayout)
        Me.tabDefinition.Location = New System.Drawing.Point(4, 22)
        Me.tabDefinition.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.tabDefinition.Name = "tabDefinition"
        Me.tabDefinition.Padding = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.tabDefinition.Size = New System.Drawing.Size(163, 45)
        Me.tabDefinition.TabIndex = 1
        Me.tabDefinition.Text = "元素定义"
        Me.tabDefinition.UseVisualStyleBackColor = True
        '
        'definitionLayout
        '
        Me.definitionLayout.ColumnCount = 2
        Me.definitionLayout.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 48.0!))
        Me.definitionLayout.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 52.0!))
        Me.definitionLayout.Controls.Add(Me.tvElements, 0, 0)
        Me.definitionLayout.Controls.Add(Me.pg, 1, 0)
        Me.definitionLayout.Dock = System.Windows.Forms.DockStyle.Fill
        Me.definitionLayout.Location = New System.Drawing.Point(3, 2)
        Me.definitionLayout.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.definitionLayout.Name = "definitionLayout"
        Me.definitionLayout.RowCount = 1
        Me.definitionLayout.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.definitionLayout.Size = New System.Drawing.Size(157, 41)
        Me.definitionLayout.TabIndex = 0
        '
        'tvElements
        '
        Me.tvElements.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvElements.HideSelection = False
        Me.tvElements.Location = New System.Drawing.Point(3, 2)
        Me.tvElements.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.tvElements.Name = "tvElements"
        Me.tvElements.Size = New System.Drawing.Size(69, 37)
        Me.tvElements.TabIndex = 0
        '
        'pg
        '
        Me.pg.Dock = System.Windows.Forms.DockStyle.Fill
        Me.pg.Location = New System.Drawing.Point(78, 2)
        Me.pg.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.pg.Name = "pg"
        Me.pg.Size = New System.Drawing.Size(76, 37)
        Me.pg.TabIndex = 1
        '
        'SCSIGUI
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 12.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(1058, 537)
        Me.Controls.Add(Me.rootLayout)
        Me.Icon = CType(resources.GetObject("$this.Icon"), System.Drawing.Icon)
        Me.Margin = New System.Windows.Forms.Padding(3, 2, 3, 2)
        Me.MinimumSize = New System.Drawing.Size(817, 435)
        Me.Name = "SCSIGUI"
        Me.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen
        Me.Text = "SCSI 指令模板编辑器"
        Me.rootLayout.ResumeLayout(False)
        Me.topPanel.ResumeLayout(False)
        Me.topPanel.PerformLayout()
        Me.leftLayout.ResumeLayout(False)
        Me.leftLayout.PerformLayout()
        Me.commandButtonsPanel.ResumeLayout(False)
        Me.rightLayout.ResumeLayout(False)
        Me.toolbarPanel.ResumeLayout(False)
        Me.toolbarPanel.PerformLayout()
        Me.tabs.ResumeLayout(False)
        Me.tabValues.ResumeLayout(False)
        Me.tabDefinition.ResumeLayout(False)
        Me.definitionLayout.ResumeLayout(False)
        Me.ResumeLayout(False)

    End Sub
End Class
